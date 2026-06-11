# P1_Arrows Counterclockwise Config — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an opt-in `Counterclockwise` setting to `P1_Arrows` that mirrors the arrow waypoint layout from clockwise to counterclockwise, default off.

**Architecture:** Register a second set of 16 waypoint elements (names = CW name + `" CCW"`) with the confirmed CCW coordinates. `OnUpdate` selects which set to address by appending a `" CCW"` name suffix when the config bool is on. No runtime mutation of the existing CW elements; their default behavior is untouched.

**Tech Stack:** C# (Dalamud plugin, `net10.0-windows7.0`), Splatoon `SplatoonScript` API, ECommons `EzConfig`/`IEzConfig`, `Dalamud.Bindings.ImGui`.

---

## Context the engineer must know

- File: `SplatoonScripts/Duties/Dawntrail/Dancing Mad/P1_Arrows.cs`. Single class `P1_Arrows : SplatoonScript`.
- The script registers waypoint circles named like `↑↑ 1`, `↑→ 2`, `↓← 1` (glyph pair + space + `1`/`2`). Point `1` = first-expiring arrow; point `2` = second.
- `OnUpdate` calls `Controller.Hide()` then re-enables only the local player's two points via `Controller.TryGetElementByName($"{MyArrows} {n}", ...)`.
- **No unit-test framework exists** (per `CLAUDE.md`). Verification = a static coordinate cross-check script (runs on Linux) + build/in-game checks done in the user's Windows + Dalamud dev environment. This repo **cannot build on Linux** (Dalamud libs resolve from `%appdata%\XIVLauncher\...`).
- Arrow glyphs are exact Unicode: `↑`=U+2191, `→`=U+2192, `↓`=U+2193, `←`=U+2190. Copy them exactly so names match `MyArrows` at runtime.
- Spec: `docs/superpowers/specs/2026-06-10-p1-arrows-counterclockwise-config-design.md`.
- Work happens on branch `feat/p1-arrows-ccw-config` (already created).

---

## Confirmed CCW coordinate table (source of truth)

| Element | refX | refY | Element | refX | refY |
|---------|------|------|---------|------|------|
| ↑↑ 1 CCW | 112 | 106 | ↑↑ 2 CCW | 112 | 100 |
| ↓↓ 1 CCW | 88 | 94 | ↓↓ 2 CCW | 88 | 100 |
| →→ 1 CCW | 94 | 112 | →→ 2 CCW | 100 | 112 |
| ←← 1 CCW | 106 | 88 | ←← 2 CCW | 100 | 88 |
| ↑→ 1 CCW | 112 | 112 | ↑→ 2 CCW | 106 | 112 |
| ↓← 1 CCW | 88 | 88 | ↓← 2 CCW | 94 | 88 |
| ↑← 1 CCW | 112 | 94 | ↑← 2 CCW | 112 | 88 |
| ↓→ 1 CCW | 88 | 106 | ↓→ 2 CCW | 88 | 112 |

---

### Task 1: Plumbing — usings, version bump, Config

**Files:**
- Modify: `SplatoonScripts/Duties/Dawntrail/Dancing Mad/P1_Arrows.cs`

- [ ] **Step 1: Replace the using block** (add ImGui, ECommons.Configuration, System.Globalization)

Replace:
```csharp
using Dalamud.Game.ClientState.Objects.SubKinds;
using ECommons;
using ECommons.Logging;
using Splatoon.SplatoonScripting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
```
With:
```csharp
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using ECommons;
using ECommons.Configuration;
using ECommons.Logging;
using Splatoon.SplatoonScripting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
```

- [ ] **Step 2: Bump the metadata version**

Replace:
```csharp
    public override Metadata Metadata { get; } = new(1, "NightmareXIV");
```
With:
```csharp
    public override Metadata Metadata { get; } = new(2, "NightmareXIV");
```

- [ ] **Step 3: Add the Config class and accessor** (right after `ValidTerritories`)

Replace:
```csharp
    public override HashSet<uint>? ValidTerritories { get; } = [1363];
```
With:
```csharp
    public override HashSet<uint>? ValidTerritories { get; } = [1363];

    private Config C => Controller.GetConfig<Config>();

    private sealed class Config : IEzConfig
    {
        public bool Counterclockwise = false;
    }
```

