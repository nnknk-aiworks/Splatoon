# P1_Arrows — Counterclockwise (handedness) config

Date: 2026-06-10
Script: `SplatoonScripts/Duties/Dawntrail/Dancing Mad/P1_Arrows.cs`
Distribution id: `SplatoonScriptsOfficial.Duties.Dawntrail.Dancing_Mad@P1_Arrows`

## Goal

Add an opt-in setting that mirrors the arrow waypoint layout from its current
**clockwise (CW)** handedness to **counterclockwise (CCW)**, without changing the
default behavior for anyone already using the script.

## Background

P1_Arrows reacts to the two directional arrow debuffs each player carries during
the P1 arrow mechanic (territory `1363`). The debuffs map to glyphs `↑ → ↓ ←`
(`Arrows` dict). The script registers a fixed set of waypoint circles and, in
`OnUpdate`, highlights the two the local player should stand on:

- **Point 1** = where you stand while holding both debuffs; the **first-expiring**
  arrow (`MyArrows[0]`) resolves here.
- **Point 2** = where you move next; the **second** arrow (`MyArrows[1]`) resolves here.

`MyArrows` is the two glyphs ordered by remaining time. `IsOrderReverse` maps a
non-canonical order onto the 8 canonical pair elements. Coordinates are
`(refX, refY)` = `(X, Z)`; arena center `(100,100)`; N = −Z, S = +Z, E = +X, W = −X.

The 16 ground arrows the mechanic drops form an axis-aligned square ring whose
arrows circulate **clockwise** (each edge's arrows point along the edge toward the
next corner). This config flips that circulation to **counterclockwise** — same
16-cell footprint, every cell's arrow reversed in circulation sense, players
relocated accordingly.

## Confirmed coordinate tables

Convention: `point1 = first-expiring arrow`, `point2 = second arrow`.

### CW (current, unchanged)

| Pair | Point 1 (first) | Point 2 (second) |
|------|-----------------|------------------|
| ↑↑ | ↑ (88,106) W edge S | ↑ (88,100) W edge mid |
| ↓↓ | ↓ (112,94) E edge N | ↓ (112,100) E edge mid |
| →→ | → (94,88) N edge W | → (100,88) N edge mid |
| ←← | ← (106,112) S edge E | ← (100,112) S edge mid |
| ↑→ | ↑ (88,94) W edge N | → (88,88) NW corner |
| ↓← | ↓ (112,106) E edge S | ← (112,112) SE corner |
| ↑← | ↑ (88,112) SW corner | ← (94,112) S edge W |
| ↓→ | ↓ (112,88) NE corner | → (106,88) N edge E |

### CCW (new, enabled by config)

| Pair | Point 1 (first) | Point 2 (second) |
|------|-----------------|------------------|
| ↑↑ | ↑ (112,100) E edge mid | ↑ (112,106) E edge S |
| ↓↓ | ↓ (88,100) W edge mid | ↓ (88,94) W edge N |
| →→ | → (100,112) S edge mid | → (94,112) S edge W |
| ←← | ← (100,88) N edge mid | ← (106,88) N edge E |
| ↑→ | ↑ (112,112) SE corner | → (106,112) S edge E |
| ↓← | ↓ (88,88) NW corner | ← (94,88) N edge W |
| ↑← | ↑ (112,94) E edge N | ← (112,88) NE corner |
| ↓→ | ↓ (88,106) W edge S | → (88,112) SW corner |

Corners (CCW): NW = ↓, NE = ←, SE = ↑, SW = →. Each corner points along the edge
it turns into under CCW flow. Same-arrow pairs use the mirrored footprint (↑↑/↓↓
flip W↔E, →→/←← flip N↔S) but resolve the on-axis (cardinal) cell first and the
off-axis cell second; mixed pairs move to the opposite corner — there is no single
geometric transform, so positions are declared explicitly.

CCW arrow-direction → edge: ↓ on W edge (incl. NW corner), ← on N edge (incl. NE
corner), ↑ on E edge (incl. SE corner), → on S edge (incl. SW corner). 4 of each.

