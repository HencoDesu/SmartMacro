# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Windows-only desktop automation tool (C# / .NET 10 / Avalonia). Generic in design, Perfect World in practice: it watches for windows of configured processes, tags them (free-form strings, typically applied by OpenCV template matching), and runs user-authored **macro graphs** against them via Win32 messages — driving ~10 `elementclient_64.exe` clients through login and party-wide actions. UI text and commit messages are in Russian; tags are Cyrillic class names ("Лучник", "Жрец") because that's what the game renders.

## ⚠️ In-flight refactoring — read before large changes

`docs/refactoring-plan-split.md` is the approved roadmap and is being executed wave by wave: rename to **SmartMacro**, macros as **node graphs** (actions + conditional nodes + variables) with triggers (hotkey / process-appeared), `CharacterClass`/roster dissolved into free-form **window tags** with tag-selector routing, then a split into a background **Daemon** (tray, hooks, vision, IPC server) + on-demand Avalonia **App** (named-pipe client).

**Done so far:** W0.0 (rename to SmartMacro + `SmartMacro.Tests`), W0.1 (`WindowRegistry`/tags/`ProcessProfiles`, `CharacterClass` and Stateless removed), W0.2a (node-graph model, `MacroExecutor`, validator), W0.2b (primitives over real input/vision, `macros/` folder storage + migration + PW examples, triggers driven by the macro library, legacy pipeline deleted). The sections below describe the architecture as it stands after W0.2b.

**Still ahead:** W0.3 rebuilds the UI (main window around windows+tag chips, a real node editor — the current macros dialog is a transitional Run/Stop list), W0.4 adds the canvas editor, then stages 1–4 split the app into Daemon + on-demand UI over IPC.

## Commands

```bash
dotnet build SmartMacro.slnx                  # full solution
dotnet run --project src/SmartMacro.App
dotnet run --project tools/VisionSampleRunner # vision debugging harness (coord OCR over samples/)
dotnet run --project tests/SmartMacro.Tests   # TEST GATE — use this one
```

`dotnet test` currently reports "zero tests ran" (exit 5) in this environment despite the MTP opt-in in `global.json`; the TUnit-generated entry point via `dotnet run` is the reliable gate.

Build warnings NU1903 (Tmds.DBus.Protocol) are known noise.

**DLL-lock gotcha:** if the app is running, `dotnet build` fails copying DLLs (MSB3027/MSB3021 with a PID). The user must Exit via the tray icon (window close only hides to tray), then rebuild. Code-compile errors vs file-lock errors look similar in output — check before diagnosing.

**Config propagation gotcha:** `appsettings.json` is copied to `bin/Debug/net10.0-windows/` only on build (`PreserveNewest`). Editing the source-tree JSON and restarting the exe without a rebuild does NOT pick up changes. Options are bound once at startup via `IOptions` — every config change requires app restart.

## Architecture

Three projects, strict layering: `App` (Avalonia UI, DI composition root in `Program.cs`) → `Core` (all domain logic) → `Native` (Win32 P/Invoke via `LibraryImport`, no dependencies).

### Core pipeline

Everything the app does is a macro run. There is exactly one path from trigger to effect:

```
hotkey / process-appeared / UI Run
  → Orchestrator.RunAsync(name, contextWindow, singleFlightKey)
  → MacroGraphStore.TryGet  →  MacroRunRegistry.TryBegin  →  MacroExecutor.RunAsync
  → IMacroPrimitives (input / vision / icon)  +  WindowRegistry (tags)
```

- **ProcessMonitoring** polls for watched processes (union of `ProcessProfiles` names) → `Orchestrator` creates one `CharacterAgent` per process via `CharacterAgentFactory`.
- **CharacterAgent** (`Core/Agents`) is now just a window-lifetime shell: `Start()` registers hwnd + process name + the `IGameWindow` facade in `WindowRegistry`, a poll loop notices the window dying and unregisters it. No state machine, no inbox commands, no boot flow — those are macro graphs.
- **WindowRegistry** (`Core/Windows`) is the sole owner of window tags AND the `hwnd → IGameWindow` lookup. Tag selectors (`RequireTags`/`ExcludeTags`) route every fan-out; "identified" just means "has at least one tag".
- **Orchestrator** (`Core/Orchestration`) turns triggers into runs. Hotkey runs have no context window (macros must route by selector) and are single-flight per macro NAME; process-appeared runs get the new window as context and are single-flight per (macro, window) so N clients launching at once each boot. Both seed the `cursor` variable via `CursorPositionProvider`.
- **Macros** (`Core/Macros`) — `Model` (polymorphic `$type` nodes + triggers), `Execution` (`MacroExecutor` walker, `MacroPrimitives`, `MacroRunRegistry`, run variables), `Validation`, `Storage`. See `docs/refactoring-plan-split.md` §0.2–0.3 for the node catalogue and semantics.
- **`MacroGraphStore`** (`Core/Macros/Storage`) is the library of record: one JSON file per graph under `macros/`, filename stem = macro name. It resolves sub-macros for `RunMacroNode`, supplies `HotkeyListener`'s bindings (re-registered on every change), and tells the orchestrator which graphs a new process should boot. On first run it migrates a legacy `macros.json` (+ `hotkeys.json` macro bindings → triggers) and, if the folder ends up empty, seeds the `pw-*` examples.
- **Identification** is no longer built in: it's the `pw-identify` / `pw-boot` example macros — `KeyPress(C)` → `Delay` → `RecognizeTagNode` (template set `"classes"` → `Assets/GameClassNames/{tag}.png`) → `SetIconNode` → `KeyPress(C)`. Master/ignored characters are just tags in a selector (`ExcludeTags: ["Лучник", "Шаман"]`).

