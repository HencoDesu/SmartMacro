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
- **After D5** — the asset trees collapsed into one `Assets/templates/`, and «Шаблоны» stopped being an empty frame. See «Vision» and «The template browser» below. Then **example seeding and the legacy migrator were deleted**: backwards compatibility is off, so `MacroGraphStore`'s constructor no longer writes anything. (The handout folder that briefly replaced the seeding is gone too — see «Runtime state files».) And **«Лог» stopped being an empty frame** — the daemon's Serilog output now crosses the pipe as a second opt-in subscription. See «The log feed» below.

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

**Breakpoints live in the daemon's session, never in the macro file.** The reasoning is written where the storage is (`MacroDebugSession`): a breakpoint is a fact about a debugging session, and persisting one would put it in a diff, travel with any macro the user shares, and make a red dot dirty the editor. The ergonomic half of persistence comes free from the split — the daemon outlives the panel, so a breakpoint survives closing and reopening the UI. It does not survive «Выход» from the tray, which is also when every tag, hotkey and run goes.

**A parked walk must never outlive its audience.** A walk waiting in the gate holds its `MacroRunRegistry` single-flight slot, so that macro's hotkey is dead until it moves — unacceptable in a resident daemon driving a live game. The attach count is therefore the *same edge* as `SubscribeRunEvents` (`IpcServer.ClientConnection.SetRunEventSubscription` drives both), which the server already releases on disconnect. The last debugger leaving **auto-resumes every parked walk** and stops breakpoints biting. Resume rather than abort: the run was started legitimately and abandoning a macro halfway can leave the game worse off. There is deliberately **no inactivity timeout** — the only case left is "the panel is open and the user walked away", where the pause is doing its job.

**Stop is per-RUN; pause and step are per-WALK.** A fan-out is N walks on one cancellation token, and nobody hitting ■ while ten clients are being driven means "stop one of them". The panel is required to label it: the button reads «■ Стоп ×3». Cancellation unparks a gated walk, so ■ (and daemon shutdown) work on a paused one.

Four `RunEventKind` members were added rather than new message types, exactly as D3b laid out: `Paused`, `BreakpointHit`, `Resumed`, `VariableSet`. The first three **bypass the 50 ms coalescing window** — a step that pays a full dwell feels like a stuck button. The bypass is a signal, not a flag: a flag read once at the top of the pump loop never fired, because a breakpoint hit is always preceded by `WalkStarted`/`NodeEntered` in the same millisecond and the dwell had already begun (measured 63 ms; single digits after).

**`MacroVariableAnalysis` (Contracts, next to the validator)** is the static half of the variables panel: who writes each variable, who reads it, and in which slot — including `{var}` interpolated inside strings, using the *same* `MacroVariableNames.Placeholder()` regex the executor substitutes with, because a panel claiming a read the executor never performs is the D4 targets-badge lie again. The live value is a separate concern and arrives as `VariableSet`; parsing it out of a `NodeExited` detail would mean the panel parsing a string whose format is the daemon's, and would still never see `cursor`, which no node writes.

Stage 5 (translating comments to Russian) is deliberately last.

### The template browser (Шаблоны)

Two requests, `GetTemplates` (metadata) and `GetTemplateImage` (bytes of one file), plus a `Contracts/Macros/Analysis/MacroTemplateAnalysis` that the panel runs locally. Four things constrain anything built on it:

- **The list is metadata; pixels are not.** One `TemplateDto` is tens of bytes, so the whole tree arrives in one request on entering the mode. PNGs are kilobytes each and the pipe is shared with the run-event stream — `IpcConnection` serialises writes, so a batch of images sent "just in case" would queue ahead of a live macro's events. Bytes therefore travel one file at a time, on selection, cached for the life of the mode. **There are deliberately no thumbnails in the rows** — that is precisely the shape that would pull the whole tree at once.
- **`TemplateLimits.MaxImageBytes` (1 MB) is checked on both ends.** The panel already knows the file size from the list, so it never asks for something that would be refused; the daemon refuses anyway, because the file can change between the listing and the request. `TemplateImageDto` echoes the request — not for correlation (that is the envelope `Id`) but against the *selection race*: the user clicks faster than the daemon answers, and without the echo the preview would keep the previous template's image.
- **"Which macros need this template" is computed in the panel, with no new request.** `MacroTemplateAnalysis` is the D4 targets-badge move again: the library is already there, the names are in `FindElementNode`/`WaitForElementNode`/`RecognizeTagNode`, one implementation of the rule and not two. A `RecognizeTag` reference names the SET, so it is credited to every file in it — otherwise "Лучник.png is unused" would be a lie about the template that identifies an archer. **Template names are NOT interpolated** (unlike tag / icon path / macro name), so `{var}` in one is part of the name, exactly as the executor treats it.
- The reverse falls out for free: **a node naming a template that does not exist** gets its own «НЕТ ФАЙЛА · N» section. Before this it surfaced only mid-run, as a line in the daemon's log.