## Chosen approach: second registered element set (Approach 2)

Register a second full set of 16 CCW waypoint elements as JSON, alongside the
existing CW set. `OnUpdate` selects which set to address by name suffix based on
config. This keeps the script data-driven (CCW points declared as JSON like the CW
ones) and leaves the selection logic a thin name-builder — no runtime coordinate
mutation.

Rejected alternatives:
- **Runtime coordinate override** (mutate `refX/refY` of the CW elements each frame
  from a CCW table): fewer elements, but mutates shared element state and diverges
  from the script's pure-JSON style.
- **Geometric transform**: mixed pairs/corners do not follow a uniform mirror or
  rotation, so it degenerates into per-group special cases — more error-prone.

## Design

### 1. Config

```csharp
private Config C => Controller.GetConfig<Config>();

private sealed class Config : IEzConfig
{
    public bool Counterclockwise = false;   // default = current CW behavior
}
```

Per-user, per-script (EzConfig in the plugin config dir). Default `false` ⇒
behavior byte-for-byte identical to today; existing users are unaffected.

### 2. CCW elements (OnSetup)

After the existing CW `RegisterElementsFromMultilineCode(...)`, register 16 more
elements via a second `RegisterElementsFromMultilineCode(...)` block.

- Name = `"<CW name> CCW"`, e.g. `↑↑ 1 CCW`, `↑→ 2 CCW`.
- `refX/refY` from the CCW table above.
- All other fields copied verbatim from the CW point template (`type:0`,
  `color:3358457600`, `radius:0.5`, `Filled:false`, `thicc:4.0`,
  `overlayText:"$ELEMENT"`, `faceplayer:"<1>"`, etc.).
- The 8 `-- … --` separator placeholders are **not** duplicated.

`overlayText` stays `"$ELEMENT"`, so a CCW point's floating label reads
`↑↑ 1 CCW` (suffix visible). Accepted as-is; if a clean label is wanted later, set
those elements' `overlayText` to a fixed string instead.

### 3. OnUpdate (minimal change)

Introduce a variant suffix and append it to every element lookup name:

```csharp
string Variant => C.Counterclockwise ? " CCW" : "";
// e1: $"{MyArrows} {(IsOrderReverse?2:1)}{Variant}"
// e2: $"{MyArrows} {(IsOrderReverse?1:2)}{Variant}"
```

All highlight / dim / tether / color logic is unchanged. `OriginalElements[e.Name]`
lookups (used to restore the dim element's original color) resolve against the
CCW element's own name, which is correct because both sets are registered.

`DetermineArrow` is **unchanged**: its existence/reverse check (`"{MyArrows} 1"`)
may keep using CW names, because both sets contain the same 8 canonical pairs, so
which pairs exist (and whether to reverse) is independent of handedness.

### 4. Settings (OnSettingsDraw — new)

```csharp
public override void OnSettingsDraw()
{
    ImGui.Checkbox("Counterclockwise (mirror handedness)", ref C.Counterclockwise);
    // one short explanatory line
}
```

### 5. Versioning / distribution

- Bump `Metadata` from `new(1, "NightmareXIV")` to version `2` (keep attribution).
- Regenerate `update.csv` with `ScriptUpdateFileGenerator` rather than hand-editing.
- "Won't break others": users on the upstream official script never receive this
  change; users on this fork get only a default-off toggle. No default behavior
  changes either way.

## Testing

No unit-test harness in this repo (per CLAUDE.md). Verification:

1. Static: cross-check all 16 CCW JSON coordinates against the CCW table above.
2. In-game / duty-recorder replay: with the box off, confirm CW behavior unchanged;
   with it on, confirm each pair points to the CCW cells and the 16 ground arrows
   circulate counterclockwise.

## Out of scope (YAGNI)

Preview mode, arbitrary rotation/scaling/recentering, per-point customization. This
change is a single CW/CCW boolean only.
