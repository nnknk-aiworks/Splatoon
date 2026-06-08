using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using ECommons.CircularBuffers;
using ECommons.DalamudServices;
using ECommons.GameFunctions;
using ECommons.Hooks.ActionEffectTypes;
using ECommons.ImGuiMethods;
using ECommons.MathHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using Splatoon;
using Splatoon.SplatoonScripting;
using Splatoon.SplatoonScripting.Priority;
using Splatoon.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SplatoonScriptsOfficial.Duties.Dawntrail.Dancing_Mad;

// Implements a custom, opinionated P2 Forsaken (Missing) strategy built around
// fixed role pairs: (T1,H1) (T2,H2) (M1,R1) (M2,R2). Pair membership decides
// which group ("A" or "B") is responsible for towers on a given wave; within
// that, fixed positioning rules (described by the user) decide where each
// player should stand. This script is independent of the existing Forsaken
// scripts in this folder and does not modify them.
public unsafe class P2_Forsaken_FixedRolePairs : SplatoonScript<P2_Forsaken_FixedRolePairs.Config>
{
    public override Metadata Metadata { get; } = new(1, "nnknk-aiworks");
    public override HashSet<uint>? ValidTerritories { get; } = [1363];

    public const uint EffectSpread = 5085;
    public const uint EffectStack = 5084;
    public const uint EffectFan = 5086;

    public const uint DebuffSpellsTrouble = 5083;

    public const uint ActionTowerExplode = 47806;
    public const uint CastFuturesEnd = 47826;
    public const uint CastPastsEnd = 47827;
    public static readonly uint[] CastAllThingsEnding = [47836, 47837];

    private const uint KefkaDataId = 9020;

    private static readonly Vector3 ArenaCenter = new(100f, 0f, 100f);

    // Fixed role pairing as described by the user: (MT,H1)(ST,H2)(D1,D3)(D2,D4) == (T1,H1)(T2,H2)(M1,R1)(M2,R2)
    private static readonly Dictionary<RolePosition, RolePosition> PartnerOf = new()
    {
        [RolePosition.T1] = RolePosition.H1,
        [RolePosition.H1] = RolePosition.T1,
        [RolePosition.T2] = RolePosition.H2,
        [RolePosition.H2] = RolePosition.T2,
        [RolePosition.M1] = RolePosition.R1,
        [RolePosition.R1] = RolePosition.M1,
        [RolePosition.M2] = RolePosition.R2,
        [RolePosition.R2] = RolePosition.M2,
    };

    private static readonly RolePosition[] AllRoles =
    [
        RolePosition.T1, RolePosition.T2, RolePosition.H1, RolePosition.H2,
        RolePosition.M1, RolePosition.M2, RolePosition.R1, RolePosition.R2,
    ];

    // Where each idle role bait-spreads while guiding the boss's "nearest 4" 5m AoE on even waves.
    // Every role gets a distinct compass angle so any subset of 4 idle players never overlaps.
    private static readonly Dictionary<RolePosition, float> IdleBaitAngleByRole = new()
    {
        [RolePosition.T1] = 0f,
        [RolePosition.M1] = 45f,
        [RolePosition.T2] = 90f,
        [RolePosition.M2] = 135f,
        [RolePosition.H1] = 180f,
        [RolePosition.R1] = 225f,
        [RolePosition.H2] = 270f,
        [RolePosition.R2] = 315f,
    };

    uint TowerCount = 0;
    uint SequenceCount => TowerCount / 2 + 1;
    bool? StoredAoe = null;
    bool? IsGroupA = null;

    CircularArray<uint> ActiveMapEffects = new(2);

    Dictionary<uint, Vector2> MapEffect2TowerPos
    {
        get
        {
            if(field == null)
            {
                field = [];
                for(uint i = 1; i <= 8; i++)
                {
                    field[i] = MathHelper.RotateWorldPoint(new(100, 0, 100), (45f * (i - 1)).DegreesToRadians(), new(100, 0, 92)).ToVector2();
                }
                for(uint i = 9; i <= 16; i++)
                {
                    field[i] = MathHelper.RotateWorldPoint(new(100, 0, 100), (45f * (i - 1)).DegreesToRadians(), new(100, 0, 88)).ToVector2();
                }
            }
            return field;
        }
    }