`GetTemplates` is a pull, but for a different reason than `GetHotkeyFailures`: the tree is changed by the user in Explorer, not by the panel, and the `FileSystemWatcher` watches `macros/`, not `Assets/`. So the list is re-read on entering the mode and on «Обновить».

### The log feed (Лог)

One request (`SubscribeLog`) and one push (`LogEntries`) — the **second subscription on D3b's channel**, deliberately reusing its batch shape rather than inventing a mechanism. `Core/Ipc/LogEventPublisher` is the engine half (ring + queue + pump + broadcast); `Daemon/Logging/IpcLogSink` is the Serilog adapter, and it lives in the daemon because **Core has no Serilog reference** and must not grow one. Four things constrain anything built on it:

- **Recursion is cut by construction, in three places.** A sink that ships records over IPC lives inside the process that logs. (1) *Neither the sink nor the publisher has a logger* — and cannot be given one, because the logger is built from the very Serilog pipeline the sink is plugged into, so injecting it is a DI cycle that fails at host build. The emit path is `Append` → ring + `TryWrite`, with no place a second record could come from. (2) The pump sets an `AsyncLocal` suppression flag around the *synchronous* broadcast, which covers the one thing on that path that does log — `IpcServer`'s backlogged-client warning. The record still reaches console and file; only the feed drops it. (3) The 100 ms dwell is the backstop: an escapee logged from another task costs one extra line per flush window, never a loop, because the pump cannot outrun its own timer.
- **The ring buffer exists here and deliberately does not for run events.** Run events have no history because nothing is recorded while unsubscribed, so a buffer would be stale. The log is written whether or not anyone is watching, and «Лог» is opened precisely to see what just happened — so the daemon keeps `LogLimits.HistoryCapacity` (1000) entries always, and `SubscribeLog` answers with them. The cost is named: rendering + storing every record is paid with the panel closed.
- **`SubscribeLog` enables the stream BEFORE snapshotting the ring.** The other order leaves an unfillable gap; this one can only duplicate the single record that lands between the two lines, and `LogEntryDto.Seq` lets the panel drop it. Reconnect **replaces** the feed rather than appending — `Seq` is only monotonic within one daemon lifetime.
- **The subscription lives as long as the panel, not as long as the mode** — the one place this departs from run events (which are scoped to «Макросы»). The question the log answers is "did something go wrong", and it is asked from whatever mode you are in; a counter that only updates while you are looking at it is not a signal. So the rail counter is **problems (Warning+) in the feed**, the only counter that counts a subset of its rows: total entries saturate at the cap and a constant is not information.

Filtering (level + substring) is **panel-side only**, because moving the filter down must *reveal* what already arrived — something a server-side filter cannot do. The lever that actually reduces wire volume is the daemon's own Serilog `MinimumLevel`; a `LoggingLevelSwitch` for it belongs to a settings screen and is not wired.

### Settings, and the live settings store (D6)

The screen is mockup **2c** (one screen, no scrolling, tiles in the shell, diagnostics as a strip along the bottom), and «Настройки» is a sixth rail row **below a divider, with a gear and no counter** — rail counters answer "how many are there now", and settings have no such number; a red dot for failed diagnostics takes that slot instead.

**The mechanism mattered more than the field list.** Every engine knob used to arrive through `IOptions<T>`, computed once at startup, so any change needed a daemon restart — a trap `CLAUDE.md` documented separately. A settings screen on top of that would have been more annoying than Notepad. So `Agent`, `ProcessProfiles` and `Vision` left `appsettings.json` for `Core/Settings/SettingsStore` — **the `MacroGraphStore` pattern, deliberately, down to the debounced `FileSystemWatcher` and own-write suppression**: the daemon owns `settings.json`, the panel edits it over IPC, the daemon raises `SettingsChanged`, subscribers re-read. `appsettings.json` keeps Serilog and nothing else.

Four facts constrain anything built on it:

- **Consumers read `ISettingsSource.Current` AT THE POINT OF USE and never cache it in a field.** That single rule is what makes the knobs live: poll intervals apply on the next tick, vision thresholds on the next match, input method on the next activation cycle, profiles to newly adopted windows. The snapshot is immutable, so a read is one reference. *Verified live*: a profile added by editing the file in a text editor was picked up by the running daemon, `ProcessMonitor` started watching the new process on its next tick, and the window appeared in «Окна» — no restart anywhere.
- **`SettingsStore`'s constructor WRITES when the file is missing** — the one deliberate departure from `MacroGraphStore`, whose "constructor writes nothing" is an invariant. The reasoning inverts: the daemon must work without the panel (the panel is on-demand and may be absent for days), so a first launch on a new machine cannot require "open the panel and press Save". An *existing* file is never rewritten, and an **unreadable** one is neither rewritten nor allowed to reset anything — the user put a stray comma in while editing by hand, and "the program silently restored factory defaults" is losing their work.
- **The log level stays in `appsettings.json`, against the general rule of shrinking it.** If the daemon trips on reading the settings file, the only level that can debug that is the one known BEFORE the read; a setting that breaks the diagnosis of its own breakage is the worst kind. So `SetLogLevel` moves a `LoggingLevelSwitch` and persists nothing, the change lasts until the daemon restarts, and the screen says so. `MinimumLevel.ControlledBy` must come AFTER `ReadFrom.Configuration` — the other order and the knob silently stops working.
- **The editor stages; the store is live.** Edits accumulate and go on «Применить». Applying per keystroke would mean "12" is briefly "1", and the daemon would honestly work a second at a one-second interval. Live means "no restart needed", not "state changes under your fingers". The log level is the single exception (it is not part of the file). A `SettingsChanged` push — which also fires for a Notepad edit — **does not clobber a half-filled form**; it swaps the baseline so «Отменить» shows the new file.

