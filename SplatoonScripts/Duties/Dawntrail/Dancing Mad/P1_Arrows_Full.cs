using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using ECommons;
using ECommons.Configuration;
using ECommons.GameFunctions;
using ECommons.Hooks;
using Splatoon.Serializables;
using Splatoon.SplatoonScripting;
using Splatoon.SplatoonScripting.Priority;

namespace SplatoonScriptsOfficial.Duties.Dawntrail.Dancing_Mad;

// Full three-stage solution for the arrow callout mechanic:
//   1) place two arrow-callout debuffs at fixed spots (in the correct order)
//   2) handle the poison ("double trouble trap") knockback
//   3) spread out to the final MT/ST/H1/H2/D1-D4 stack-out spots
public class P1_Arrows_Full : SplatoonScript
{
    #region Metadata

    public override Metadata? Metadata => new(1, "NightmareXIV");
    public override HashSet<uint>? ValidTerritories => [TerritoryDmad];

    #endregion

    #region Constant

    private const uint TerritoryDmad = 1363;

    private enum ArrowDir { Up, Down, Left, Right }

    // Each player receives two arrow-callout debuffs (~5s and ~8s, imprecise).
    // Multiple status IDs map to the same direction (two independent sources).
    private static readonly Dictionary<uint, ArrowDir> ArrowDirByStatus = new()
    {
        [4876] = ArrowDir.Up, [5079] = ArrowDir.Up,
        [4878] = ArrowDir.Right, [5081] = ArrowDir.Right,
        [4877] = ArrowDir.Down, [5080] = ArrowDir.Down,
        [4879] = ArrowDir.Left, [5082] = ArrowDir.Left,
    };

    // Same-direction pairs always resolve in this fixed order (first spot, then second),
    // regardless of which of the two debuffs has the lower remaining time.
    private static readonly Dictionary<ArrowDir, (Vector2 First, Vector2 Second)> SameDirSpots = new()
    {
        [ArrowDir.Up] = (new(88, 100), new(88, 106)),
        [ArrowDir.Down] = (new(112, 100), new(112, 94)),
        [ArrowDir.Left] = (new(100, 112), new(106, 112)),
        [ArrowDir.Right] = (new(100, 88), new(94, 88)),
    };

    // Mixed-direction pairs: each direction has its own fixed spot, but which one
    // resolves first depends on which debuff has the lower remaining time.
    private static readonly Dictionary<(ArrowDir, ArrowDir), Dictionary<ArrowDir, Vector2>> MixedDirSpots = new()
    {
        [(ArrowDir.Up, ArrowDir.Left)] = new() { [ArrowDir.Up] = new(88, 112), [ArrowDir.Left] = new(94, 112) },
        [(ArrowDir.Up, ArrowDir.Right)] = new() { [ArrowDir.Up] = new(88, 94), [ArrowDir.Right] = new(88, 88) },
        [(ArrowDir.Down, ArrowDir.Left)] = new() { [ArrowDir.Down] = new(112, 106), [ArrowDir.Left] = new(112, 112) },
        [(ArrowDir.Down, ArrowDir.Right)] = new() { [ArrowDir.Down] = new(112, 88), [ArrowDir.Right] = new(106, 88) },
    };

    // Poison ("Double Trouble Trap") debuff: knocks back nearby players when it resolves.
    // Same status as used by P1_DoubleTroubleTrap_AutoMarker.cs.
    private const uint PoisonStatus = 5078;
    private static readonly Vector2 PoisonedDpsSpot = new(108, 108);
    private static readonly Vector2 PoisonedTankSpot = new(92, 92);

    // Final stack-out spots, keyed by the precise role position assigned via the
    // in-game Priority Editor (Controller.RolePosition / RolePosition enum).
    private static readonly Dictionary<RolePosition, Vector2> FinalSpots = new()
    {
        [RolePosition.T1] = new(100, 92),
        [RolePosition.T2] = new(108, 100),
        [RolePosition.H1] = new(84, 100),
        [RolePosition.H2] = new(100, 116),
        [RolePosition.M1] = new(92, 100),
        [RolePosition.M2] = new(100, 108),
        [RolePosition.R1] = new(100, 84),
        [RolePosition.R2] = new(116, 100),
    };

    private const float ArenaFloorY = 0f;
    private const float KnockbackArrowExtension = 6f;
    private const float MinKnockbackDirectionLengthSq = 0.01f;
    private const float FinalSpreadDisplaySeconds = 15f;

    private const uint QueuedSpotColor = 0xC8A0A0A0;
    private const uint PoisonDestinationColor = 0xC800FF80;
    private const uint PoisonPartnerColor = 0xC8FFA000;
    private const uint KnockbackArrowColor = 0xC8FF3030;
    private const uint FinalSpotColor = 0xC840A0FF;