    public override void OnSetup()
    {
        Controller.RegisterElement("Destination", new Element(0)
        {
            Enabled = false,
            radius = 0.6f,
            Donut = 0.3f,
            thicc = 4f,
            fillIntensity = 0.5f,
            color = 0xC800FFFF,
            tether = true,
            overlayBGColor = 0xC8000000,
            overlayTextColor = 0xFFFFFFFF,
            overlayVOffset = 2.0f,
            overlayFScale = 1.2f,
            overlayText = ""
        });

        Controller.RegisterElement("Bait", new Element(0)
        {
            Enabled = false,
            radius = 0.5f,
            color = 3357277952,
            Filled = false,
            fillIntensity = 0.5f,
            thicc = 5f,
            tether = true,
            overlayText = "Past/Future Bait"
        });

        for(int i = 0; i < 2; i++)
        {
            Controller.RegisterElementFromCode($"VStack{i}", """
                {"Name":"Stack","type":1,"radius":5.0,"Donut":0.5,"color":3357277952,"fillIntensity":0.5,"overlayTextColor":4278779648,"overlayVOffset":1.2,"overlayText":"","refActorComparisonType":2}
                """);
            Controller.RegisterElementFromCode($"VSpread{i}", """
                {"Name":"Spread","type":1,"radius":5.0,"fillIntensity":0.5,"Donut":0.5,"overlayTextColor":4278190335,"overlayVOffset":1.2,"overlayText":"","refActorComparisonType":2}
                """);
            Controller.RegisterElementFromCode($"VFan{i}", """
                {"Name":"Cone","type":4,"radius":40.0,"coneAngleMin":-45,"coneAngleMax":45,"fillIntensity":0.3,"overlayTextColor":4294180608,"overlayVOffset":1.2,"overlayText":"","thicc":8.0,"includeRotation":true,"FaceMe":true,"refActorComparisonType":2}
                """);
        }

        for(int i = 0; i < 4; i++)
        {
            Controller.RegisterElementFromCode($"BossNear{i}", """
                {"Name":"Nearest4","type":1,"radius":5.0,"Donut":0.5,"color":3372220160,"fillIntensity":0.4,"overlayTextColor":4294180608,"overlayVOffset":1.2,"overlayText":"Nearest-4 AoE","refActorComparisonType":2}
                """);
        }
    }

    public override void OnActionEffectEvent(ActionEffectSet set)
    {
        if(set.Action == null) return;

        if(set.Action.Value.RowId == ActionTowerExplode) TowerCount++;
        if(set.Action.Value.RowId == CastFuturesEnd) StoredAoe = true;
        if(set.Action.Value.RowId == CastPastsEnd) StoredAoe = false;
    }

    public override unsafe void OnStartingCast(uint sourceId, PacketActorCast* packet)
    {
        if(packet->ActionType == (int)ActionType.Action && CastAllThingsEnding.Contains(packet->ActionID))
        {
            StoredAoe = null;
        }
    }

    public override void OnMapEffect(uint position, ushort data1, ushort data2)
    {
        if(MapEffect2TowerPos.ContainsKey(position) && data1 == 1)
        {
            ActiveMapEffects.Push(position);
        }
    }

    public override void OnReset()
    {
        TowerCount = 0;
        ActiveMapEffects = new(2);
        StoredAoe = null;
        IsGroupA = null;
    }

    public override void OnUpdate()
    {
        Controller.Hide();

        if(!Controller.GetPartyMembers().Any(x => x.StatusList.Any(s => s.StatusId == DebuffSpellsTrouble)))
            return;

        var partner = GetPartner();
        UpdateGroupDetermination(partner);

        UpdateBaitElement();
        UpdateDamagePreviewElements();
        UpdateBossNearestPreviewElements();

        if(TryGetLeftRightTowers(out var leftPos, out var rightPos))
        {
            UpdateDestination(BuildRoleLookup(), leftPos, rightPos);
        }
        else
        {
            ClearDestination();
        }
    }

