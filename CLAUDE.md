# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Windows-only desktop automation tool (C# / .NET 10 / Avalonia). Generic in design, Perfect World in practice: it watches for windows of configured processes, tags them (free-form strings, typically applied by OpenCV template matching), and runs user-authored **macro graphs** against them via Win32 messages — driving ~10 `elementclient_64.exe` clients through login and party-wide actions. UI text and commit messages are in Russian; tags are Cyrillic class names ("Лучник", "Жрец") because that's what the game renders.

## ⚠️ In-flight refactoring — read before large changes

`docs/refactoring-plan-split.md` is the approved roadmap and is being executed wave by wave: rename to **SmartMacro**, macros as **node graphs** (actions + conditional nodes + variables) with triggers (hotkey / process-appeared), `CharacterClass`/roster dissolved into free-form **window tags** with tag-selector routing, then a split into a background **Daemon** (tray, hooks, vision, IPC server) + on-demand Avalonia **App** (named-pipe client).

**Done:** W0.0 (rename to SmartMacro + `SmartMacro.Tests`), W0.1 (`WindowRegistry`/tags/`ProcessProfiles`, `CharacterClass` and Stateless removed), W0.2a (node-graph model, `MacroExecutor`, validator), W0.2b (primitives over real input/vision, `macros/` folder storage + migration + PW examples, triggers driven by the macro library, legacy pipeline deleted), W0.3 (UI rebuilt on graphs), **stage 1** (`SmartMacro.Contracts`: graph model + validator moved out of Core, wire DTOs and IPC protocol types added), **stage 2** (`SmartMacro.Daemon`: Win32 tray + named-pipe IPC server), **stage 3** (App is a pure IPC client — Core reference gone, tray gone, closing the window exits), **stage 4A** (memory measured: daemon ~11 MB private / ~50 MB working set idle, five times under the ≤60 MB target; GC knobs gave nothing and were reverted; RID trim cut the daemon 169→107 MB and the panel 112→29 MB on disk), **stage 4B** (`docs/spec.md` rewritten to v0.5, dead-code sweep).

The refactoring plan is complete through stage 4. What follows is `docs/design/implementation-plan.md`, which breaks the approved UI mockup into waves **D1–D5**:

- **D1 done** — `App/Themes/{Tokens,FluentBridge,Controls}.axaml`. Nocturne tokens as `ResourceDictionary` + `ControlTheme`s; no literal hex belongs in markup any more. Cross-dictionary references are `{DynamicResource}` — `{StaticResource}` across dictionaries is fragile here.
- **D2 done** — the 1b shell. One window: 30px custom title bar (`ExtendClientAreaToDecorationsHint`, because the OS bar stays light regardless of `RequestedThemeVariant`), 172px mode rail (Окна / Макросы / Прогоны / Шаблоны / Лог) with a tag summary pinned at its foot, 30px run bar always present. `MacrosDialog` is gone — the editor is a mode. `MainWindowViewModel` → `WorkspaceViewModel`; `ShellViewModel` owns modes and derives every counter. Views toggle by `IsVisible` rather than swapping through a `ContentControl`, so a half-typed tag and the editor's dirty state survive a mode round trip. Hotkey suspension is scoped to the «Макросы» mode being selected, and closing the window waits for the resume — **the daemon does not re-register hotkeys when a client drops.**
- **D3a done** — the canvas editor. `App/ViewModels/Canvas/` + `App/Controls/{EdgeLayer,CanvasBackdrop}.cs`; the rows editor is deleted. Boxes as wrapping rows, edges routed in the gutters, conditional outcomes as labelled rows on the box. An **unwired outcome produces no edge and no phantom terminal node** — ending a branch is a legitimate end of the macro. Single click fills the inspector (1d), double click expands the box in place (1e). Auto-layout DFS from the start node; graphs saved before this wave get positions on load. Library groups by prefix: text before the first `-`, and a prefix shared by **two or more** macros becomes a group (the mockup's «Баг госта · 5» holds both `Баг госта-Лучник` and plain `Баг госта`, so splitting strictly on the dash would break a family).
- **D3b done** — the run-event channel. See below; it is the foundation D5 and the «Лог» mode both build on.
- **D4 done** — hotkey capture (1f) + target-filter badge (1g). See below.
- **D5 done** — the debugger. See below. **The design waves are complete.**

### The targets badge and the hotkey trap (D4)

**The badge needed no new IPC request**, despite what the plan's table said. The matching rule moved out of `SelectorEvaluator` into `TargetSelector.Matches` (Contracts) and now takes TAGS rather than a window, so both processes share one implementation: the daemon still calls `SelectorEvaluator` (kept as the typed wrapper — it is where `ManagedWindowInfo` is known, and Contracts must not know it), the panel calls `Matches` directly against its own `WindowDto` snapshot. **Do not reimplement those two loops anywhere.** A badge that disagrees with what the executor actually targets is the one defect that makes the widget worse than nothing.

`MacroEditorViewModel` keeps its own `WindowCatalog` — seeded by `GetWindows`, kept current by the window pushes — and hands it to every node's `TargetSelectorViewModel` the same way it hands out `NodeIdChoices`. It is deliberately NOT `WorkspaceViewModel`'s list: that one holds editable rows with focus state, and injecting it would tie two modes together.

Two departures from the mockup, both from looking at it running:
- **Zero matches is an error; "no windows at all" is not.** With the game closed every selector matches nothing, and painting every node of every macro red teaches the user to ignore the colour. The empty daemon gets a quiet «нет окон».
- **The canvas box shows the count only** («7 окон»); the tags are in the tooltip and in the inspector's badge. The full string pushes the type label out of a 210px header, and the label is what says what the node does.

**Hotkey conflicts are two different things.** «уже занят pw-immunity» is a library clash and the panel answers it alone. A `RegisterHotKey` refusal — another application owns the chord — is visible only to the daemon, and before D4 it went to a log file the pipe does not carry: the user bound a key, saw a clean UI, and nothing ever happened. `GetHotkeyFailures` closes that. It is a **pull, not a push**: the list changes only when the daemon (re-)registers, and the panel is what causes that, so it re-reads on `Connected`, on `MacrosChanged`, and after `ResumeHotkeys` answers — which is the load-bearing one, because hotkeys are suspended for the whole time «Макросы» is on screen and a chord bound in the editor is only tried on the way out. `HotkeyListener.Failures` deliberately survives a suspend. A ⚠ on the library row reports the same thing for macros nobody opened.

`KeyBindingPicker` was **evolved, not replaced**: the capture semantics (Esc/Del, mouse thumb buttons, ignoring bare modifiers) are fiddly and hand-tested, and are untouched. What changed is that a `Button` whose `Content` was the string "Ctrl+Shift+F1" became a templated control with a keycap strip and four states as pseudoclasses (`:unbound` / `:capturing` / `:conflict`).

### Run events (D3b)

`MacroExecutor` reports progress through `IMacroRunObserver`; `Core/Ipc/RunEventPublisher` turns that into the `RunEvents` push. Four facts that constrain anything built on top:

- **The unit is a WALK, not a run.** `RunMacroNode` with a selector forks one executor walk per window and they all share one `RunId` — a run id cannot tell them apart. Each walk gets its own id at `MacroWalkTrace.Begin`, and that walk id is the correlation key on every event. The canvas therefore has a walk picker and lights the node of the *selected* walk; otherwise a ten-window fan-out would light ten boxes on one graph.
- **Nothing is produced unless someone subscribed** (`SubscribeRunEvents`). `IsEnabled` is one volatile read per node, checked in the walker before it times anything or formats a detail string — not merely documented there. The daemon is resident and the panel is not, so unsubscribed is the normal state and must cost nothing.
- **Events are coalesced into batches, at most one envelope per 50 ms.** This is not an optimisation. `IpcServer` gives each connection a 256-deep queue and **drops a client that stops draining**; a ten-window fan-out is several hundred events in a few hundred milliseconds, so unbatched the panel would be dropped exactly when the user is watching. Measured: 344 events, one envelope, zero loss.
- **Overflow is counted and reported, never silent.** The queue is bounded and `TryWrite` failures ride out as `RunEventBatch.Dropped` so the panel can say the log has a hole. The engine must never block — a walk runs between two Win32 messages to a live game.

A panel connecting mid-run gets the live walks with `FromStart = false` and says so. There is deliberately no per-run history buffer: between a drop and a reconnect nobody was subscribed, so recording had stopped and a buffer would be stale.

### The debugger (D5)

`Core/Macros/Execution/MacroDebugSession` is the whole engine side: breakpoints, per-walk pause state, and the attach count. It reaches the walker as `MacroRunContext.Debugger` — the control sibling of `Observer`, with the same `IsActive` gate, so an undebugged walk pays one volatile read per node and allocates nothing.

**The gate is between two nodes, and that is the safety argument, not a convenience.** Every `ActivateAsync`/`DeactivateAsync` bracket and every vision tick's wake/re-freeze lives entirely inside `IMacroPrimitives`, so by the time control is back in `MacroExecutor` no game window is left woken. Pausing there cannot strand a frozen client; pausing anywhere deeper could. `ABreakpointParksTheWalkBeforeTheNodeRuns` pins it with a primitive-call count.

**Breakpoints live in the daemon's session, never in the macro file.** The reasoning is written where the storage is (`MacroDebugSession`): a breakpoint is a fact about a debugging session, and persisting one would put it in a diff, ship it with the `pw-*` examples, and make a red dot dirty the editor. The ergonomic half of persistence comes free from the split — the daemon outlives the panel, so a breakpoint survives closing and reopening the UI. It does not survive «Выход» from the tray, which is also when every tag, hotkey and run goes.

**A parked walk must never outlive its audience.** A walk waiting in the gate holds its `MacroRunRegistry` single-flight slot, so that macro's hotkey is dead until it moves — unacceptable in a resident daemon driving a live game. The attach count is therefore the *same edge* as `SubscribeRunEvents` (`IpcServer.ClientConnection.SetRunEventSubscription` drives both), which the server already releases on disconnect. The last debugger leaving **auto-resumes every parked walk** and stops breakpoints biting. Resume rather than abort: the run was started legitimately and abandoning a macro halfway can leave the game worse off. There is deliberately **no inactivity timeout** — the only case left is "the panel is open and the user walked away", where the pause is doing its job.

**Stop is per-RUN; pause and step are per-WALK.** A fan-out is N walks on one cancellation token, and nobody hitting ■ while ten clients are being driven means "stop one of them". The panel is required to label it: the button reads «■ Стоп ×3». Cancellation unparks a gated walk, so ■ (and daemon shutdown) work on a paused one.

Four `RunEventKind` members were added rather than new message types, exactly as D3b laid out: `Paused`, `BreakpointHit`, `Resumed`, `VariableSet`. The first three **bypass the 50 ms coalescing window** — a step that pays a full dwell feels like a stuck button. The bypass is a signal, not a flag: a flag read once at the top of the pump loop never fired, because a breakpoint hit is always preceded by `WalkStarted`/`NodeEntered` in the same millisecond and the dwell had already begun (measured 63 ms; single digits after).

**`MacroVariableAnalysis` (Contracts, next to the validator)** is the static half of the variables panel: who writes each variable, who reads it, and in which slot — including `{var}` interpolated inside strings, using the *same* `MacroVariableNames.Placeholder()` regex the executor substitutes with, because a panel claiming a read the executor never performs is the D4 targets-badge lie again. The live value is a separate concern and arrives as `VariableSet`; parsing it out of a `NodeExited` detail would mean the panel parsing a string whose format is the daemon's, and would still never see `cursor`, which no node writes.

Stage 5 (translating comments to Russian) is deliberately last.

**Шаблоны and Лог modes have no data behind them** and render an honest empty state with a blank counter, pinned by a test. `templates/` is not listed or fetchable over IPC, and the daemon's Serilog output never crosses the pipe. Both need protocol additions; «Лог» would be a second opt-in subscription reusing D3b's batch shape.

**Two executables run elevated** (`requireAdministrator`), so a medium-integrity shell cannot terminate either one — `Stop-Process`/`taskkill` return access denied. A wedged panel has to be closed from an elevated context. It also holds the single-instance mutex and renames locked DLLs to `*.locked<pid>` in its `bin/`; those clear themselves when it finally exits.

**The split is live.** `SmartMacro.Daemon.exe` is the resident engine (tray, hooks, vision, macro library, IPC server); `SmartMacro.App.exe` is an on-demand panel that owns nothing and reaches everything over the `smartmacro-control` pipe. Run the daemon; the tray's "Открыть панель" (or launching the App directly) brings the UI up. A second App launch does not open a second window — it asks the daemon to broadcast `ActivateWindow` and exits. If the daemon dies, the panel says so and closes.

## Commands

```bash
dotnet build SmartMacro.slnx                  # full solution
dotnet run --project src/SmartMacro.Daemon    # the engine — start this first
dotnet run --project src/SmartMacro.App       # the panel (also auto-starts the daemon if it isn't up)
dotnet run --project tools/VisionSampleRunner # vision debugging harness (coord OCR over samples/)
dotnet run --project tests/SmartMacro.Tests   # TEST GATE — use this one
```

`dotnet test` (bare, from the repo root) also works now and reports the full count; note that passing the solution needs `dotnet test --solution SmartMacro.slnx`, not a positional path. `dotnet run` remains the gate of record.

Build warnings NU1903 (Tmds.DBus.Protocol) are known noise.

**DLL-lock gotcha:** if either executable is running, `dotnet build` fails copying DLLs (MSB3027/MSB3021 with a PID). Close the panel window AND pick "Выход" in the daemon's tray icon, then rebuild — the daemon outlives the panel by design, so closing the window alone is not enough. Code-compile errors vs file-lock errors look similar in output — check before diagnosing.

**Config propagation gotcha:** `appsettings.json` is copied to `bin/Debug/net10.0-windows/` only on build (`PreserveNewest`). Editing the source-tree JSON and restarting the exe without a rebuild does NOT pick up changes. Options are bound once at startup via `IOptions` — every config change requires app restart.

## Architecture

Five projects, two executables:

- `Daemon` (WinExe, tray + hosted engine) → `Core` (all domain logic) → `Contracts` → `Native`
- `App` (Avalonia panel) → `Contracts` → `Native` — **and nothing else.** No `SmartMacro.Core` reference: that is the load-bearing constraint of the split, and the reason the panel's output directory contains no OpenCV, no Tesseract and no native vision blobs. If a view-model needs something from Core, the answer is a new IPC message type, not a reference.

`Native` is Win32 P/Invoke via `LibraryImport`, no dependencies.

**`SmartMacro.Contracts`** is what the two processes speak: the macro graph model (`SmartMacro.Macros.Model`) and its pure validator (`SmartMacro.Macros.Validation`) — namespaces deliberately kept as they were when these lived in Core — plus `SmartMacro.Contracts.Dto` (`WindowDto`, `RunningMacroDto`, `ValidationIssueDto`) and `SmartMacro.Contracts.Ipc` (envelope, `IpcMessageTypes` catalog, `IpcJson`, `IpcPipe`, and the shared `IpcConnection` framing both ends use). **It references Native and nothing else.** Anything needing OpenCV/Tesseract/file IO belongs in Core; mappers from live Core types to DTOs therefore live in `Core/Ipc/DtoMappers.cs`, not in Contracts.

### IPC

`Core/Ipc/IpcServer` accepts on the named pipe and fans engine events out; `Core/Ipc/IpcRequestDispatcher` turns one envelope into one envelope. `App/Ipc/IpcClient` is the other end: a single reader loop demultiplexing `Id`-bearing responses into pending `TaskCompletionSource`s versus raising events, with a reconnect loop for the process's lifetime.

Three protocol facts every caller has to respect:

- **Responses may arrive out of order.** The server does not await a handler before reading the next request. Correlate by `Id`; never by arrival.
- **A client that stops draining events is dropped.** So `IIpcClient.Connected` is a re-fetch signal, not a nicety — every view-model re-seeds its snapshots there, and reconciles (drops rows the daemon no longer reports) rather than merely upserting.
- **`SaveMacro` → `[]` means saved.** A non-empty issue list means nothing was written. Warnings on a *successful* save are NOT returned, so the editor re-derives them by running `MacroGraphValidator` locally.

`IIpcBroadcaster` (Core) exists so `RequestActivate` and the tray can push `ActivateWindow` without a DI cycle: `IpcServer` implements it and hands itself to the dispatcher in its own constructor.

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
- **Macros** — the model (polymorphic `$type` nodes + triggers) and its validator now live in `Contracts/Macros`; `Core/Macros` keeps the daemon-side halves: `Execution` (`MacroExecutor` walker, `MacroPrimitives`, `MacroRunRegistry`, run variables) and `Storage`. See `docs/refactoring-plan-split.md` §0.2–0.3 for the node catalogue and semantics.
- **`MacroGraphStore`** (`Core/Macros/Storage`) is the library of record: one JSON file per graph under `macros/`, filename stem = macro name. It resolves sub-macros for `RunMacroNode`, supplies `HotkeyListener`'s bindings (re-registered on every change), and tells the orchestrator which graphs a new process should boot. On first run it migrates a legacy `macros.json` (+ `hotkeys.json` macro bindings → triggers) and seeds the six `pw-*` examples. Seeding is gated by the marker file `macros/.examples-seeded`, NOT by the folder being empty — deleting an example does not bring it back, and the `SeedDefaultsIfEmpty` name understates this.
- **Identification** is no longer built in: it's the `pw-identify` / `pw-boot` example macros — `KeyPress(C)` → `Delay` → `RecognizeTagNode` (template set `"classes"` → `Assets/GameClassNames/{tag}.png`) → `SetIconNode` → `KeyPress(C)`. Master/ignored characters are just tags in a selector (`ExcludeTags: ["Лучник", "Шаман"]`).

### Win32 input model (the hard-won part — do not "simplify" without re-testing in game)

PW freezes background clients (input + rendering). Every input session is bracketed by `IGameWindow.ActivateAsync` (sends magic `WM_ACTIVATEAPP` with lParam from the window's `ProcessProfile`) and `DeactivateAsync` (drain delay, then deactivate — unless window is foreground). `AgentInputDispatcher` wraps this lifecycle around every keypress/click, one cycle per node.

- **Keyboard = SendMessage, mouse = PostMessage** (`GameWindowFactory` bakes this in). Post'd keys got dropped by the frozen pump; clicks were always reliable.
- **Modifier chords (Shift+1) DO NOT WORK** via message injection: PW reads modifiers with `GetKeyState`, which cross-thread SendMessage never updates. That's why the `pw-assist` example selects the master by *clicking* party-slot-1 instead of Shift+1. There is deliberately no chord node and no chord primitive — stage 4B deleted the callerless `PressChordAsync`/`SendChordAsync` rather than leave working-looking code that silently no-ops in game. Do not add them back.
- **WM_SETICON must be SendMessage** — Post'd icon updates sit unprocessed in frozen queues. `WindowIconService` also retries at +2s/+5s because PW's post-boot init can reset the icon.
- Both the game and this app run elevated (`requireAdministrator` in app.manifest) — UIPI blocks input/capture into elevated windows otherwise.

### Vision

`IGameWindow.FindElementAsync` (one shot) and `WaitForElementAsync` (poll until timeout) are the generic primitives; both return the CLIENT-SPACE CENTRE of the match, which is what `FoundPointVar` feeds to a later `ClickNode`. Each tick is an active capture (wake → PrintWindow with `PW_CLIENTONLY | PW_RENDERFULLCONTENT` → re-freeze) + grayscale `TM_CCOEFF_NORMED` matching. Grayscale, NOT binarized — game UI sits on semi-transparent backgrounds where binarization is unstable. `ClassMatcher` (stats-window text on solid panel) is the exception that still binarizes.

`TemplateSetProvider` resolves the names nodes carry into bytes: single templates by stem from `Assets/GameUiElements`, and the set `"classes"` from `Assets/GameClassNames` (a compatibility alias — TODO W0.4 collapses both into one `templates/` tree). All coordinates/templates are pixel-exact for the author's 3840×2160 screen; they now live in macro nodes, not config.

**Assets live with the daemon.** `src/SmartMacro.Daemon/Assets/**` — vision templates, per-tag `ClassIcons`, the tray `icon.ico`. Stage 3 moved them out of App because every reader of them runs in the daemon; App keeps exactly one, `Assets/icon.png`, embedded as an `AvaloniaResource` for the window icon.

**Tesseract is parked and no longer ships with the daemon (stage 4B).** `TesseractCoordinateReader` (HUD coordinate OCR) still works and is still exercised by `tools/VisionSampleRunner`, but the daemon does not register `ICoordinateReader` — nothing injected it, and the ctor eagerly builds a `TesseractEngine`. `SmartMacro.Core.csproj` marks the package `PrivateAssets="all" ExcludeAssets="build"`, which keeps both the managed dll and the 12 MB of `x64/`+`x86/` natives out of the daemon's output; `src/SmartMacro.Daemon/tessdata/` is gone and the language pack lives only with the sample runner, which carries its own PackageReference. Daemon output: 107 → 91 MB. Un-parking it for stuck detection = drop those two attributes and restore the DI line. ⚠️ Until then `SmartMacro.Core.dll` ships next to the daemon with a metadata reference to an assembly that is not there — harmless because the CLR resolves it lazily and nothing touches that type.

### Runtime state files (next to the DAEMON exe, gitignored)

`macros/*.json` (one graph per file), `debug/` and `logs/smartmacro-*.log`. `MacroGraphStore` follows the usual store pattern — load on ctor → immutable snapshot → CRUD persists + raises `MacrosChanged` → subscribers (`HotkeyListener`, and the panel via the `MacrosChanged` push) re-register live — plus a debounced `FileSystemWatcher` for external edits, with our own writes suppressed by comparing a folder signature of last-write timestamps. An unparseable file is skipped and logged, never fatal to the load.

The panel's own directory holds only `logs/smartmacro-ui-*.log`. Its `appsettings.json` configures Serilog and nothing else — every engine knob (`Agent`, `ProcessProfiles`, `Vision:*`) is the daemon's.

`hotkeys.json` and the single `macros.json` are GONE. Both are migrated once on first run and renamed to `*.migrated`; a hotkey is now a `HotkeyTrigger` inside the macro it starts. Legacy bindings for the deleted built-in broadcast actions have no destination and are reported as orphaned in the log — the equivalent behaviors are the `pw-*` example macros.

### Conventions

- Logging via source-generated `[LoggerMessage]` partial methods, usually split into a sibling `*.Logging.cs` partial file.
- Options classes bind from `appsettings.json` sections in `Program.cs` (`AgentOptions` ← `"Agent"` — poll intervals only now, `ProcessProfileOptions` ← `"ProcessProfiles"`, vision options ← `"Vision:*"`). Coordinates, regions, templates, keys and timeouts belong in macro nodes, NOT in config.
- `ScreenPoint` / `ScreenRect` record structs (in `Native`) for all pixel coordinates — bind from JSON as `{ "X": .., "Y": .. }` objects.
- Coordinate discovery workflow: user hovers cursor in-game and triggers a macro; `CursorPositionProvider` logs the client-space point it seeds the `cursor` variable with, which then goes into a node.
- "Dump captures" in the «Окна» mode header sends `DumpCaptures` (with a generous timeout — it screenshots every client) and opens the folder the daemon replies with: per-agent `debug/*-full.png` and `*-class-bin.png` for tuning vision regions.
- View-models take `IIpcClient` + `IUiDispatcher` and nothing Avalonia-shaped, so every one of them is exercised headlessly against `FakeIpcClient` (`tests/SmartMacro.Tests/Ipc/`), whose canned answers go through the real `IpcJson` round trip.

`docs/spec.md` (v0.5, Russian) is current as of stage 4B and describes the system as built — including a §13 that lists what the design mockup shows but the code does not have (canvas, debugger, run-event stream, variable analysis). Its most load-bearing section is §14, the decision history: two tables of "было → стало → почему" covering both the pre-refactor drops (FOLLOW/HOLD/COMBAT state machine, LLM integration, per-character roster, nameplate identification) and the refactor's own (classes → tags, action lists → graphs, built-in commands → macros, `macros.json` → folder, `hotkeys.json` → triggers, single process → daemon + panel). Read it before proposing anything that sounds like a fresh idea.