    #endregion

    #region Config

    private Config C => Controller.GetConfig<Config>();

    private sealed class Config : IEzConfig
    {
        // When enabled, the script draws every party member's connectors instead of
        // just the local player's - useful for callers who want an overview of the
        // whole raid's routes while the mechanic is resolving.
        public bool ShowAllPlayers;
    }

    #endregion

    #region State

    private enum Stage { Idle, PlacingArrows, HandlingPoison, FinalSpread }

    private Stage _stage = Stage.Idle;
    private long _lastPoisonSeenAtMs;

    // Remembers, per tracked player, which of the two arrow spots was resolved as
    // "first"/"second" so that once one debuff expires we keep pointing at the
    // correct remaining spot instead of re-resolving from a single data point.
    private readonly Dictionary<uint, (Vector2 First, Vector2 Second)> _arrowSpotCache = [];

    #endregion

    #region LifeCycle

    public override void OnSetup()
    {
        // Templates are cloned per tracked player at runtime (see GetOrCreateClone).
        // They start disabled; OnUpdate enables/positions only what the current stage needs.
    }

    public override void OnCombatStart() => ResetState();

    public override void OnReset() => ResetState();

    public override void OnDirectorUpdate(DirectorUpdateCategory category)
    {
        if (category.EqualsAny(DirectorUpdateCategory.Commence, DirectorUpdateCategory.Recommence, DirectorUpdateCategory.Wipe))
            ResetState();
    }

    public override void OnSettingsDraw()
    {
        ImGui.Checkbox("Show all party members' routes", ref C.ShowAllPlayers);
        ImGui.TextWrapped("When enabled, every connector/marker is drawn for the whole party " +
            "instead of just yourself - handy for callers checking everyone's routes at a glance.");

        ImGui.Spacing();
        ImGui.TextWrapped("Stage 3 (final stack-out spots) requires your precise role position " +
            "(MT/ST/H1/H2/D1-D4) to be configured once via the in-game Priority Editor. " +
            "Without it the final spot connector is simply not shown.");
    }

    public override void OnUpdate()
    {
        Controller.Hide();

        var me = Controller.BasePlayer;
        if (me == null) return;

        UpdateStage(me);

        switch (_stage)
        {
            case Stage.PlacingArrows:
                ApplyArrowDisplay();
                break;
            case Stage.HandlingPoison:
                ApplyPoisonDisplay();
                break;
            case Stage.FinalSpread:
                ApplyFinalSpreadDisplay();
                break;
        }
    }

    #endregion

    #region Stage transitions

    // Stages are re-derived every frame from currently observable debuffs rather than
    // from boss cast IDs - none of the relevant cast/ActionEffect IDs for this mechanic
    // are currently known in this repository. This keeps the script self-correcting:
    // it always reflects what's actually on the player right now.
    private void UpdateStage(IPlayerCharacter me)
    {
        var myArrowsActive = CountArrowDebuffs(me) > 0;
        var anyonePoisonedNow = Controller.GetPartyMembers().Any(HasPoison);

        if (anyonePoisonedNow)
            _lastPoisonSeenAtMs = Environment.TickCount64;

        if (myArrowsActive)
        {
            _stage = Stage.PlacingArrows;
        }
        else if (anyonePoisonedNow)
        {
            _stage = Stage.HandlingPoison;
        }
        else if (_lastPoisonSeenAtMs > 0 &&
                 Environment.TickCount64 - _lastPoisonSeenAtMs <= (long)(FinalSpreadDisplaySeconds * 1000))
        {
            _stage = Stage.FinalSpread;
        }
        else
        {
            _stage = Stage.Idle;
            _arrowSpotCache.Clear();
        }
    }

    private void ResetState()
    {
        _stage = Stage.Idle;
        _lastPoisonSeenAtMs = 0;
        _arrowSpotCache.Clear();
    }

    #endregion

    #region Stage 1: arrow callouts

    private void ApplyArrowDisplay()
    {
        var index = 0;
        foreach (var player in GetDisplayTargets())
        {
            if (!TryResolveArrowSpots(player, out var first, out var second))
                continue;

            if (first.HasValue)
                ShowDestination("ArrowFirst", index, player, first.Value, Controller.AttentionColor, "Place now", tetherWhenSelf: true);

            if (second.HasValue)
                ShowDestination("ArrowSecond", index, player, second.Value, QueuedSpotColor, "Place next", tetherWhenSelf: false);

            index++;
        }
    }