    // --- Role / partner resolution -------------------------------------------------

    private static IPlayerCharacter? ResolvePlayerByRole(RolePosition role)
    {
        if(role == RolePosition.Not_Selected) return null;
        var jp = new JobbedPlayer { Role = role };
        if(jp.IsInParty(true, out var member) && member.IGameObject is IPlayerCharacter pc) return pc;
        return null;
    }

    private IPlayerCharacter? GetPartner()
    {
        var myRole = Controller.RolePosition;
        if(myRole == RolePosition.Not_Selected || !PartnerOf.TryGetValue(myRole, out var partnerRole)) return null;
        return ResolvePlayerByRole(partnerRole);
    }

    private Dictionary<uint, RolePosition> BuildRoleLookup()
    {
        var map = new Dictionary<uint, RolePosition>();
        foreach(var role in AllRoles)
        {
            var pc = ResolvePlayerByRole(role);
            if(pc != null) map[pc.ObjectId] = role;
        }
        return map;
    }

    // Priority order described by the user for picking who takes the left tower / who
    // is "first" within a same-debuff pair: Healer > Tank > Melee DPS (D1/D2) > Ranged DPS (D3/D4).
    private static int RolePriorityRank(RolePosition role) => role switch
    {
        RolePosition.H1 => 0,
        RolePosition.H2 => 1,
        RolePosition.T1 => 2,
        RolePosition.T2 => 3,
        RolePosition.M1 => 4,
        RolePosition.M2 => 5,
        RolePosition.R1 => 6,
        RolePosition.R2 => 7,
        _ => int.MaxValue
    };

    private static int RankOf(IPlayerCharacter player, Dictionary<uint, RolePosition> roleLookup)
        => roleLookup.TryGetValue(player.ObjectId, out var role) ? RolePriorityRank(role) : int.MaxValue;

    // --- Group A/B determination ----------------------------------------------------

    // On wave 1, whichever pair member is hit by Stack first marks the whole pair as
    // Group A (handles waves 1/2/3/8); if neither gets Stack, the pair is Group B
    // (handles waves 4/5/6/7).
    private void UpdateGroupDetermination(IPlayerCharacter? partner)
    {
        if(IsGroupA != null || SequenceCount != 1) return;

        if(IsStack(BasePlayer) || (partner != null && IsStack(partner)))
        {
            IsGroupA = true;
        }
        else if(partner != null && HasMarker(BasePlayer) && HasMarker(partner))
        {
            IsGroupA = false;
        }
    }

    private bool? IsMyGroupTakingTowerThisWave => IsGroupA switch
    {
        true => SequenceCount.EqualsAny<uint>(1, 2, 3, 8),
        false => SequenceCount.EqualsAny<uint>(4, 5, 6, 7),
        null => null
    };

    private bool IsStack(IPlayerCharacter x) => x.StatusList.Any(s => s.StatusId == EffectStack);
    private bool IsSpread(IPlayerCharacter x) => x.StatusList.Any(s => s.StatusId == EffectSpread);
    private bool IsFan(IPlayerCharacter x) => x.StatusList.Any(s => s.StatusId == EffectFan);
    private bool HasMarker(IPlayerCharacter x) => IsStack(x) || IsSpread(x) || IsFan(x);

    // --- Left / right tower determination --------------------------------------------