**`ActivationLParam` is not in the UI at all** — only "есть"/"не нужен". It is a reverse-engineered crutch for one game, not a setting. But its ABSENCE is working semantics ("ordinary process, wake-up skipped"), which non-game profiles depend on, so `KnownActivationSignals` is applied **when a profile is CREATED**, never on read: `elementclient_64` gets 37336 by itself, `notepad` stays without one, and the row carries the value through untouched. Anyone with a build using a different value still has the file.

**Autostart is the only part applied outside the process**, and it is two checkboxes over three mechanisms, the app choosing: nothing / `Run` key in `HKCU` / (no autostart, elevation requested at manual start) / **Scheduled Task with highest privileges** — because the `Run` key cannot elevate. Reconciliation is always FULL, including removing what should not be there, or switching ✓/— → ✓/✓ would leave both and the daemon would launch twice. `StartupReconciler` also reconciles at startup, not only on edit, so an install whose file says "run at logon" but whose registry entry was swept away fixes itself. **Registration can FAIL** (only an administrator can create that task) — and that used to reach a log nobody reads, which is exactly the D4 hotkey defect: box ticked, clean UI, nothing happens. It is now a diagnostic.

**Diagnostics («Проверить сейчас») are six checks, five in the daemon and one in the panel** — the channel round-trip, because the daemon cannot honestly measure its own response time. They exist because almost every failure of this application is environmental and all of them look identical: the macro just doesn't work, and the log is silent. The elevation check is a **real `WM_NULL` send per window**, not an inference from "we are not elevated": if the game is also running unelevated everything works, and a false alarm teaches people to ignore the strip. All checks are read-only; the most "active" one sends `WM_NULL`. Problems are sorted FIRST — a failure card wedged between two green pills is as good as absent.

**`SendInput` has a slot in the model and no implementation, and the UI does not offer it.** Offering an untested input method is worse than not offering one. A value that reaches the file by hand resolves to `SendMessage` with a warning logged once per process — never silently, or the symptom would be "a setting that does nothing". Input-method wording lives once, in `Contracts/Settings/InputMethodInfo`: the default-input list and the per-profile picker both read it, because a copy in markup drifts invisibly.

Found by eye while running it, none of it visible to build or tests: `ScreenRect`'s computed `TopLeft`/`Right`/`Bottom` were being SERIALIZED into every hand-edited file (settings and `macros/*.json` alike) — three fields that look settable and silently do nothing, now `[JsonIgnore]`; a checkbox whose two-line hint sat inside its content put the box next to the *second* line; the change counter had no home in the markup; «1 окон».

⚠️ **The fixed-height `TextBox` trap.** The theme's field padding is 11.2px vertical and the text sits inside a clipping `ScrollViewer`; a caller-supplied `Height` of 24–26 leaves less room than the line needs and severs descenders exactly at the baseline — letters stay legible, only the tails of «р»/«у»/«д» vanish. Three sites had it (log search, macro name, library search); all now pass `Padding="8,0"` + `VerticalContentAlignment="Center"` alongside their `Height`, and the trap is written up at the theme. Build and tests cannot see this class of defect — it was found by measuring glyph ink rows against a reference `TextBlock`.

**Two executables run elevated** (`requireAdministrator`) — and they stay two; see «The shipped layout» for why merging them is not on the table. A medium-integrity shell cannot terminate either one — `Stop-Process`/`taskkill` return access denied. A wedged panel has to be closed from an elevated context. It also holds the single-instance mutex and renames locked DLLs to `*.locked<pid>` in its `bin/`; those clear themselves when it finally exits.

**The split is live.** `SmartMacro.Daemon.exe` is the resident engine (tray, hooks, vision, macro library, IPC server); `SmartMacro.exe` (the panel project is still `SmartMacro.App`) is an on-demand panel that owns nothing and reaches everything over the `smartmacro-control` pipe. Run the daemon; the tray's "Открыть панель" (or launching the App directly) brings the UI up. A second App launch does not open a second window — it asks the daemon to broadcast `ActivateWindow` and exits. If the daemon dies, the panel says so and closes.

## Commands

