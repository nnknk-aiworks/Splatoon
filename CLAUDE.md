# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Splatoon is a **Dalamud plugin** for Final Fantasy XIV. It reads live game state (object positions, casts, tethers, map effects, buffs) and renders 2D/3D overlays ("waymarks") in the world, plus runs user-written C# **scripts** ("microplugins") that react to in-game events. It never touches the server — it only consumes data the client already has and draws client-side.

Upstream is `PunishXIV/Splatoon` (author NightmareXIV); this checkout's `origin` is the `nnknk-aiworks/Splatoon` fork. The plugin runs on Dalamud **API level 15**.

## Repository layout

The **git root is the solution root**, and all dependency repos are vendored in as sibling subfolders — they are *not* NuGet packages:

- `Splatoon/` — the actual plugin project (`Splatoon.csproj`). **This is where almost all work happens.**
- `SplatoonScripts/` — the official scripts repo (`SplatoonScriptsOfficial.csproj`); user-facing C# scripts, organized as `Duties/<Expansion>/Name.cs`, `Generic/`, `Tests/`.
- `ECommons/`, `ECommons.IPC/` — NightmareXIV's shared library (the `Svc` service locator, hook helpers, config, ImGui helpers). Reused across all their plugins.
- `ffxiv_pictomancy/` (Pictomancy) — 3D world drawing used by the DirectX11 render backend.
- `NightmareUI/`, `WrathCombo.API/`, `AutoRetainerAPI/`, `FFXIVClientStructs/`, `ScriptUpdateFileGenerator/` — supporting projects referenced by the solution.

**These sibling repos are git submodules** (see `.gitmodules`: `ECommons`, `ECommons.IPC`, `ffxiv_pictomancy`, `NightmareUI`, `FFXIVClientStructs`, `WrathCombo.API`, `AutoRetainerAPI`, `PunishLib`). A fresh checkout may have them empty — run `git submodule update --init <name>` before reading/grepping their source (e.g. ECommons `Svc`/`MathHelper`).

## Building & running

There is **no `dotnet test` suite**. The `Tests/` folder under `SplatoonScripts/` contains *in-game manual test scripts*, not unit tests. Verification is done by loading the plugin in the game and observing behavior.

Build requires a **Windows machine with XIVLauncher/Dalamud (dev branch) installed** — the `.csproj` resolves `Dalamud.dll`, `FFXIVClientStructs.dll`, `Lumina.dll`, ImGui bindings, etc. from:

```
DalamudLibPath = %appdata%\XIVLauncher\addon\Hooks\dev\
```

Project targets `net10.0-windows7.0`, `x64` only, `LangVersion=preview`, `AllowUnsafeBlocks=true`.

```sh
# Build the whole solution (Debug or Release)
dotnet build Splatoon.sln -c Debug

# DalamudPackager (PackageReference in Splatoon.csproj) emits the loadable plugin
# zip + manifest into Splatoon/bin/<Config>/ on build. Point a Dalamud dev-plugin
# entry at that output (or Splatoon.json) to load it in-game.
```

Run/iterate in-game via the dev plugin loader; key commands are `/splatoon` (open config) and `/sf` (find nearby objects). After editing a *script*, it hot-reloads via `ScriptFileWatcher`; after editing the *plugin*, rebuild and reload the dev plugin.

## Architecture — the big picture

### Three ambient service locators (know these before reading any file)

Globals are wired through `Splatoon/Utility/Global.cs` (`global using` directives). Almost every file assumes these are in scope:

- **`Svc`** (from ECommons) → Dalamud framework services: `Svc.Objects` (object table), `Svc.ClientState`, `Svc.Framework`, `Svc.Data` (Lumina/Excel sheets), `Svc.Chat`, `Svc.Condition`, `Svc.Commands`, `Svc.PluginInterface`.
- **`S`** (`Splatoon/Services/!ServiceManager.cs`, the `!` sorts it first) → Splatoon's own singletons: `S.RenderManager`, `S.Projection`, `S.ScriptFileWatcher`, `S.MapEffectManager`, etc. Populated by `SingletonServiceManager.Initialize(typeof(S))` in `Splatoon.Load`.
- **`P`** / `Splatoon.P` (via `global using static Splatoon.Splatoon`) → the plugin instance: `P.Config`, `P.FrameCounter`, `P.Phase`, `P.LogWindow`, the `*Processor` instances, etc.
- **`BasePlayer`** → the local player as `IPlayerCharacter`, but **override-able during duty replay** (`BasePlayerOverride`). Prefer it over `Svc.ClientState.LocalPlayer` so scripts work in recordings.