    // "Left" = clockwise side, "Right" = counter-clockwise side, when facing the boss
    // (per the user's strategy description). The two active towers always sit 90 degrees
    // (2 index steps) apart in MapEffect2TowerPos. Whether increasing index corresponds
    // to clockwise or counter-clockwise as seen in-game needs empirical confirmation -
    // flip C.ClockwiseIndexIncreases in Settings if Left/Right come out swapped.
    private bool TryGetLeftRightTowers(out Vector2 leftPos, out Vector2 rightPos)
    {
        leftPos = default;
        rightPos = default;
        if(ActiveMapEffects.Count() < 2) return false;

        var eff1 = ActiveMapEffects[0];
        var eff2 = ActiveMapEffects[1];
        if(!MapEffect2TowerPos.TryGetValue(eff1, out var pos1) || !MapEffect2TowerPos.TryGetValue(eff2, out var pos2))
            return false;

        var diff = (((int)eff2 - (int)eff1) % 8 + 8) % 8;
        var (clockwisePos, counterClockwisePos) = diff == 2 ? (pos2, pos1) : (pos1, pos2);

        (leftPos, rightPos) = C.ClockwiseIndexIncreases
            ? (clockwisePos, counterClockwisePos)
            : (counterClockwisePos, clockwisePos);
        return true;
    }

    // --- Standing position resolution -------------------------------------------------

    // Resolves a world position relative to a tower using a local "radial / tangential"
    // frame: radial points from the arena center toward the tower (positive offset = away
    // from the boss / "back", negative = toward the boss / "front"); tangential is
    // perpendicular to radial and is used to spread two players sideways at the same tower.
    private Vector3 ResolveStandPosition(StandRule rule, Vector2 leftPos, Vector2 rightPos, int tangentialSign)
    {
        var towerPos = rule.Tower == TowerSide.Left ? leftPos : rightPos;
        var center = new Vector2(ArenaCenter.X, ArenaCenter.Z);
        var radial = Vector2.Normalize(towerPos - center);
        var tangential = new Vector2(-radial.Y, radial.X);
        var result = towerPos + radial * rule.RadialOffset + tangential * (rule.TangentialOffset * tangentialSign);
        return new Vector3(result.X, ArenaCenter.Y, result.Y);
    }

    // Spreads idle players around the boss (arena center) on even waves while baiting the
    // "nearest 4 to boss" 5m AoE. Each role gets its own fixed compass angle so that any
    // subset of 4 idle players ends up at distinct, spread-out positions.
    private Vector3 ResolveBaitSpreadPosition(RolePosition myRole)
    {
        var angleDeg = IdleBaitAngleByRole.GetValueOrDefault(myRole, 0f);
        var rad = angleDeg * MathF.PI / 180f;
        var offset = new Vector2(MathF.Sin(rad), -MathF.Cos(rad)) * C.BaitSpreadRadius;
        var center = new Vector2(ArenaCenter.X, ArenaCenter.Z);
        var result = center + offset;
        return new Vector3(result.X, ArenaCenter.Y, result.Y);
    }