```bash
dotnet build SmartMacro.slnx                  # full solution
dotnet run --project src/SmartMacro.Daemon    # the engine — start this first
dotnet run --project src/SmartMacro.App       # the panel (also auto-starts the daemon if it isn't up)
dotnet run --project tools/VisionSampleRunner # vision debugging harness (coord OCR over samples/)
dotnet run --project tests/SmartMacro.Tests   # TEST GATE — use this one

dotnet msbuild build/portable.proj            # shipped layout into dist/portable/SmartMacro/ (panel at the root, daemon in daemon/)
dotnet msbuild build/portable.proj -t:Package # …plus dist/SmartMacro-Release.zip
```

`dotnet test` (bare, from the repo root) also works now and reports the full count; note that passing the solution needs `dotnet test --solution SmartMacro.slnx`, not a positional path. `dotnet run` remains the gate of record.

Build warnings NU1903 (Tmds.DBus.Protocol) are known noise.

**DLL-lock gotcha:** if either executable is running, `dotnet build` fails copying DLLs (MSB3027/MSB3021 with a PID). Close the panel window AND pick "Выход" in the daemon's tray icon, then rebuild — the daemon outlives the panel by design, so closing the window alone is not enough. Code-compile errors vs file-lock errors look similar in output — check before diagnosing.

**Config propagation gotcha — now only for `appsettings.json`:** it is copied to `bin/Debug/net10.0-windows/` only on build (`PreserveNewest`), so editing the source-tree JSON and restarting the exe without a rebuild does NOT pick up changes, and its one remaining section (Serilog) is still read once at startup. **`settings.json` is the opposite in every respect** — it lives next to the exe, is gitignored, is created by the daemon on first run, and is watched: editing it in Notepad applies without a restart. Every engine knob moved there in D6; see «Settings» above.

## Architecture

Five projects, two executables:

- `Daemon` (WinExe, tray + hosted engine) → `Core` (all domain logic) → `Contracts` → `Native`
- `App` (Avalonia panel) → `Contracts` → `Native` — **and nothing else.** No `SmartMacro.Core` reference: that is the load-bearing constraint of the split, and the reason the panel's output directory contains no OpenCV, no Tesseract and no native vision blobs. If a view-model needs something from Core, the answer is a new IPC message type, not a reference.

`Native` is Win32 P/Invoke via `LibraryImport`, no dependencies.

**`SmartMacro.Contracts`** is what the two processes speak: the macro graph model (`SmartMacro.Macros.Model`) and its pure validator (`SmartMacro.Macros.Validation`) — namespaces deliberately kept as they were when these lived in Core — plus `SmartMacro.Contracts.Dto` (`WindowDto`, `RunningMacroDto`, `ValidationIssueDto`, `TemplateDto`, `LogEntryDto`) and `SmartMacro.Contracts.Ipc` (envelope, `IpcMessageTypes` catalog, `IpcJson`, `IpcPipe`, and the shared `IpcConnection` framing both ends use). **It references Native and nothing else.** Anything needing OpenCV/Tesseract/file IO belongs in Core; mappers from live Core types to DTOs therefore live in `Core/Ipc/DtoMappers.cs`, not in Contracts.

### IPC

`Core/Ipc/IpcServer` accepts on the named pipe and fans engine events out; `Core/Ipc/IpcRequestDispatcher` turns one envelope into one envelope. `App/Ipc/IpcClient` is the other end: a single reader loop demultiplexing `Id`-bearing responses into pending `TaskCompletionSource`s versus raising events, with a reconnect loop for the process's lifetime.

Four protocol facts every caller has to respect:

- **Responses may arrive out of order.** The server does not await a handler before reading the next request. Correlate by `Id`; never by arrival.
- **A client that stops draining events is dropped.** So `IIpcClient.Connected` is a re-fetch signal, not a nicety — every view-model re-seeds its snapshots there, and reconciles (drops rows the daemon no longer reports) rather than merely upserting.
- **`SaveMacro` → `[]` means saved.** A non-empty issue list means nothing was written. Warnings on a *successful* save are NOT returned, so the editor re-derives them by running `MacroGraphValidator` locally.
- **Two of the pushes are opt-in and per-connection**, and they are the only two frequent ones: `RunEvents` (`SubscribeRunEvents`) and `LogEntries` (`SubscribeLog`). Their flags are separate on purpose — one is scoped to «Макросы», the other to the panel's whole life — so neither mode pays the other's traffic. Both are forgotten with the connection, so both must be re-requested on every `Connected`.

`IIpcBroadcaster` (Core) exists so `RequestActivate` and the tray can push `ActivateWindow` without a DI cycle: `IpcServer` implements it and hands itself to the dispatcher in its own constructor. Its two `BroadcastTo*Subscribers` lanes must stay **synchronous and non-blocking** — `LogEventPublisher`'s recursion guard is scoped to the duration of the call.

### Core pipeline

Everything the app does is a macro run. There is exactly one path from trigger to effect:

```
hotkey / process-appeared / UI Run
  → Orchestrator.RunAsync(name, contextWindow, singleFlightKey)
  → MacroGraphStore.TryGet  →  MacroRunRegistry.TryBegin  →  MacroExecutor.RunAsync
  → IMacroPrimitives (input / vision / icon)  +  WindowRegistry (tags)
```