### Lifecycle

`Splatoon : IDalamudPlugin` (`Splatoon/Splatoon.cs`). Construction → `Load()` does the entire wiring: `ECommonsMain.Init`, `EzConfig.Init<Configuration>()`, builds NPC-name/zone caches from Lumina sheets, constructs the `Memory/*Processor` hooks, subscribes `Svc.Framework.Update += Tick`, and registers IPC/HTTP/commands. Teardown disposes every hook (critical — see gotchas).

The heartbeat is **`Splatoon.Tick(IFramework)`** (runs every framework update): clears the previous frame's display objects, drains the chat-message queue (dispatching `ScriptingProcessor.OnMessage`), updates combat timer + phase, then iterates visible `Layout`s → `ProcessElementsOfLayout` → `S.RenderManager.GetRenderer(element).ProcessElement(...)`, which produces `DisplayObject`s for the active render backend to draw.

### How game data is read (`Splatoon/Memory/`)

Three mechanisms, often combined:

1. **Per-frame polling** of `Svc.Objects` for positions/rotation/cast/DataId — the default for anything that has a steady state.
2. **Direct struct reads** via FFXIVClientStructs for things not in the object table — `Memory/Camera.cs` (view/projection matrices), `Memory/Scene.cs`.
3. **Function hooks** for *instantaneous events* that can't be polled. Each `Memory/*Processor.cs` either:
   - **owns its hook** — `[Signature("...")]` + `Hook<T>` + a detour (e.g. `TetherProcessor`, `MapEffectProcessor`, `ObjectEffectProcessor`, `ActorControlProcessor`, `BuffEffectProcessor`), or
   - **subscribes to a hook ECommons already maintains** — e.g. `ActionEffectProcessor` is a one-line forwarder; the real hook lives in ECommons and is attached via `ActionEffect.Init(...)` / `ActionEffect.ActionEffectEvent +=` and `DirectorUpdate.Init(...)` in `Load()`.

   **Rule of thumb:** broadly-useful hooks live in ECommons (write once, every plugin benefits); niche/Splatoon-specific ones stay local. When adding a hook, decide which side it belongs on.

Detours convert events into two things: **state** cached in `Memory/AttachedInfo.cs` (e.g. `GetOrCreateTetherInfo` — so per-frame Element evaluation can query "is X currently tethered?"), and **event callbacks** fanned out via `ScriptingProcessor.On*` to every enabled script.

### Elements & Layouts (`Splatoon/Serializables/`)

The declarative, data-driven half (no code). An `Element` is a serializable shape config that **binds itself to game objects** through `refActor*` fields (`refActorName`, `refActorDataID`, `refActorObjectID`, `refActorNPCNameID`, `refActorRequireCast` + `refActorCastId`, `refActorType`/`refActorComparisonType` filters, distance/rotation gates). The render pass resolves the matching live object each frame and computes world coordinates — script/user code does not compute positions for these. `Layout`s group Elements with trigger conditions; the **Freeze** feature snapshots produced display objects for delayed/persistent display.

### Rendering (`Splatoon/RenderEngines/` + `Services/Projection.cs`)

`RenderEngine` is the abstract backend; `RenderManager.GetRenderer(element)` picks per-element or global between **DirectX11Renderer** (true 3D depth; `DirectX11/DirectX11Scene.cs` draws via Pictomancy + native DX11) and **ImGuiLegacyRenderer** (2D fallback). `Services/Projection.cs` does world→screen using the camera matrices from `Memory/Camera.cs`.

### Scripting subsystem (`Splatoon/SplatoonScripting/`)

The code-driven half — the most important subsystem for "vibe-coding" features.