    private void UpdateDestination(Dictionary<uint, RolePosition> roleLookup, Vector2 leftPos, Vector2 rightPos)
    {
        if(IsGroupA == null)
        {
            ClearDestination();
            return;
        }

        var isOdd = SequenceCount % 2 == 1;
        var taking = IsMyGroupTakingTowerThisWave == true;

        Vector3? destination = null;
        var label = "";

        if(taking)
        {
            if(isOdd)
            {
                // Odd wave taking group: 2 Stack (one tower each, by priority) + 1 Fan (back of left tower) + 1 Spread (back of right tower)
                if(IsStack(BasePlayer))
                {
                    var holders = Controller.GetPartyMembers().Where(IsStack).ToList();
                    if(holders.Count == 2)
                    {
                        var ordered = holders.OrderBy(x => RankOf(x, roleLookup)).ToList();
                        var iAmHigherPriority = ordered[0].AddressEquals(BasePlayer);
                        destination = (iAmHigherPriority ? leftPos : rightPos).ToVector3();
                        label = iAmHigherPriority ? "Stack -> Left Tower" : "Stack -> Right Tower";
                    }
                }
                else if(IsFan(BasePlayer))
                {
                    destination = ResolveStandPosition(C.OddFanBack, leftPos, rightPos, 1);
                    label = "Fan -> back of Left Tower";
                }
                else if(IsSpread(BasePlayer))
                {
                    destination = ResolveStandPosition(C.OddSpreadBack, leftPos, rightPos, 1);
                    label = "Spread -> back of Right Tower";
                }
            }
            else
            {
                // Even wave taking group: 2 Stack (back of left tower, mutual guide) + 2 Spread (right tower, side by side)
                if(IsStack(BasePlayer))
                {
                    var holders = Controller.GetPartyMembers().Where(IsStack).ToList();
                    if(holders.Count == 2)
                    {
                        var ordered = holders.OrderBy(x => RankOf(x, roleLookup)).ToList();
                        var sign = ordered[0].AddressEquals(BasePlayer) ? 1 : -1;
                        destination = ResolveStandPosition(C.EvenStackBack, leftPos, rightPos, sign);
                        label = "Stack -> back of Left Tower (mutual guide)";
                    }
                }
                else if(IsSpread(BasePlayer))
                {
                    var holders = Controller.GetPartyMembers().Where(IsSpread).ToList();
                    if(holders.Count == 2)
                    {
                        var ordered = holders.OrderBy(x => RankOf(x, roleLookup)).ToList();
                        var sign = ordered[0].AddressEquals(BasePlayer) ? 1 : -1;
                        destination = ResolveStandPosition(C.EvenSpreadCenter, leftPos, rightPos, sign);
                        label = "Spread -> Right Tower (side by side)";
                    }
                }
            }
        }
        else
        {
            if(isOdd)
            {
                // Odd wave idle group: T soaks Stack splash at front of left tower, H baits Fan at back of left tower, both DPS soak Stack splash at front of right tower
                var role = BasePlayer.GetRole();
                if(role == CombatRole.Tank)
                {
                    destination = ResolveStandPosition(C.OddIdleTankFront, leftPos, rightPos, 1);
                    label = "Idle Tank -> front of Left Tower";
                }
                else if(role == CombatRole.Healer)
                {
                    destination = ResolveStandPosition(C.OddIdleHealerBack, leftPos, rightPos, 1);
                    label = "Idle Healer -> back of Left Tower (bait Fan)";
                }
                else
                {
                    destination = ResolveStandPosition(C.OddIdleDpsFront, leftPos, rightPos, 1);
                    label = "Idle DPS -> front of Right Tower";
                }
            }
            else
            {
                // Even wave idle group: stand outside the towers near the boss, spread out, baiting the boss's "nearest 4" 5m AoE
                destination = ResolveBaitSpreadPosition(Controller.RolePosition);
                label = "Idle -> spread near boss, bait nearest-4 AoE";
            }
        }

        if(destination != null) SetDestination(destination.Value, label);
        else ClearDestination();
    }

    private void SetDestination(Vector3 position, string label)
    {
        if(Controller.TryGetElementByName("Destination", out var e))
        {
            e.Enabled = true;
            e.RefPosition = position;
            e.overlayText = label;
            e.color = Controller.AttentionColor;
        }
    }

    private void ClearDestination()
    {
        if(Controller.TryGetElementByName("Destination", out var e)) e.Enabled = false;
    }

    // --- Past / Future bait (midpoint / mirror, reused from P2_Forsaken_Fixed_Partner) ----

    private void UpdateBaitElement()
    {
        if(!Controller.TryGetElementByName("Bait", out var e)) return;

        if(StoredAoe == null || ActiveMapEffects.Count() != 2)
        {
            e.Enabled = false;
            return;
        }

        Vector2 pos;
        if(!StoredAoe.Value)
        {
            // Past: midpoint of the two currently active towers
            pos = (MapEffect2TowerPos[ActiveMapEffects[0]] + MapEffect2TowerPos[ActiveMapEffects[1]]) / 2;
        }
        else
        {
            // Future: midpoint of the towers mirrored across the arena center (+4 steps)
            var i1 = (((ActiveMapEffects[0] - 1) + 4) % 8) + 1;
            var i2 = (((ActiveMapEffects[1] - 1) + 4) % 8) + 1;
            pos = (MapEffect2TowerPos[i1] + MapEffect2TowerPos[i2]) / 2;
        }
        e.Enabled = true;
        e.RefPosition = pos.ToVector3();
        e.color = Controller.AttentionColor;
    }