- [ ] **Step 4: Commit**

```bash
git add "SplatoonScripts/Duties/Dawntrail/Dancing Mad/P1_Arrows.cs"
git commit -m "P1_Arrows: add Counterclockwise config scaffold (default off)"
```

---

### Task 2: Write the verification script (the test) and watch it fail

**Files:**
- Create: `/tmp/verify_ccw.py` (not committed)

- [ ] **Step 1: Create the verification script**

```python
import re, sys, pathlib
src = pathlib.Path("SplatoonScripts/Duties/Dawntrail/Dancing Mad/P1_Arrows.cs").read_text(encoding="utf-8")
m = re.search(r"CcwElements\s*=\s*\[(.*?)\];", src, re.S)
if not m:
    print("FAIL: CcwElements array not found"); sys.exit(1)
entries = re.findall(r'\("([^"]+)",\s*(\d+),\s*(\d+)\)', m.group(1))
got = {name: (int(x), int(z)) for name, x, z in entries}
expected = {
    "↑↑ 1 CCW": (112, 106), "↑↑ 2 CCW": (112, 100),
    "↓↓ 1 CCW": (88, 94),   "↓↓ 2 CCW": (88, 100),
    "→→ 1 CCW": (94, 112),  "→→ 2 CCW": (100, 112),
    "←← 1 CCW": (106, 88),  "←← 2 CCW": (100, 88),
    "↑→ 1 CCW": (112, 112), "↑→ 2 CCW": (106, 112),
    "↓← 1 CCW": (88, 88),   "↓← 2 CCW": (94, 88),
    "↑← 1 CCW": (112, 94),  "↑← 2 CCW": (112, 88),
    "↓→ 1 CCW": (88, 106),  "↓→ 2 CCW": (88, 112),
}
ok = True
if len(got) != 16:
    print(f"FAIL: expected 16 entries, got {len(got)}"); ok = False
for k, v in expected.items():
    if got.get(k) != v:
        print(f"FAIL: {k} expected {v} got {got.get(k)}"); ok = False
for k in got:
    if k not in expected:
        print(f"FAIL: unexpected entry {k}"); ok = False
print("PASS: all 16 CCW coordinates match" if ok else "FAILED")
sys.exit(0 if ok else 1)
```

- [ ] **Step 2: Run it to confirm it fails** (CCW table not added yet)

Run: `python3 /tmp/verify_ccw.py`
Expected: `FAIL: CcwElements array not found` and exit code 1.

---

### Task 3: Register the CCW element set in OnSetup

**Files:**
- Modify: `SplatoonScripts/Duties/Dawntrail/Dancing Mad/P1_Arrows.cs`

- [ ] **Step 1: Add the template constant and CCW table** (immediately before `public override void OnSetup()`)

Replace:
```csharp
    public override void OnSetup()
    {
```
With:
```csharp
    private const string CcwElementTemplate =
        """{{"Name":"{0}","type":0,"Enabled":false,"refX":{1},"refY":{2},"radius":0.5,"color":3358457600,"Filled":false,"fillIntensity":0.5,"overlayVOffset":2.0,"overlayFScale":1.0,"overlayText":"$ELEMENT","thicc":4.0,"faceplayer":"<1>","FillStep":0.5}}""";

    // Counterclockwise waypoints: same 16-cell footprint, circulation reversed.
    private static readonly (string Name, float X, float Z)[] CcwElements =
    [
        ("↑↑ 1 CCW", 112, 106), ("↑↑ 2 CCW", 112, 100),
        ("↓↓ 1 CCW", 88, 94),   ("↓↓ 2 CCW", 88, 100),
        ("→→ 1 CCW", 94, 112),  ("→→ 2 CCW", 100, 112),
        ("←← 1 CCW", 106, 88),  ("←← 2 CCW", 100, 88),
        ("↑→ 1 CCW", 112, 112), ("↑→ 2 CCW", 106, 112),
        ("↓← 1 CCW", 88, 88),   ("↓← 2 CCW", 94, 88),
        ("↑← 1 CCW", 112, 94),  ("↑← 2 CCW", 112, 88),
        ("↓→ 1 CCW", 88, 106),  ("↓→ 2 CCW", 88, 112),
    ];

    public override void OnSetup()
    {
```