- **`SplatoonScript`** (abstract base): scripts subclass it and override lifecycle (`OnSetup`/`OnEnable`/`OnDisable`/`OnReset`) and event hooks (`OnUpdate`, `OnStartingCast`, `OnTetherCreate/Removal`, `OnMapEffect`, `OnActionEffectEvent`, `OnGain/Remove/UpdateBuffEffect`, `OnDirectorUpdate`, `OnSettingsDraw`, etc.). `ValidTerritories` gates which maps it runs in; `Metadata` carries author/version. `SplatoonScript<T>` adds typed config via `C`.
- **`Controller`** (`Controller.cs`): the helper API exposed to scripts — register/unregister Elements & Layouts (incl. `RegisterElementFromCode` that ingests export strings), state queries (`InCombat`, `Phase`, `Scene`, `GetPartyMembers`, `RolePosition`), `GetConfig<T>`/`SaveConfig`, and `Schedule`/throttlers.
- **`Compiler.cs`**: compiles scripts at runtime with **Roslyn** (`CSharpCompilation`, `LanguageVersion.Preview`, `allowUnsafe`, `OptimizationLevel.Release`). `ReferenceCache.cs` supplies the metadata references.
- **`ScriptingProcessor.cs`**: orchestrates everything on a background thread — queued compile/load, compiled-assembly caching under `<pluginConfigDir>/ScriptCache/<md5>-<version>.bin`, auto-update of trusted scripts from `TrustedURLs`, and the `On*` fan-out to all enabled scripts (each call wrapped in try/catch so one bad script can't break others or the game). User scripts live in `<pluginConfigDir>/Scripts/<namespace>/`.

### Official scripts repo conventions (`SplatoonScripts/`)

`namespace` mirrors the folder path (e.g. `SplatoonScriptsOfficial.Duties.Endwalker`). The distribution id is `SplatoonScriptsOfficial.<Folder>@<ClassName>`. `update.csv` lists `id,version,rawGitHubUrl` for auto-update; `blacklist.csv` blocks known-bad scripts; **`ScriptUpdateFileGenerator/` regenerates `update.csv`** — run it after adding/bumping scripts rather than hand-editing.

### External integration

- **IPC** (`Modules/SplatoonIPC.cs`): `Splatoon.IsLoaded`, `Splatoon.GetActiveDrawGeometryV1`, and `Splatoon.Loaded`/`Unloaded` messages, for other Dalamud plugins.
- **HTTP Web API** (`Modules/HTTPServer.cs`): local server at `http://127.0.0.1:<Config.port>/` for external programs (opt-in via `Config.UseHttpServer`).

### Config

ECommons **`EzConfig`** — the `Serializables/Configuration` class is the single config object, persisted as JSON in the plugin config dir. `EzConfig.Migrate<Configuration>()` + `Services/DataMigrator.cs` + `Modules/ConfigurationMigrator1to2.cs` handle legacy upgrades. Bump migrations rather than breaking old configs.

## Conventions & gotchas

- **Comments in `.cs` files must be English-only**, even when the working conversation is in another language.
- **`unsafe` is pervasive.** Raw game pointers (`GameObject*`, `Character*`, `VfxContainer*`) can be null even when the parent isn't — null-check before dereferencing; there is no GC safety net.
- **Detour discipline** (when adding/editing a `*Processor` hook): call `Hook.Original(...)` first so the game behaves normally, wrap all custom logic in `try/catch` (an exception thrown back into the game call stack crashes the game), and return the original's value untouched. Mark fragile signatures `Fallibility.Fallible` so a sig break degrades one feature instead of failing plugin load.
- **Every hook must be disposed on unload** (see the `Safe(...Dispose)` calls in `Splatoon.cs`). A live hook pointing at unloaded detour code crashes the game on the next call.
- **Game-version fragility:** `[Signature("...")]` byte patterns and FFXIVClientStructs struct offsets break on game patches. Prefer ECommons-provided events over hand-rolled signatures when one exists.
- **Code style** (no `.editorconfig`; match surrounding code): Allman braces, `if(...)`/`for(...)` with **no space** before `(`, 4-space indent, and C# `preview` features (e.g. the `field` keyword, collection expressions `[]`) are used.
- Per-frame caches (`PlaceholderCache`, `PlayerPosCache`, `loggedObjectList`) are cleared/rebuilt at the top of `Tick`; don't assume cross-frame persistence unless the value lives in `AttachedInfo` or config.