    // --- Damage range previews --------------------------------------------------------

    private void UpdateDamagePreviewElements()
    {
        if(!C.ShowDamagePreviews) return;

        foreach(var x in Controller.GetPartyMembers())
        {
            if(IsStack(x)) ShowDebuffPreview("VStack", x.ObjectId);
            if(IsSpread(x)) ShowDebuffPreview("VSpread", x.ObjectId);
            if(IsFan(x)) ShowFanPreview(x);
        }
    }

    private void ShowDebuffPreview(string baseName, uint objectId)
    {
        for(var i = 0; i < 2; i++)
        {
            if(Controller.TryGetElementByName($"{baseName}{i}", out var e) && !e.Enabled)
            {
                e.Enabled = true;
                e.refActorObjectID = objectId;
                return;
            }
        }
    }

    // The Fan cone always orients toward the caster's nearest other player - reproduce
    // that here (same approach as P2_Forsaken.cs) so the preview matches the real AoE.
    private void ShowFanPreview(IPlayerCharacter source)
    {
        var nearest = Svc.Objects.OfType<IPlayerCharacter>()
            .Where(x => x.EntityId != source.EntityId)
            .OrderBy(x => Vector3.DistanceSquared(x.Position, source.Position))
            .FirstOrDefault();
        if(nearest == null) return;

        for(var i = 0; i < 2; i++)
        {
            if(Controller.TryGetElementByName($"VFan{i}", out var e) && !e.Enabled)
            {
                e.Enabled = true;
                e.refActorComparisonType = 2;
                e.refActorObjectID = source.EntityId;
                e.faceplayer = GetPlayerOrder(nearest);
                return;
            }
        }
    }

    private static string GetPlayerOrder(IPlayerCharacter c)
    {
        for(var i = 1; i <= 8; i++)
        {
            if((nint)FakePronoun.Resolve($"<{i}>") == c.Address)
                return $"<{i}>";
        }
        throw new Exception("Could not determine player order");
    }

    private void UpdateBossNearestPreviewElements()
    {
        if(!C.ShowBossNearestPreview) return;

        var boss = Svc.Objects.OfType<IBattleNpc>().FirstOrDefault(npc => npc.DataId == KefkaDataId);
        if(boss == null) return;

        var nearest = Controller.GetPartyMembers()
            .OrderBy(x => Vector3.DistanceSquared(x.Position, boss.Position))
            .Take(4)
            .ToList();

        for(var i = 0; i < nearest.Count; i++)
        {
            if(Controller.TryGetElementByName($"BossNear{i}", out var e))
            {
                e.Enabled = true;
                e.refActorObjectID = nearest[i].ObjectId;
            }
        }
    }

    // --- Settings ----------------------------------------------------------------------