    // Resolves the spot(s) a player needs to place their arrow callouts at, honoring:
    //   - same-direction pairs always resolve in a fixed order
    //   - mixed-direction pairs resolve in order of remaining debuff time (lower first)
    // Once a pair has been resolved it's cached, so when only one debuff remains we keep
    // pointing at the correct remembered spot instead of guessing from a single direction.
    private bool TryResolveArrowSpots(IPlayerCharacter pc, out Vector2? first, out Vector2? second)
    {
        first = null;
        second = null;

        var active = pc.StatusList
            .Where(s => s.RemainingTime > 0.5f && ArrowDirByStatus.ContainsKey(s.StatusId))
            .Select(s => (Dir: ArrowDirByStatus[s.StatusId], s.RemainingTime))
            .ToArray();

        if (active.Length == 0)
        {
            _arrowSpotCache.Remove(pc.EntityId);
            return false;
        }

        if (active.Length >= 2)
        {
            var a = active[0];
            var b = active[1];
            (Vector2 First, Vector2 Second)? resolved = null;

            if (a.Dir == b.Dir)
            {
                resolved = SameDirSpots[a.Dir];
            }
            else if (MixedDirSpots.TryGetValue(NormalizeDirPair(a.Dir, b.Dir), out var spots))
            {
                var firstDir = a.RemainingTime <= b.RemainingTime ? a.Dir : b.Dir;
                var secondDir = firstDir == a.Dir ? b.Dir : a.Dir;
                resolved = (spots[firstDir], spots[secondDir]);
            }

            if (resolved is { } pair)
            {
                _arrowSpotCache[pc.EntityId] = pair;
                first = pair.First;
                second = pair.Second;
                return true;
            }

            return false;
        }

        // Only one debuff remains: trust the cached pairing from when we first saw both,
        // and show whichever spot hasn't been placed yet.
        if (_arrowSpotCache.TryGetValue(pc.EntityId, out var cached))
        {
            first = cached.Second;
            return true;
        }

        return false;
    }

    private static (ArrowDir, ArrowDir) NormalizeDirPair(ArrowDir a, ArrowDir b)
        => a <= b ? (a, b) : (b, a);

    private int CountArrowDebuffs(IPlayerCharacter pc)
        => pc.StatusList.Count(s => s.RemainingTime > 0.5f && ArrowDirByStatus.ContainsKey(s.StatusId));

    #endregion

    #region Stage 2: poison handling

    private bool HasPoison(IPlayerCharacter pc)
        => pc.StatusList.Any(s => s.StatusId == PoisonStatus && s.RemainingTime > 0.1f);

    private void ApplyPoisonDisplay()
    {
        var targets = GetDisplayTargets().ToArray();
        var partyMembers = Controller.GetPartyMembers().ToArray();

        var poisonedTankGroup = partyMembers.FirstOrDefault(p => IsTankGroup(p) && HasPoison(p));
        var poisonedDps = partyMembers.FirstOrDefault(p => !IsTankGroup(p) && HasPoison(p));

        var index = 0;
        foreach (var player in targets)
        {
            var isTankGroup = IsTankGroup(player);
            var poisoned = HasPoison(player);
            var spot = isTankGroup ? PoisonedTankSpot : PoisonedDpsSpot;

            if (poisoned)
            {
                ShowDestination("PoisonDestination", index, player, spot, PoisonDestinationColor, "Poison stack spot", tetherWhenSelf: true);
            }
            else
            {
                var partner = isTankGroup ? poisonedTankGroup : poisonedDps;
                if (partner != null && partner.EntityId != player.EntityId)
                {
                    ShowPlayerLink("PoisonPartnerLink", index, player, partner, PoisonPartnerColor);
                    ShowKnockbackDirection("PoisonKnockback", index, partner, player);
                }
            }

            index++;
        }
    }

    // Healers are grouped with tanks for this mechanic (per user confirmation):
    // poisoned-tank pairing/knockback also applies to healers.
    private static bool IsTankGroup(IPlayerCharacter pc)
        => pc.GetRole() is CombatRole.Tank or CombatRole.Healer;

    #endregion

    #region Stage 3: final spread

    private void ApplyFinalSpreadDisplay()
    {
        var index = 0;
        foreach (var player in GetDisplayTargets())
        {
            if (!TryGetFinalSpot(player, out var spot))
                continue;

            ShowDestination("FinalSpot", index, player, spot, FinalSpotColor, "Final spot", tetherWhenSelf: true);
            index++;
        }
    }