### Win32 input model (the hard-won part — do not "simplify" without re-testing in game)

PW freezes background clients (input + rendering). Every input session is bracketed by `IGameWindow.ActivateAsync` (sends magic `WM_ACTIVATEAPP` with lParam from the window's `ProcessProfile`) and `DeactivateAsync` (drain delay, then deactivate — unless window is foreground). `AgentInputDispatcher` wraps this lifecycle around every keypress/click, one cycle per node.

- **Keyboard = SendMessage, mouse = PostMessage** (`GameWindowFactory` bakes this in). Post'd keys got dropped by the frozen pump; clicks were always reliable.
- **Modifier chords (Shift+1) DO NOT WORK** via message injection: PW reads modifiers with `GetKeyState`, which cross-thread SendMessage never updates. That's why the `pw-assist` example selects the master by *clicking* party-slot-1 instead of Shift+1.
- **WM_SETICON must be SendMessage** — Post'd icon updates sit unprocessed in frozen queues. `WindowIconService` also retries at +2s/+5s because PW's post-boot init can reset the icon.
- Both the game and this app run elevated (`requireAdministrator` in app.manifest) — UIPI blocks input/capture into elevated windows otherwise.

### Vision

`IGameWindow.FindElementAsync` (one shot) and `WaitForElementAsync` (poll until timeout) are the generic primitives; both return the CLIENT-SPACE CENTRE of the match, which is what `FoundPointVar` feeds to a later `ClickNode`. Each tick is an active capture (wake → PrintWindow with `PW_CLIENTONLY | PW_RENDERFULLCONTENT` → re-freeze) + grayscale `TM_CCOEFF_NORMED` matching. Grayscale, NOT binarized — game UI sits on semi-transparent backgrounds where binarization is unstable. `ClassMatcher` (stats-window text on solid panel) is the exception that still binarizes.

`TemplateSetProvider` resolves the names nodes carry into bytes: single templates by stem from `Assets/GameUiElements`, and the set `"classes"` from `Assets/GameClassNames` (a compatibility alias — TODO W0.4 collapses both into one `templates/` tree). All coordinates/templates are pixel-exact for the author's 3840×2160 screen; they now live in macro nodes, not config.

### Runtime state files (next to the exe, gitignored)

`macros/*.json` (one graph per file) and `logs/`. `MacroGraphStore` follows the usual store pattern — load on ctor → immutable snapshot → CRUD persists + raises `MacrosChanged` → subscribers (`HotkeyListener`, UI) re-register live — plus a debounced `FileSystemWatcher` for external edits, with our own writes suppressed by comparing a folder signature of last-write timestamps. An unparseable file is skipped and logged, never fatal to the load.

`hotkeys.json` and the single `macros.json` are GONE. Both are migrated once on first run and renamed to `*.migrated`; a hotkey is now a `HotkeyTrigger` inside the macro it starts. Legacy bindings for the deleted built-in broadcast actions have no destination and are reported as orphaned in the log — the equivalent behaviors are the `pw-*` example macros.

### Conventions

- Logging via source-generated `[LoggerMessage]` partial methods, usually split into a sibling `*.Logging.cs` partial file.
- Options classes bind from `appsettings.json` sections in `Program.cs` (`AgentOptions` ← `"Agent"` — poll intervals only now, `ProcessProfileOptions` ← `"ProcessProfiles"`, vision options ← `"Vision:*"`). Coordinates, regions, templates, keys and timeouts belong in macro nodes, NOT in config.
- `ScreenPoint` / `ScreenRect` record structs (in `Native`) for all pixel coordinates — bind from JSON as `{ "X": .., "Y": .. }` objects.
- Coordinate discovery workflow: user hovers cursor in-game and triggers a macro; `CursorPositionProvider` logs the client-space point it seeds the `cursor` variable with, which then goes into a node.
- "Dump captures" button in the main window writes per-agent `debug/*-full.png` and `*-class-bin.png` for tuning vision regions.

`docs/spec.md` (v0.4, Russian) has the game-domain context and a decision-history table explaining why earlier designs (FOLLOW/HOLD/COMBAT state machine, LLM integration, per-character roster, nameplate identification) were dropped. Its architecture sections predate the node-graph waves — read them as history until the doc is refreshed.