- [ ] **Step 2: Register the CCW elements at the end of OnSetup** (after the existing CW block)

Replace:
```csharp
            """);
    }
```
With:
```csharp
            """);

        foreach(var e in CcwElements)
        {
            Controller.RegisterElementFromCode(e.Name,
                string.Format(CultureInfo.InvariantCulture, CcwElementTemplate, e.Name, e.X, e.Z),
                overwrite: true);
        }
    }
```

- [ ] **Step 3: Run the verification script to confirm it passes**

Run: `python3 /tmp/verify_ccw.py`
Expected: `PASS: all 16 CCW coordinates match` and exit code 0.

- [ ] **Step 4: Commit**

```bash
git add "SplatoonScripts/Duties/Dawntrail/Dancing Mad/P1_Arrows.cs"
git commit -m "P1_Arrows: register counterclockwise waypoint element set"
```

---

### Task 4: Wire the variant suffix into OnUpdate

**Files:**
- Modify: `SplatoonScripts/Duties/Dawntrail/Dancing Mad/P1_Arrows.cs`

- [ ] **Step 1: Add the `Variant` selector property** (next to the existing state fields)

Replace:
```csharp
    string MyArrows;
    bool IsOrderReverse;
    public override void OnUpdate()
```
With:
```csharp
    string MyArrows;
    bool IsOrderReverse;
    string Variant => C.Counterclockwise ? " CCW" : "";
    public override void OnUpdate()
```

- [ ] **Step 2: Append `{Variant}` to the first (highlighted) lookup**

Replace:
```csharp
            if(Controller.TryGetElementByName($"{MyArrows} {(IsOrderReverse?2:1)}", out var e1))
```
With:
```csharp
            if(Controller.TryGetElementByName($"{MyArrows} {(IsOrderReverse?2:1)}{Variant}", out var e1))
```

- [ ] **Step 3: Append `{Variant}` to the second (dim) lookup**

Replace:
```csharp
            if(Controller.TryGetElementByName($"{MyArrows} {(IsOrderReverse ? 1 : 2)}", out var e2))
```
With:
```csharp
            if(Controller.TryGetElementByName($"{MyArrows} {(IsOrderReverse ? 1 : 2)}{Variant}", out var e2))
```

- [ ] **Step 4: Append `{Variant}` to the cnt==1 lookup**

Replace:
```csharp
            if(Controller.TryGetElementByName($"{MyArrows} {(IsOrderReverse?1:2)}", out var e2))
```
With:
```csharp
            if(Controller.TryGetElementByName($"{MyArrows} {(IsOrderReverse?1:2)}{Variant}", out var e2))
```

- [ ] **Step 5: Confirm the suffix wiring is present**

Run: `grep -c "{Variant}" "SplatoonScripts/Duties/Dawntrail/Dancing Mad/P1_Arrows.cs"`
Expected: `3`

Note on correctness: `DetermineArrow` keeps using the un-suffixed name for its existence/reverse check — that is intentional and correct, because both element sets contain the same 8 canonical pairs, so which pair exists (and whether to reverse) does not depend on handedness. The dim-color restore line `Controller.OriginalElements[e2.Name].color` resolves against the CCW element's own name, which is present because the CCW set is registered in `OnSetup` (captured into `OriginalElements` the same way as the CW set).

- [ ] **Step 6: Commit**

```bash
git add "SplatoonScripts/Duties/Dawntrail/Dancing Mad/P1_Arrows.cs"
git commit -m "P1_Arrows: select CW/CCW waypoint set from config in OnUpdate"
```

---

### Task 5: Settings UI

**Files:**
- Modify: `SplatoonScripts/Duties/Dawntrail/Dancing Mad/P1_Arrows.cs`

- [ ] **Step 1: Add `OnSettingsDraw`** (before the final class brace, after `OnUpdate`)