- **ProcessMonitoring** polls for watched processes (union of `ProcessProfiles` names) → `Orchestrator` builds the `IGameWindow` facade, registers it, and boots the process's macros.
- **WindowLifetimeMonitor** (`Core/Windows`) is the whole of window-death handling: ONE `IHostedService` that sweeps the registry snapshot on the shared `Agent:AgentPollIntervalSeconds` timer and unregisters windows whose `IsAlive` went false; `StopAsync` clears the registry entirely. W0.4 replaced `CharacterAgent` + its factory + `AgentMessage`/`AgentStoppingMessage` + the orchestrator's inbox channel with it — four layers and ~270 lines for the fact "a window closed". **Do not fold it into `WindowRegistry`** (which is what the old TODO suggested): the registry is a pure synchronous state holder under one lock, and that is exactly why it is trivial to test.
- **WindowRegistry** (`Core/Windows`) is the sole owner of window tags AND the `hwnd → IGameWindow` lookup. Tag selectors (`RequireTags`/`ExcludeTags`) route every fan-out; "identified" just means "has at least one tag".
- **Orchestrator** (`Core/Orchestration`) turns triggers into runs. Hotkey runs have no context window (macros must route by selector) and are single-flight per macro NAME; process-appeared runs get the new window as context and are single-flight per (macro, window) so N clients launching at once each boot. Both seed the `cursor` variable via `CursorPositionProvider`. Its `OnProcessAppeared` is also the only place a window is *adopted*, and **`Register` and `StartProcessAppearedMacros` are deliberately adjacent, synchronous lines** — a node reaching an unregistered hwnd fails at execution, so nothing may go between them. Ordering at shutdown is the mirror image: `WindowLifetimeMonitor` is registered in the host BEFORE the orchestrator so it stops AFTER it, and windows leave the registry only once in-flight runs have been cancelled.
- **Macros** — the model (polymorphic `$type` nodes + triggers) and its validator now live in `Contracts/Macros`; `Core/Macros` keeps the daemon-side halves: `Execution` (`MacroExecutor` walker, `MacroPrimitives`, `MacroRunRegistry`, run variables) and `Storage`. See `docs/refactoring-plan-split.md` §0.2–0.3 for the node catalogue and semantics.
- **`MacroGraphStore`** (`Core/Macros/Storage`) is the library of record: one JSON file per graph under `macros/` in the installation root, filename stem = macro name. It resolves sub-macros for `RunMacroNode`, supplies `HotkeyListener`'s bindings (re-registered on every change), and tells the orchestrator which graphs a new process should boot. **Its constructor writes nothing**: it creates the folder if missing, reads it, and stops. That is an invariant written on the class, not an accident — until backwards compatibility was dropped the same constructor migrated a legacy `macros.json`, renamed it and `hotkeys.json` to `*.migrated`, seeded six `pw-*` examples and dropped a `.examples-seeded` marker, so *constructing the object* meant *changing state on disk*. `DefaultMacroGraphs` and `LegacyMacroMigration` are gone; do not hang start-up side effects back on the ctor.
- **Identification** is no longer built in: it is an ordinary macro the user writes — `KeyPress(C)` → `Delay` → `RecognizeTagNode` (template set `"classes"` → `templates/classes/{tag}.png`) → `SetIconNode` → `KeyPress(C)`. Master/ignored characters are just tags in a selector (`ExcludeTags: ["Лучник", "Шаман"]`).

### Win32 input model (the hard-won part — do not "simplify" without re-testing in game)