    // Stage 3 needs the precise MT/ST/H1/H2/D1-D4 assignment, which can only come from
    // the user-configured Priority system (see Controller.RolePosition and the appendix
    // in AI_DEVELOPMENT_GUIDE.md). If a player hasn't configured it, we simply don't draw
    // anything for them rather than guessing.
    private bool TryGetFinalSpot(IPlayerCharacter pc, out Vector2 spot)
    {
        spot = default;

        var role = pc.EntityId == Controller.BasePlayer.EntityId
            ? Controller.RolePosition
            : RolePosition.Not_Selected;

        if (role == RolePosition.Not_Selected || !FinalSpots.TryGetValue(role, out spot))
            return false;

        return true;
    }

    #endregion

    #region Display helpers

    private IEnumerable<IPlayerCharacter> GetDisplayTargets()
    {
        if (C.ShowAllPlayers)
            return Controller.GetPartyMembers();

        var me = Controller.BasePlayer;
        return me == null ? [] : [me];
    }

    // Connects `player` to a fixed-coordinate `destination`. For the local player this
    // uses a tethered circle marker (the `tether` flag always draws a line to the local
    // viewer, matching the convention from the original P1_Arrows.cs); for other party
    // members - only relevant in "show all players" mode - it draws an explicit line that
    // follows their live position, since `tether` cannot connect to a non-local actor.
    private void ShowDestination(string baseName, int index, IPlayerCharacter player, Vector2 destination, uint color, string label, bool tetherWhenSelf)
    {
        if (player.EntityId == Controller.BasePlayer.EntityId)
        {
            var marker = GetOrCreateClone($"{baseName}Self", 0, () => new Element(0)
            {
                radius = 0.5f,
                Filled = false,
                thicc = 4f,
                overlayText = "$ELEMENT",
            });
            marker.Enabled = true;
            marker.color = color;
            marker.tether = tetherWhenSelf;
            marker.overlayText = label;
            marker.SetRefPosition(ToWorldPosition(destination));
        }
        else
        {
            var line = GetOrCreateClone($"{baseName}Other", index, () => new Element(2)
            {
                radius = 0f,
                thicc = 4f,
                LineEndB = LineEnd.Arrow,
            });
            line.Enabled = true;
            line.color = color;
            line.overlayText = label;
            line.SetRefPosition(player.Position);
            line.SetOffPosition(ToWorldPosition(destination));
        }
    }

    // Draws a live connector between two players (e.g. "you" and "the poisoned tank"),
    // following both endpoints' positions every frame.
    private void ShowPlayerLink(string baseName, int index, IPlayerCharacter from, IPlayerCharacter to, uint color)
    {
        var line = GetOrCreateClone(baseName, index, () => new Element(2) { radius = 0f, thicc = 4f });
        line.Enabled = true;
        line.color = color;
        line.SetRefPosition(from.Position);
        line.SetOffPosition(to.Position);
    }

    // Shows the direction `from` will be knocked back in once `source`'s poison resolves
    // (push direction = away from the poison source, computed in the XZ plane).
    // Mirrors the approach used by P1_GravenImage_Reminder.ApplyKnockbackLine.
    private void ShowKnockbackDirection(string baseName, int index, IPlayerCharacter source, IPlayerCharacter from)
    {
        var deltaXz = new Vector2(from.Position.X - source.Position.X, from.Position.Z - source.Position.Z);
        if (deltaXz.LengthSquared() < MinKnockbackDirectionLengthSq)
            return;

        var directionXz = Vector2.Normalize(deltaXz);
        var tip = new Vector3(
            from.Position.X + directionXz.X * KnockbackArrowExtension,
            from.Position.Y,
            from.Position.Z + directionXz.Y * KnockbackArrowExtension);

        var arrow = GetOrCreateClone(baseName, index, () => new Element(2)
        {
            radius = 0f,
            thicc = 6f,
            LineEndA = LineEnd.Arrow,
            color = KnockbackArrowColor,
        });
        arrow.Enabled = true;
        arrow.SetRefPosition(tip);
        arrow.SetOffPosition(from.Position);
    }

    // Lazily creates (or reuses) a per-index clone of a template element. Clones are
    // registered once and then repositioned/recolored every frame, avoiding repeated
    // (de)registration churn while still letting "show all players" mode track up to
    // 8 independent connectors per category.
    private Element GetOrCreateClone(string templateName, int index, Func<Element> factory)
    {
        var name = $"{templateName}{index}";
        if (Controller.TryGetElementByName(name, out var existing))
            return existing;

        var element = factory();
        element.Name = name;
        Controller.RegisterElement(name, element);
        return element;
    }

    private static Vector3 ToWorldPosition(Vector2 xz) => new(xz.X, ArenaFloorY, xz.Y);

    #endregion
}