    public override void OnSettingsDraw()
    {
        ImGuiEx.TextWrapped("""
            Strategy assumptions (fixed role pairs: T1<->H1, T2<->H2, M1<->R1, M2<->R2):
            - Whichever pair member is hit by Stack first on wave 1 marks that pair as Group A
              (handles tower waves 1/2/3/8); otherwise the pair is Group B (handles waves 4/5/6/7).
            - "Left" tower = clockwise side when facing the boss, "Right" = counter-clockwise side.
            If Left/Right destinations look swapped in-game, flip "Clockwise follows increasing
            tower index" below.
            """);

        ImGui.Checkbox("Clockwise follows increasing tower index", ref C.ClockwiseIndexIncreases);
        ImGui.Checkbox("Show Stack/Spread/Fan damage range previews", ref C.ShowDamagePreviews);
        ImGui.Checkbox("Show boss-nearest-4 AoE preview", ref C.ShowBossNearestPreview);
        ImGui.SetNextItemWidth(180f);
        ImGui.DragFloat("Idle bait spread radius (even waves)", ref C.BaitSpreadRadius, 0.5f, 5f, 25f);

        if(ImGui.CollapsingHeader("Position rule tuning"))
        {
            ImGuiEx.TextWrapped("Radial offset: negative = toward the boss (front), positive = away from the boss (back). Tangential offset: sideways spread along the tower's edge.");
            DrawStandRule("Odd wave - Fan holder (back of Left Tower)", C.OddFanBack);
            DrawStandRule("Odd wave - Spread holder (back of Right Tower)", C.OddSpreadBack);
            DrawStandRule("Odd wave - Idle Tank (front of Left Tower)", C.OddIdleTankFront);
            DrawStandRule("Odd wave - Idle Healer (back of Left Tower)", C.OddIdleHealerBack);
            DrawStandRule("Odd wave - Idle DPS (front of Right Tower)", C.OddIdleDpsFront);
            DrawStandRule("Even wave - Stack holders (back of Left Tower, mutual guide)", C.EvenStackBack);
            DrawStandRule("Even wave - Spread holders (Right Tower, side by side)", C.EvenSpreadCenter);
        }

        if(ImGui.CollapsingHeader("Debug"))
        {
            ImGui.InputUInt("Tower count", ref TowerCount);
            ImGuiEx.Text($"Sequence: {SequenceCount} ({(SequenceCount % 2 == 1 ? "odd" : "even")})");
            ImGuiEx.Text($"My role: {Controller.RolePosition}");
            ImGuiEx.Text($"Partner: {GetPartner()?.ToString() ?? "<not found>"}");
            ImGuiEx.Text($"My group taking tower this wave: {IsMyGroupTakingTowerThisWave?.ToString() ?? "<unknown>"}");
            ImGuiEx.Checkbox("Group A", ref IsGroupA);
            ImGui.SameLine();
            if(ImGui.Button("Force A")) IsGroupA = true;
            ImGui.SameLine();
            if(ImGui.Button("Force B")) IsGroupA = false;
            ImGui.SameLine();
            if(ImGui.Button("Clear")) IsGroupA = null;
            ImGuiEx.Text($"Active map effects: {ActiveMapEffects.Print("|")}");
            ImGuiEx.Text($"Stored AoE: {(StoredAoe == null ? "none" : StoredAoe.Value ? "Future" : "Past")}");
        }
    }

    private static void DrawStandRule(string label, StandRule rule)
    {
        ImGui.PushID(label);
        ImGuiEx.Text(label);
        ImGui.SetNextItemWidth(180f);
        ImGui.DragFloat("Radial offset", ref rule.RadialOffset, 0.1f, -10f, 10f);
        ImGui.SetNextItemWidth(180f);
        ImGui.DragFloat("Tangential offset", ref rule.TangentialOffset, 0.1f, 0f, 10f);
        ImGui.PopID();
    }

    public enum TowerSide { Left, Right }

    public sealed class StandRule
    {
        public TowerSide Tower;
        public float RadialOffset;
        public float TangentialOffset;

        public StandRule() { }
        public StandRule(TowerSide tower, float radialOffset, float tangentialOffset)
        {
            Tower = tower;
            RadialOffset = radialOffset;
            TangentialOffset = tangentialOffset;
        }
    }

    public class Config
    {
        public bool ClockwiseIndexIncreases = true;
        public bool ShowDamagePreviews = true;
        public bool ShowBossNearestPreview = true;
        public float BaitSpreadRadius = 14f;

        public StandRule OddFanBack = new(TowerSide.Left, 3f, 0f);
        public StandRule OddSpreadBack = new(TowerSide.Right, 3f, 0f);
        public StandRule OddIdleTankFront = new(TowerSide.Left, -3f, 0f);
        public StandRule OddIdleHealerBack = new(TowerSide.Left, 3f, 0f);
        public StandRule OddIdleDpsFront = new(TowerSide.Right, -3f, 0f);
        public StandRule EvenStackBack = new(TowerSide.Left, 3f, 2f);
        public StandRule EvenSpreadCenter = new(TowerSide.Right, 0f, 2f);
    }
}
