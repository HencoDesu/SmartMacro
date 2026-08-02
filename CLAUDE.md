# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Windows-only desktop agent (C# / .NET 10 / Avalonia) that automates a party of ~10 Perfect World MMO characters, each running in its own `elementclient_64.exe` process. The app watches for game processes, drives each client's window through login (server-select → character-select → in-world), identifies which character class is in each window via OpenCV template matching, and broadcasts commands (immunity, assist, macros, clicks) to all windows via Win32 messages. UI text and commit messages are in Russian; class enum identifiers are Cyrillic (`CharacterClass.Лучник`).

## ⚠️ Planned refactoring — read before large changes

`docs/refactoring-plan-split.md` is the approved roadmap: the project is about to be renamed to **SmartMacro** and rebuilt as a generic window-automation tool — macros become **node graphs** (actions + conditional nodes + variables) with triggers (hotkey / process-appeared), `CharacterClass`/roster concepts dissolve into free-form **window tags** with tag-selector routing, and the monolith splits into a background **Daemon** (tray, hooks, vision, IPC server) + on-demand Avalonia **App** (named-pipe client). Tests return via **TUnit + FakeItEasy**.

Until that plan is executed, the sections below describe the CURRENT (pre-refactor) architecture. Consequences for work done today: don't invest in things the plan deletes — `OrchestratorTrigger` built-in hotkeys, `hotkeys.json`, `AgentMessage` broadcast records, `AgentState`/Stateless machine, hardcoded coordinates in `appsettings.json` are all slated for removal; new features should be weighed against the plan first.

## Commands

```bash
dotnet build PerfectWorldAgent.slnx           # full solution
dotnet run --project src/PerfectWorldAgent.App
dotnet run --project tools/VisionSampleRunner # vision debugging harness (coord OCR over samples/)
```

There are no tests. Build warnings NU1903 (Tmds.DBus.Protocol) are known noise.

**DLL-lock gotcha:** if the app is running, `dotnet build` fails copying DLLs (MSB3027/MSB3021 with a PID). The user must Exit via the tray icon (window close only hides to tray), then rebuild. Code-compile errors vs file-lock errors look similar in output — check before diagnosing.

**Config propagation gotcha:** `appsettings.json` is copied to `bin/Debug/net10.0-windows/` only on build (`PreserveNewest`). Editing the source-tree JSON and restarting the exe without a rebuild does NOT pick up changes. Options are bound once at startup via `IOptions` — every config change requires app restart.

## Architecture

Three projects, strict layering: `App` (Avalonia UI, DI composition root in `Program.cs`) → `Core` (all domain logic) → `Native` (Win32 P/Invoke via `LibraryImport`, no dependencies).

### Core pipeline

- **ProcessMonitoring** polls for game processes → `Orchestrator` creates one `CharacterAgent` per process via `CharacterAgentFactory`.
- **Orchestrator** (`Core/Orchestration`) is a pure dispatcher: global hotkeys (`HotkeyListener`) → broadcast `AgentMessage` records to every agent's inbox channel. Agent→orchestrator traffic (identified, stopping) flows through one shared upstream channel.
- **CharacterAgent** (`Core/Agents`) owns a Stateless state machine (`AwaitingIdentification → Idle`), an inbox `Channel<AgentMessage>`, and a run loop. On Start it fire-and-forgets `EnterWorldAsync` (vision-driven boot: poll for server-select button template → click → poll character-select → click → poll in-world → identify). A `SemaphoreSlim _operationLock` makes boot/identify/macro-run single-flight per agent — concurrent triggers no-op via `WaitAsync(0)`.
- **Identification**: agent opens the in-game stats window (hotkey C), captures, `ClassMatcher` template-matches the class-name text region against `Assets/GameClassNames/{Класс}.png`, closes stats. Class IS the identity (no roster, no per-character config — all clients share default keybinds from `AgentOptions`). `MasterClass` designates the master (skips assist); `IgnoredClasses` designates utility characters that drop all broadcasts.
- **Macro** system: `Macro.ActionsByClass` maps `CharacterClass → List<MacroAction>` (polymorphic JSON: key press / delay / click) so each class runs its own rotation from one broadcast. `MacroRunner` executes; `MacroLibrary` persists to `macros.json` with FileSystemWatcher hot-reload (own writes suppressed via LastWriteTime tracking).