PW freezes background clients (input + rendering). Every input session is bracketed by `IGameWindow.ActivateAsync` (sends magic `WM_ACTIVATEAPP` with lParam from the window's `ProcessProfile`) and `DeactivateAsync` (drain delay, then deactivate — unless window is foreground). `AgentInputDispatcher` wraps this lifecycle around every keypress/click, one cycle per node.

- **Keyboard = SendMessage, mouse = PostMessage** (`GameWindowFactory` bakes this in). Post'd keys got dropped by the frozen pump; clicks were always reliable.
- **Modifier chords (Shift+1) DO NOT WORK** via message injection: PW reads modifiers with `GetKeyState`, which cross-thread SendMessage never updates. That's why the `pw-assist` example selects the master by *clicking* party-slot-1 instead of Shift+1. There is deliberately no chord node and no chord primitive — stage 4B deleted the callerless `PressChordAsync`/`SendChordAsync` rather than leave working-looking code that silently no-ops in game. Do not add them back.
- **WM_SETICON must be SendMessage** — Post'd icon updates sit unprocessed in frozen queues. `WindowIconService` also retries at +2s/+5s because PW's post-boot init can reset the icon.
- Both the game and this app run elevated (`requireAdministrator` in app.manifest) — UIPI blocks input/capture into elevated windows otherwise.

### Vision

`IGameWindow.FindElementAsync` (one shot) and `WaitForElementAsync` (poll until timeout) are the generic primitives; both return the CLIENT-SPACE CENTRE of the match, which is what `FoundPointVar` feeds to a later `ClickNode`. Each tick is an active capture (wake → PrintWindow with `PW_CLIENTONLY | PW_RENDERFULLCONTENT` → re-freeze) + grayscale `TM_CCOEFF_NORMED` matching. Grayscale, NOT binarized — game UI sits on semi-transparent backgrounds where binarization is unstable. `ClassMatcher` (stats-window text on solid panel) is the exception that still binarizes.

`TemplateSetProvider` resolves the names nodes carry into bytes out of **one tree**, `templates/` in the installation root (it ships from `src/SmartMacro.Daemon/Assets/templates/`): a PNG in the root is a single template named by its stem (`Find`/`Wait`), a subfolder is a set named by the folder (`RecognizeTag`, filename stem = tag). Root and subfolders are separate namespaces — `Find` cannot reach a set member by tag name. `GameUiElements`/`GameClassNames` and the hard-wired `"classes"` alias are gone; **no name a macro carries changed**, only where the files sit, so there was no migration and `"classes"` still means exactly what it meant. All coordinates/templates are pixel-exact for the author's 3840×2160 screen; they now live in macro nodes, not config.

The provider has **two read paths on purpose**. `TryGetTemplate`/`GetSet` is the executor's, cached for the process lifetime — a vision tick must not hit the disk. `Catalog()`/`TryReadFile` is the panel's template browser, reads the disk every time and never touches that cache, because the user drops a PNG in that folder precisely in order to look at it. The cost is named honestly: until the daemon restarts it still matches with whatever bytes were cached first. `Catalog()` reads pixel dimensions from the 24-byte IHDR header rather than decoding.

**Assets ship from the daemon project.** `src/SmartMacro.Daemon/Assets/**` — the `templates/` tree, per-tag `ClassIcons`, the tray `icon.ico`. Stage 3 moved them out of App because every reader of them runs in the daemon; App keeps exactly one, `Assets/icon.png`, embedded as an `AvaloniaResource` for the window icon, and sees templates only over IPC. **Where they LAND differs by kind**: `templates/` goes to the installation root (user data — the user drops PNGs in it and browses them in «Шаблоны»), while `ClassIcons/` and `icon.ico` stay in `daemon/Assets/`. That split is deliberate: a `SetIconNode` carries the string `Assets/ClassIcons/{tag}.png`, `WindowIconService` resolves it against `AppContext.BaseDirectory`, and moving those files would break every macro that names one — exactly the migration the template move avoided.

**Tesseract is parked and no longer ships with the daemon (stage 4B).** `TesseractCoordinateReader` (HUD coordinate OCR) still works and is still exercised by `tools/VisionSampleRunner`, but the daemon does not register `ICoordinateReader` — nothing injected it, and the ctor eagerly builds a `TesseractEngine`. `SmartMacro.Core.csproj` marks the package `PrivateAssets="all" ExcludeAssets="build"`, which keeps both the managed dll and the 12 MB of `x64/`+`x86/` natives out of the daemon's output; `src/SmartMacro.Daemon/tessdata/` is gone and the language pack lives only with the sample runner, which carries its own PackageReference. Daemon output: 107 → 91 MB. Un-parking it for stuck detection = drop those two attributes and restore the DI line. ⚠️ Until then `SmartMacro.Core.dll` ships next to the daemon with a metadata reference to an assembly that is not there — harmless because the CLR resolves it lazily and nothing touches that type.

### The shipped layout (panel at the root, daemon in a subfolder)

Distribution is a **zip containing a folder**, and everything is inside it — no `%LOCALAPPDATA%`,
one path for the whole install:

```
SmartMacro/
  SmartMacro.exe      the panel, single-file — THE ONLY thing visible at the root
  macros/  templates/  settings.json  logs/  debug/
  daemon/
    SmartMacro.Daemon.exe + runtime + appsettings.json + Assets/ + its own logs/
```

`build/portable.proj` expresses it: `dotnet msbuild build\portable.proj` publishes the daemon into
`dist\portable\SmartMacro\daemon\` and the panel into `dist\portable\SmartMacro\`, `-t:Package`
adds the zip. Measured at `HEAD`: **88 files / 117.9 MB** — 1 file / 27.7 MB at the root, 73 /
90.1 in `daemon/`, 14 template PNGs. The zip is 48.5 MB.

- **The point is what the user sees, not bytes.** The previous iteration merged both exes into one
  folder for deduplication. It bought 26 files and 3.0 MB of 118 — 2.5%, because the weight is
  OpenCV natives on one side and Skia/HarfBuzz on the other and those do not overlap — and it cost
  a whole class of bug: **one folder is one assembly probing directory, and anything that scans it
  starts seeing the other process's dependencies.** The panel once did not start at all
  (`Serilog.Settings.Configuration` scans `Serilog*.dll` next to itself, found the daemon's
  `Serilog.Extensions.*`, and died on `CreateLogger` with exit code 1 and no output). That was
  cured with an explicit assembly list — i.e. the symptom. Separate folders remove the cause. The
  26 duplicates are back and that is the accepted price.
- **The root resolver is `#if DEBUG`, and deliberately nothing cleverer.** `Contracts/Ipc/InstallationLayout`:
  **in DEBUG the root is my own folder, in Release the parent.** No filesystem probing, no marker
  files — in the dev tree each project builds into its own `bin/` and "next to me" is right; in the
  shipped layout the daemon sits in `daemon/` and the root is one level up. The panel computes the
  same thing from its side (shipped: its own folder; dev: the located daemon's). ⚠️ **A Release
  build run from `bin/Release/net10.0-windows/` will look for state in `bin/Release/`** — consistent,
  but surprising if you do not know. Getting this wrong is silent and expensive (the daemon writes
  one `macros/`, the panel reads another, the library looks empty), so **both processes log the
  computed root at startup**, and both halves of the rule are unit-tested (the layout flag is a
  parameter; `#if DEBUG` only feeds the production overloads).
- **`PeerExecutableLocator` has no "next to me" candidate any more.** Two exes are never folder
  neighbours in any layout now: the panel looks for the daemon in `daemon/`, the daemon looks for
  the panel one level up, and the second candidate is still the dev-tree project-folder swap.
- **The panel is single-file; the daemon is not.** `PublishSingleFile` +
  `IncludeNativeLibrariesForSelfExtract` (Skia/HarfBuzz/ANGLE cannot load from a bundle, so they
  extract to `%TEMP%\.net\SmartMacro\` — **once per version**, verified: exactly those three files).
  `DebugType=embedded` and, in Release only, `AllowedReferenceRelatedFileExtensions=none` — otherwise
  `SmartMacro.Contracts.pdb`/`SmartMacro.Native.pdb` land next to the exe and the root stops being
  one file. The daemon stays unbundled: it is in a subfolder, does not have to look like a product,
  and its OpenCV blobs plus stack traces pointing at paths that are not on disk cost more than they
  save.
- **The panel's `AssemblyName` is `SmartMacro`** (project, folder and namespaces stay
  `SmartMacro.App`; the four `avares://` URIs moved). The SDK gives no separate knob for the apphost
  name — `Microsoft.NET.Sdk.targets` emits it with `Link=$(AssemblyName)$(_NativeExecutableExtension)`.
  Renaming a built single-file host post-publish loses twice: unsupported, and the name would differ
  between dev tree and shipping artifact, forcing the peer locator to know two names.
- **The panel has no configuration file.** Single-file does not unpack content files, and putting
  `appsettings.panel.json` next to the exe is a second file at the root. Serilog defaults live in
  `App/Program.BuildLogger`; an external `appsettings.panel.json` next to the exe is read **if the
  user puts one there** and then replaces them wholesale. The explicit `ConfigurationReaderOptions`
  assembly list stays — now for the opposite reason: in single-file there are no dlls on disk to
  scan, so `WriteTo.File` would not resolve at all.
- **`templates/` is lifted out of `daemon/` by `LiftSharedContent`.** The files come from the daemon
  project (`Assets/templates/`, copied to output with `Link=templates\...`) and the daemon publishes
  into a subfolder, so they land in `daemon/templates/`. In the dev tree that is the right address
  (root == the daemon's output folder); in the shipped layout it is one level too low, and the build
  file is the only place that knows. `Move`, not `Copy` — a copy in `daemon/templates/` would be a
  decoy the user eventually edits.
- **`Publish` always cleans first**, so `dist/` is a BUILD OUTPUT, not an install: run from there and
  `macros/`, `logs/`, `debug/` and `settings.json` are gone on the next publish.
- **`VerifyNoClobber` and `build/publish-file-list.targets` are gone** with the shared folder — two
  different `PublishDir`s cannot put a file on top of a file. The
  `Microsoft.Extensions.Configuration.Binder` pin in `SmartMacro.App.csproj` stays as hygiene, not
  necessity.
- **The two exes must NOT be merged into one with a `--daemon`/`--panel` switch.** The manifest binds
  to the BINARY, not the mode: the daemon needs `requireAdministrator` (UIPI), and a single exe would
  inherit elevation in both modes — the panel would always prompt for UAC and de-elevating it would
  become impossible. The reason is written in `build/portable.proj` because that file is where the
  temptation lands.

⚠️ Separate folders do **not** re-enable checking "App references Contracts and nothing else" by
listing files: the panel's dependencies are inside a bundle now. Check `deps.json`.

**Both processes pin their working directory** (`Directory.SetCurrentDirectory`) — the daemon to its
own folder, the panel to the root. Serilog's File sink resolves a relative path against the CURRENT
directory, and the daemon's `appsettings.json` carries one; started from the `Run` key the current
directory is `system32` and the log silently goes there or nowhere.

**Portability rests on being unzipped somewhere writable**, so the daemon proves it:
`Daemon/BaseDirectoryWriteProbe` creates a subdirectory and a file in it — **right after the
single-instance mutex and BEFORE the configuration and the logger**, because the logger's first act
is to create `logs/`, and by then there is nothing left to report through. **Two directories are
probed**: the installation root (user data) and the daemon's own folder (`logs/`, the first thing the
logger touches); in the dev tree they are the same path and the duplicate is dropped by comparison.
Both ACL bits are checked (`FILE_ADD_FILE` and `FILE_ADD_SUBDIRECTORY`); the probe name carries the
pid; it cleans up after itself. **Failure is fatal — there is no read-only mode.** A resident daemon
exists in order to write (macro library, log, capture dumps), and a live tray icon over an engine
that cannot save a line is a promise it will not keep. The channel is a native message box
(`Native/Dialogs/Win32MessageBox` — a WinExe has no console and the logger does not exist yet) and
the exit code is `2`. The panel has no probe (no shared assembly would take it: Contracts forbids
file IO, Native is P/Invoke only) but its logger construction is wrapped in a `try` with the same
box — before that it was the one place in the panel where a failure had nowhere to go and killed the
process silently.

### Runtime state files (in the installation ROOT, gitignored)

`settings.json` (all engine knobs — see «Settings» above), `macros/*.json` (one graph per file), `templates/**` and `debug/` all live in the installation ROOT — the folder the panel sits in, one level above the daemon (see «The shipped layout»). The daemon's own `logs/smartmacro-*.log` is the exception: it stays in `daemon/`, next to the exe whose `appsettings.json` names it. `MacroGraphStore` follows the usual store pattern — load on ctor → immutable snapshot → CRUD persists + raises `MacrosChanged` → subscribers (`HotkeyListener`, and the panel via the `MacrosChanged` push) re-register live — plus a debounced `FileSystemWatcher` for external edits, with our own writes suppressed by comparing a folder signature of last-write timestamps. An unparseable file is skipped and logged, never fatal to the load.

The panel writes `logs/smartmacro-ui-*.log` into the root as well, at an absolute path computed in code — in the dev tree the root IS the daemon's output folder, so both logs share one `logs/` there. The panel has no configuration file at all; an optional `appsettings.panel.json` next to the exe overrides the built-in Serilog defaults if the user drops one in. Every engine knob (`Agent`, `ProcessProfiles`, `Vision:*`) is the daemon's, and lives in `settings.json`.

`hotkeys.json` and the single `macros.json` are GONE — and so is the one-shot migrator that used to convert them. Backwards compatibility is off: a file in either legacy format is now just an unknown file the store ignores. A hotkey is a `HotkeyTrigger` inside the macro it starts.

**A fresh install starts with an empty library, and there is nothing to copy from.** Nothing seeds `macros/`, and `src/SmartMacro.Daemon/examples/` — the six `pw-*` graphs that used to ship as a handout — is deleted. After the node-id → `Guid` change they no longer loaded anyway, and a folder you have to copy files out of explains the program's internals instead of giving the user a button. The «Макросы» mode has its own empty state («create one with the button, bottom left», plus the folder path — a `.json` dropped in there is picked up live), kept separate from «выберите макрос слева» because an empty list with "pick one" on it reads as a broken panel.

### Conventions

- Logging via source-generated `[LoggerMessage]` partial methods, usually split into a sibling `*.Logging.cs` partial file.
- Engine knobs live in `settings.json` next to the daemon, owned by `SettingsStore`, and are read through `ISettingsSource.Current` **at the point of use** — never cached in a field, or the knob stops being live. `IOptions<T>` and the `Config/` folder that held `AgentOptions` / `ProcessProfileOptions` / `ClassMatcherOptions` / `WindowVisionOptions` are gone. `appsettings.json` configures Serilog and nothing else. Coordinates, regions, templates, keys and timeouts belong in macro nodes, NOT in settings.
- `ScreenPoint` / `ScreenRect` record structs (in `Native`) for all pixel coordinates — bind from JSON as `{ "X": .., "Y": .. }` objects.
- Coordinate discovery workflow: user hovers cursor in-game and triggers a macro; `CursorPositionProvider` logs the client-space point it seeds the `cursor` variable with, which then goes into a node.
- "Dump captures" in the «Окна» mode header sends `DumpCaptures` (with a generous timeout — it screenshots every client) and opens the folder the daemon replies with: per-agent `debug/*-full.png` and `*-class-bin.png` for tuning vision regions.
- View-models take `IIpcClient` + `IUiDispatcher` and nothing Avalonia-shaped, so every one of them is exercised headlessly against `FakeIpcClient` (`tests/SmartMacro.Tests/Ipc/`), whose canned answers go through the real `IpcJson` round trip.

`docs/spec.md` (v0.5, Russian) is current as of stage 4B and describes the system as built — including a §13 that lists what the design mockup shows but the code does not have (canvas, debugger, run-event stream, variable analysis). Its most load-bearing section is §14, the decision history: two tables of "было → стало → почему" covering both the pre-refactor drops (FOLLOW/HOLD/COMBAT state machine, LLM integration, per-character roster, nameplate identification) and the refactor's own (classes → tags, action lists → graphs, built-in commands → macros, `macros.json` → folder, `hotkeys.json` → triggers, single process → daemon + panel). Read it before proposing anything that sounds like a fresh idea.