Replace (this is the end of `OnUpdate` and the class close):
```csharp
            }
        }
    }
}
```
With:
```csharp
            }
        }
    }

    public override void OnSettingsDraw()
    {
        ImGui.Checkbox("Counterclockwise (mirror handedness)", ref C.Counterclockwise);
        ImGui.TextDisabled("Mirrors the arrow waypoints. Default off = clockwise (unchanged).");
    }
}
```

- [ ] **Step 2: Confirm the method is present**

Run: `grep -c "OnSettingsDraw" "SplatoonScripts/Duties/Dawntrail/Dancing Mad/P1_Arrows.cs"`
Expected: `1`

- [ ] **Step 3: Commit**

```bash
git add "SplatoonScripts/Duties/Dawntrail/Dancing Mad/P1_Arrows.cs"
git commit -m "P1_Arrows: add Counterclockwise checkbox to settings"
```

---

### Task 6: Build, manual verification, and distribution decision

**Files:**
- (Decision) `SplatoonScripts/update.csv`

- [ ] **Step 1: Build in the Windows + Dalamud dev environment**

Run (on the user's Windows box, not here): `dotnet build Splatoon.sln -c Debug`
Expected: build succeeds. This repo cannot build on Linux (Dalamud DLLs unavailable), so this step is the user's.

- [ ] **Step 2: In-game / duty-recorder manual verification**

  - With the box **off**: each pair still points to the original CW cells (table unchanged); behavior identical to before.
  - With the box **on**: each pair points to the CCW cells from the table above; the 16 ground arrows circulate **counterclockwise**; the four corners read NW=↓, NE=←, SE=↑, SW=→.
  - Toggle mid-fight: the highlighted waypoints jump to the other set on the next frame (`OnUpdate` rebuilds every frame).
  - CCW point labels read with a ` CCW` suffix (e.g. `↑↑ 1 CCW`) — expected.

- [ ] **Step 3: Distribution decision for `update.csv`** (no code change required)

The script version is now `2`. The canonical regeneration tool is `ScriptUpdateFileGenerator` (runs as part of the Windows build).

  - **Recommended for a local/fork edit:** leave `SplatoonScripts/update.csv` line 203 unchanged (still version `1`, upstream URL). The auto-updater will not overwrite your local version-2 file because the listed version (`1`) is not newer than installed (`2`).
  - **If publishing through your fork:** point that line's URL at your fork's raw path and set its version to `2` — ideally by running `ScriptUpdateFileGenerator` rather than hand-editing. Do **not** set version `2` while the URL still serves upstream's version-1 file.

- [ ] **Step 4: Final review of the diff**

Run: `git diff main...feat/p1-arrows-ccw-config -- "SplatoonScripts/Duties/Dawntrail/Dancing Mad/P1_Arrows.cs"`
Confirm: only the using block, metadata version, Config, CCW template/table, `OnSetup` loop, `Variant` + three lookups, and `OnSettingsDraw` changed; nothing else.

---

## Self-Review

**Spec coverage:**
- Config bool default off → Task 1 Step 3. ✓
- Second registered CCW element set as JSON → Task 3. ✓
- CCW coordinates per confirmed table → Task 3 + Task 2 verification. ✓
- `OnUpdate` suffix selection, `DetermineArrow` untouched → Task 4. ✓
- `OnSettingsDraw` checkbox → Task 5. ✓
- Version bump + update.csv handling → Task 1 Step 2 + Task 6 Step 3. ✓
- Testing (static + in-game) → Task 2/3 + Task 6 Step 2. ✓
- "Won't break others" (default off) → Task 1 Step 3, Task 6 Step 2/3. ✓

**Placeholder scan:** No TBD/TODO; every code step shows full code; the verification script and all edit blocks are complete. ✓

**Type/name consistency:** `C` accessor, `Config.Counterclockwise`, `CcwElementTemplate`, `CcwElements` (fields `Name/X/Z`), `Variant` suffix `" CCW"`, element names `"<glyph><glyph> <n> CCW"` — used consistently across Tasks 1, 3, 4, 5 and the verification script. The runtime lookup `$"{MyArrows} {n}{Variant}"` = `"↑↑ 1" + " CCW"` matches registered `"↑↑ 1 CCW"`. ✓