### Win32 input model (the hard-won part — do not "simplify" without re-testing in game)

PW freezes background clients (input + rendering). Every input session is bracketed by `IGameWindow.ActivateAsync` (sends magic `WM_ACTIVATEAPP` with lParam from config) and `DeactivateAsync` (drain delay, then deactivate — unless window is foreground). `AgentInputDispatcher` wraps this lifecycle around every keypress/click/assist.

- **Keyboard = SendMessage, mouse = PostMessage** (`GameWindowFactory` bakes this in). Post'd keys got dropped by the frozen pump; clicks were always reliable.
- **Modifier chords (Shift+1) DO NOT WORK** via message injection: PW reads modifiers with `GetKeyState`, which cross-thread SendMessage never updates. That's why assist selects the master by *clicking* party-slot-1 (`ActivatingInputOptions.PartySlot1`) instead of Shift+1.
- **WM_SETICON must be SendMessage** — Post'd icon updates sit unprocessed in frozen queues. `ClassIconService` also retries at +2s/+5s because PW's post-boot init can reset the icon.
- Both the game and this app run elevated (`requireAdministrator` in app.manifest) — UIPI blocks input/capture into elevated windows otherwise.

### Vision

`IGameWindow.WaitForElementAt(template, region, timeout)` is the generic primitive: poll-loop of active capture (wake → PrintWindow with `PW_CLIENTONLY | PW_RENDERFULLCONTENT` → re-freeze) + grayscale `TM_CCOEFF_NORMED` matching. Grayscale, NOT binarized — game UI sits on semi-transparent backgrounds where binarization is unstable. `ClassMatcher` (stats-window text on solid panel) is the exception that still binarizes. Templates load from `Assets/GameUiElements/*.png` (keyed by filename stem, referenced by name in `AgentOptions`) and `Assets/GameClassNames/{enum}.png`. All coordinates/templates are currently pixel-exact for the author's 3840×2160 screen.

### Runtime state files (next to the exe, gitignored)

`hotkeys.json` (trigger + macro-name hotkey bindings, seeded from `HotkeyOptions` defaults), `macros.json`, `logs/`. Config stores follow one pattern (see `HotkeyConfigStore`): load on ctor → immutable snapshot → `ReplaceAsync` persists + raises changed-event → subscribers re-register live. Deserialization is two-stage (string-typed enums first) so stale entries from older versions skip-and-log instead of failing the file.

### Conventions

- Logging via source-generated `[LoggerMessage]` partial methods, usually split into a sibling `*.Logging.cs` partial file.
- Options classes bind from `appsettings.json` sections in `Program.cs` (`AgentOptions` ← `"Agent"`, `ActivatingInputOptions` ← `"Input:Activating"`, vision options ← `"Vision:*"`).
- `ScreenPoint` / `ScreenRect` record structs (in `Native`) for all pixel coordinates — bind from JSON as `{ "X": .., "Y": .. }` objects.
- Coordinate discovery workflow: user hovers cursor in-game and presses the BroadcastClick/DoubleClick hotkey; `CursorClickResolver` logs the client-space point, which then goes into config.
- "Dump captures" button in the main window writes per-agent `debug/*-full.png` and `*-class-bin.png` for tuning vision regions.

`docs/spec.md` (v0.4, Russian) is the up-to-date technical description — includes game-domain context, the full architecture, and a decision-history table explaining why earlier designs (FOLLOW/HOLD/COMBAT state machine, LLM integration, per-character roster, nameplate identification) were dropped. Keep it in sync with structural changes.
