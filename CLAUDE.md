# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Windows-only desktop automation tool (C# / .NET 10 / Avalonia). Generic in design, Perfect World in practice: it watches for windows of configured processes, tags them (free-form strings, typically applied by OpenCV template matching), and runs user-authored **macro graphs** against them via Win32 messages — driving ~10 `elementclient_64.exe` clients through login and party-wide actions. UI text and commit messages are in Russian; tags are Cyrillic class names ("Лучник", "Жрец") because that's what the game renders.

## Where it stands

**Nothing is in flight.** The rename to SmartMacro, the node-graph macro model, tags replacing the character roster, the daemon/panel split, the whole UI, the debugger, the settings store, the shipped layout and the `.hsm` format **through wave F4** are all built and green. The format plan is finished; there is no remaining wave.

The refactoring plan that got us here was **deleted** once it was done — not out of tidiness: a plan file sitting in `docs/` reads as a to-do no matter what disclaimer you put on it, and that one had gone further than stale. Its node catalogue still described the string `Id` the model no longer has, and its IPC catalogue listed 21 requests against today's 24. It is in git history if you want it. `docs/design/implementation-plan.md` survives because the mockups next to it are still the visual reference — but it is history too, and several of its claims were disproved on contact (the targets badge needed no new IPC; the breakpoint dot could not live on the box corner). Read it for context, never as a to-do.

**`docs/spec.md` describes the system as built** and is the place to look first. Its most load-bearing part is **§14, the decision history** — tables of «было → стало → почему» covering what was dropped before the refactor (FOLLOW/HOLD/COMBAT state machine, LLM integration, per-character roster, nameplate identification), what the refactor itself changed, and the `.hsm` waves F1–F4. Read it before proposing anything that sounds like a fresh idea; a good share of "obvious improvements" are in there with a reason they were rejected.

⚠️ **§13.1 is gone, and that is the point.** It was the `.hsm` decision written up as something not yet built, and it survived three waves as a to-do. With F4 the format is finished, so its contents moved into the sections that describe what exists — the bundle layout and the library in §5.7, templates inside the macro in §8, submacros in §5.1, the panel's authorship in §5.7 and §6.1 — and the reasoning became rows in §14. Do not recreate a "planned format" section.

Gate: `dotnet run --project tests/SmartMacro.Tests` — **950 tests**, and they are expected green before anything is committed.

### The «Окна» header has no actions (issue #25, closing #6)

**«Опознать все» and «Дамп захватов» are gone, and with them the whole `DumpCaptures` path** — the button, the handler, the catalogue constant, the dispatcher `case`, `CaptureDumpService` and its DI line. Two separate reasons that landed on one strip:

- «Опознать все» ran a macro **by the hard-coded name `pw-identify`**, and the seeding of the `pw-*` examples had been deleted a wave earlier — so `CanIdentifyAll` was false on every install in existence and the button was dead for everyone. Tagging and recognition are what a macro is *for*; a button above the window list that starts one particular macro is the engine knowing a macro's name.
- `DumpCaptures` had **exactly one caller**, that button. Keeping the request without one would have given `Shutdown` a companion in `IpcCatalogTests.KnownGaps`, and a second entry is how such a list stops being read.

⚠️ **`IGameWindow.CaptureScreenshot` / `INativeWindow.CapturePng` were NOT touched** — vision runs on them (`MacroPrimitives`), and the future region-picker dialog will too. **`IClassMatcher.DebugBinarizeClassRegion` is now callerless** and was deliberately left in place: removing it cascades into `ClassMatcherSettings.Region`/`LuminanceThreshold` and the settings validator, which is a separate decision.

The header keeps `MinHeight="24"` where the buttons used to set it. Modes switch by *visibility* inside one panel, so without it the «Окна» header would sit 7 px shorter than «Прогоны» and «Лог» and the content would jump on every mode change.

The sections below are the constraints that are load-bearing — the things that look arbitrary, are not, and will be "simplified" back into bugs by anyone who does not know why they are there. Wave tags (D4, D3b, …) survive only because commit messages reference them.

### Target selectors and hotkey conflicts (D4)

**The badge needed no new IPC request**, despite what the plan's table said. The matching rule moved out of `SelectorEvaluator` into `TargetSelector.Matches` (Shared since F1; Contracts before that) and now takes TAGS rather than a window, so both processes share one implementation: the daemon still calls `SelectorEvaluator` (kept as the typed wrapper — it is where `ManagedWindowInfo` is known, and Shared must not know it), the panel calls `Matches` directly against its own `WindowDto` snapshot. **Do not reimplement those two loops anywhere.** A badge that disagrees with what the executor actually targets is the one defect that makes the widget worse than nothing.

`MacroEditorViewModel` keeps its own `WindowCatalog` — seeded by `GetWindows`, kept current by the window pushes — and hands it to every node's `TargetSelectorViewModel` the same way it hands out `NodeChoices`. It is deliberately NOT `WorkspaceViewModel`'s list: that one holds editable rows with focus state, and injecting it would tie two modes together.

Two departures from the mockup, both from looking at it running:
- **Zero matches is an error; "no windows at all" is not.** With the game closed every selector matches nothing, and painting every node of every macro red teaches the user to ignore the colour. The empty daemon gets a quiet «нет окон».
- **The canvas box shows the count only** («7 окон»); the tags are in the tooltip and in the inspector's badge. The full string pushes the type label out of a 210px header, and the label is what says what the node does.

**Hotkey conflicts are two different things.** «уже занят pw-immunity» is a library clash and the panel answers it alone. A `RegisterHotKey` refusal — another application owns the chord — is visible only to the daemon, and before D4 it went to a log file the pipe does not carry: the user bound a key, saw a clean UI, and nothing ever happened. `GetHotkeyFailures` closes that. It is a **pull, not a push**: the list changes only when the daemon (re-)registers, and the panel is what causes that, so it re-reads on `Connected`, on `MacrosChanged`, and after `ResumeHotkeys` answers — which is the load-bearing one, because hotkeys are suspended for the whole time «Макросы» is on screen and a chord bound in the editor is only tried on the way out. `HotkeyListener.Failures` deliberately survives a suspend. A ⚠ on the library row reports the same thing for macros nobody opened.

`KeyBindingPicker` was **evolved, not replaced**: the capture semantics (Esc/Del, mouse thumb buttons, ignoring bare modifiers) are fiddly and hand-tested, and are untouched. What changed is that a `Button` whose `Content` was the string "Ctrl+Shift+F1" became a templated control with a keycap strip and four states as pseudoclasses (`:unbound` / `:capturing` / `:conflict`).

### Run events (D3b)

`MacroExecutor` reports progress through `IMacroRunObserver`; `Core/Ipc/RunEventPublisher` turns that into the `RunEvents` push. Four facts that constrain anything built on top:

- **The unit is a WALK, not a run.** `RunSubmacroNode` with a selector forks one executor walk per window and they all share one `RunId` — a run id cannot tell them apart. Each walk gets its own id at `MacroWalkTrace.Begin`, and that walk id is the correlation key on every event. The canvas therefore has a walk picker and lights the node of the *selected* walk; otherwise a ten-window fan-out would light ten boxes on one graph.
- **Nothing is produced unless someone subscribed** (`SubscribeRunEvents`). `IsEnabled` is one volatile read per node, checked in the walker before it times anything or formats a detail string — not merely documented there. The daemon is resident and the panel is not, so unsubscribed is the normal state and must cost nothing.
- **Events are coalesced into batches, at most one envelope per 50 ms.** This is not an optimisation. `IpcServer` gives each connection a 256-deep queue and **drops a client that stops draining**; a ten-window fan-out is several hundred events in a few hundred milliseconds, so unbatched the panel would be dropped exactly when the user is watching. Measured: 344 events, one envelope, zero loss.
- **Overflow is counted and reported, never silent.** The queue is bounded and `TryWrite` failures ride out as `RunEventBatch.Dropped` so the panel can say the log has a hole. The engine must never block — a walk runs between two Win32 messages to a live game.
- **A walk is struck off the live list in a `finally`, and it was not always.** `MacroExecutor.RunAsync` caught three exception types and closed the books after them; anything else — and `ArgumentException` from an empty tag is reachable through a hand-edited or imported bundle, because that rule lives only in the panel's view-model — flew past `WalkFinished` and `trace.Finished`. The walk then stayed in `RunEventPublisher._live` **until the daemon restarted**: every later `SubscribeRunEvents` handed the panel a phantom walk that would never finish, and the list grew with each such failure. The exception still escapes to the orchestrator, which logs it as a bug — that distinction is worth keeping — but the bookkeeping closes on every path now.

A panel connecting mid-run gets the live walks with `FromStart = false` and says so. There is deliberately no per-run history buffer: between a drop and a reconnect nobody was subscribed, so recording had stopped and a buffer would be stale.

### The debugger (D5)

`Core/Macros/Execution/MacroDebugSession` is the whole engine side: breakpoints, per-walk pause state, and the attach count. It reaches the walker as `MacroRunContext.Debugger` — the control sibling of `Observer`, with the same `IsActive` gate, so an undebugged walk pays one volatile read per node and allocates nothing.

**The gate is between two nodes, and that is the safety argument, not a convenience.** Every `ActivateAsync`/`DeactivateAsync` bracket and every vision tick's wake/re-freeze lives entirely inside `IMacroPrimitives`, so by the time control is back in `MacroExecutor` no game window is left woken. Pausing there cannot strand a frozen client; pausing anywhere deeper could. `ABreakpointParksTheWalkBeforeTheNodeRuns` pins it with a primitive-call count.

**A hook with `Scope: Run` is the one exception to «no window is left woken», and the gate itself services it.** `GateAsync` calls `hooks.ReleaseAllAsync()` after `trace.Paused` and before `gate.WaitAsync`, so a parked walk re-freezes its windows and takes them back on resume (acquisition is lazy, so «takes back» costs nothing to write). Without that, a breakpoint on a ten-window fan-out would leave ten clients rendering for as long as the user is away — and there is deliberately no inactivity timeout. It releases the whole RUN's references rather than «its own»: in a fan-out each walk drives its own window, the sets coincide, and the sweep errs safely (worst case a window is woken again on the next node, i.e. `Action` behaviour). `AParkedWalkReleasesItsRunScopedWakeAndTakesItBackOnResume` pins it.

⚠️ **That argument rested on something that was not actually guaranteed until the review.** The bracket *lived* inside the primitive as described, but its completion did not: `GameWindow`'s wake→capture→freeze had no `try/finally`, and both `Task.Delay(settle, ct)` and `CapturePng()` throw. ■ Стоп mid-vision-tick left up to ten clients awake with nobody left to re-freeze them. All four brackets are now `try/finally`, and the freeze rule («не трогаем окно переднего плана», previously copy-pasted three times) is one `Refreeze()`. `AgentInputDispatcher` had it right all along and is the reason the input path never showed the bug — including its deliberate refusal to pass the token into `Deactivate`, which is now written down where the token arrives.

**Breakpoints live in the daemon's session, never in the macro file.** The reasoning is written where the storage is (`MacroDebugSession`): a breakpoint is a fact about a debugging session, and persisting one would put it in a diff, travel with any macro the user shares, and make a red dot dirty the editor. The ergonomic half of persistence comes free from the split — the daemon outlives the panel, so a breakpoint survives closing and reopening the UI. It does not survive «Выход» from the tray, which is also when every tag, hotkey and run goes.

**A parked walk must never outlive its audience.** A walk waiting in the gate holds its `MacroRunRegistry` single-flight slot, so that macro's hotkey is dead until it moves — unacceptable in a resident daemon driving a live game. The attach count is therefore the *same edge* as `SubscribeRunEvents` (`IpcServer.ClientConnection.SetRunEventSubscription` drives both), which the server already releases on disconnect. The last debugger leaving **auto-resumes every parked walk** and stops breakpoints biting. Resume rather than abort: the run was started legitimately and abandoning a macro halfway can leave the game worse off. There is deliberately **no inactivity timeout** — the only case left is "the panel is open and the user walked away", where the pause is doing its job.

**Stop is per-RUN; pause and step are per-WALK.** A fan-out is N walks on one cancellation token, and nobody hitting ■ while ten clients are being driven means "stop one of them". The panel is required to label it: the button reads «■ Стоп ×3». Cancellation unparks a gated walk, so ■ (and daemon shutdown) work on a paused one.

Four `RunEventKind` members were added rather than new message types, exactly as D3b laid out: `Paused`, `BreakpointHit`, `Resumed`, `VariableSet`. The first three **bypass the 50 ms coalescing window** — a step that pays a full dwell feels like a stuck button. The bypass is a signal, not a flag: a flag read once at the top of the pump loop never fired, because a breakpoint hit is always preceded by `WalkStarted`/`NodeEntered` in the same millisecond and the dwell had already begun (measured 63 ms; single digits after).

**`MacroVariableAnalysis` (Shared, next to the validator)** is the static half of the variables panel: who writes each variable, who reads it, and in which slot — including `{var}` interpolated inside strings, using the *same* `MacroVariableNames.Placeholder()` regex the executor substitutes with, because a panel claiming a read the executor never performs is the D4 targets-badge lie again. The live value is a separate concern and arrives as `VariableSet`; parsing it out of a `NodeExited` detail would mean the panel parsing a string whose format is the daemon's, and would still never see `cursor`, which no node writes.

Stage 5 (translating comments to Russian) is deliberately last.

### Templates live inside the macro (F2)

**There is no global `templates/` tree any more.** A macro is a `.hsm` bundle — a store-only zip holding `metadata.json`, `nodes.json` and its own `templates/`. The point is the one thing the old tree could not give: hand the file to someone else and it works, because the PNGs travel with it. The price is named: two macros that recognise a class each carry their own copy of eleven PNGs.

- **The layout inside the bundle is the old tree's, unchanged, and no name a node carries moved.** `templates/Find.png` is a single template; `templates/classes/Лучник.png` is set `classes`, tag `Лучник`. `*.png` only, one level deep only. Root and subfolders are separate namespaces. The parsing rule lives in exactly one place — `MacroBundleFormat.TryParseTemplatePath` — and is read by three: the executor's snapshot, the validator's inventory, and the browser's catalog. That is what makes "what the editor shows" and "what the engine will find" the same set by construction.
- **Resolution is PER-RUN.** The same name in two macros is two different files, so there is no such thing as resolving a name globally. `IMacroTemplateSource` is set once in `Orchestrator.RunAsync`, rides in `MacroRunContext`, and is inherited by child walks — a run never leaves its bundle. **The WALKER resolves; `IMacroPrimitives` takes BYTES.** Threading the source through every primitive call would make "input, vision and window cosmetics by hwnd" know about the macro file format.
- **`MacroTemplateCache` is keyed by MACRO NAME, filled with a whole bundle in one archive open, and dropped wholesale on `MacrosChanged`.** Lazy per macro, eager within it: "lazy per name" pays an archive open per new name (eleven, for class recognition); "eager for the library" reads bundles nobody will run this session. Dropping on the store's own change event gives the property that **a graph and its templates go stale and refresh together** — and removes the old provider's honestly-named cost ("until the daemon restarts it matches with whatever bytes were cached first"). A running walk holds a snapshot and is not torn by the drop.
- **The two read paths survive, further apart than before.** The executor's is the cache. The BROWSER's (`App/Macros/MacroLibrary.TemplateCatalog`/`ReadTemplate` — the panel's own, since F3) opens the `.hsm` on every request and never touches the cache — the editor exists precisely to show what is in the bundle *now*.
- ⚠️ **The 14 source PNGs are in `assets/templates/` at the repo root**, outside every project so no glob can drag them back into the run path. Eleven are class names cut by hand from the game at 3840×2160 — hours of work that cannot be redone without a live client. They are the material you drag into a macro with «+ файл…»; they are not shipped and are not read by anything at runtime.

**`ShellMode.Templates` is gone from the rail** and `TemplatesViewModel` folded into `MacroEditorViewModel` — the rail is about entities, and a template stopped being one. Its counter went with it: there is no "how many templates are there" number any more, only "how many does THIS macro have", and that lives in the inspector next to triggers and variables. Selecting the browser's row still costs one image request; the list is still metadata only; there are still deliberately no thumbnails.

**«НЕТ ФАЙЛА» is gone and the validator has it instead, which is a promotion.** A bundle's template set is known statically, so `MacroGraphValidator.Validate(graph, inventory)` says "this node names a template the macro doesn't have" at save time (panel) and at load time (daemon), rather than in a section you had to go and open, or a log line mid-run. It is a **Warning**, not an Error: an error would block saving, and "type the name, then import the file" is a normal order of operations; a missing template does not abort the run either — the node takes «не найдено», which is what that edge is for. ⚠️ **`null` inventory and an empty one are different**: `null` means "the bundle's contents are unknown" and skips the check entirely. Both ends pass a real one — the daemon from the library entry, the panel from `TemplatesViewModel.Inventory` — because two runs of one validator disagreeing is the D4 targets-badge lie again.

**The four template requests are gone (F3), exactly where F2 said they would be.** `GetTemplates`, `GetTemplateImage`, `AddMacroTemplate` and `DeleteMacroTemplate` were retargeted at a macro in F2 and the last two were marked temporary the day they were written; the panel writes the zip itself now. Editing a bundle always goes read-everything → change one thing → write everything, because atomic replacement needs a finished file; so adding a template costs the same as saving the macro.

**Writing is atomic, and that lives in `Shared` next to the writer** — not in the store, because the writer is the panel (F3) and the logic is reused, not rewritten. Temp file in the same folder (`{name}.hsm.tmp`, which misses the `*.hsm` watcher filter in both long and 8.3 form), then replace. ⚠️ **The replace is `File.Replace` (`ReplaceFile`), NOT `File.Move(overwrite: true)` (`MoveFileEx`)** — measured, not read: `Move` throws `UnauthorizedAccessException` when the destination is open *even if the reader opened it with `FileShare.Delete`*; `Replace` succeeds with `FileShare.Delete` and fails without it. So `FileShare.Delete` in `MacroBundleReader` is not decoration, it is the other half of the mechanism. Renaming a macro carries `renamedFrom` so the new bundle inherits templates, submacros and the passport — without it renaming would silently drop the templates, which is exactly what the format exists to prevent. (It was `SaveMacroRequest.RenamedFrom` until F3 took the request away; the parameter stayed.)

**`*.json.incompatible` is gone.** Moving an unparseable file aside was introduced when node ids became `Guid`s and every old file stopped parsing at once. The bundle removes that failure by format: the reader distinguishes "corrupt", "busy", "made by another format version" and — since the node rename — **"names a type this build does not know"**, and moving a *future-version* bundle aside would be the exact lie the version field exists to prevent. The file stays put and the reader's verdict goes to the log in full.

### Matching gives a VALUE; tagging is a separate node

**`RecognizeTagNode` is `MatchTemplateSetNode` now** (`$type: matchTemplateSet`, family prefix `match`, label «Сопоставить с набором» — «Набор шаблонов» in the box header and the inspector title). It writes `ResultVar` and **nothing else**: `ApplyTag` and the `_windows.AddTag` call in the executor are deleted. Matching produces a VALUE; marking a window is a different action and its node already exists — `AddTagNode` with `{tag}` interpolation, which is verified end to end by `MatchTemplateSet_ThenAddTag_IsHowAWindowGetsTagged`. While the two were fused, "match but do not tag" meant unticking a checkbox you had to know about, and the chain «match → tag → icon» was half-hidden inside one canvas box.

- **`ResultVar` is required — an ERROR, and in the VALIDATOR, not in the panel's input errors.** With the tag gone, that write is the node's only observable result; without a name it is a «совпало / не совпало» fork with the value thrown away. The panel-only input error moved to `Shared` so both sides judge it identically: the library row goes red and the daemon refuses to arm. An input error would have blocked the *write*, and a row with an empty field turns into a node perfectly well — it just turns into a pointless one.
- **A miss writes NOTHING** — not an empty string, not the previous value. That is load-bearing for `MacroVariableAnalysis`: the `NotMatched` branch leading to a `{tag}` read must abort on an undefined variable rather than hang an empty-named tag on the window or resolve `Assets/ClassIcons/.png`.
- ⚠️ **Every pre-rename bundle stops parsing at once, and the refusal had to be made honest.** An unknown `$type` reaches `MacroBundleReader` as the same `JsonException` as a truncated brace, so it used to be reported as **Malformed** with the framework's English string attached. New fault `MacroBundleFault.UnknownType` and a Russian verdict naming the discriminator: «В "nodes.json" есть действие типа "recognizeTag", которого эта сборка не знает». It is deliberately neither «повреждён» (the file is intact) nor «сделан другой версией формата» (that is the *version field's* verdict, and faking it is the exact lie the field exists to prevent). The known-type catalogue comes from the model's own `JsonDerivedType` attributes — no second list — and the extra parse lives **only on the failure path**.
- ⚠️ **The full label collides in the inspector title**, exactly as «Запустить под-макрос» did in F4, and it was found the same way — by eye on the live panel. Hence two strings: `Node_Type_MatchTemplateSetMenu` (full, for «+ Нода») and `Node_Type_MatchTemplateSet` (short, for the box header and the inspector title).

### Variable-name fields offer what the macro already has

The four fields that hold a variable NAME — `FoundPointVar` on Find and Wait, `ResultVar` on the match node, `PointVar` on Click — carry a ▾ next to them listing the names already in the macro. Typing still creates a new one: there are no declarations, a variable exists because a node named it.

- **Two lists, split BY KIND, and that is the whole point.** `MacroVariableAnalysis` already labels each name Point / Text / Unknown, so `IVariableNamingRow.VariableSlotKind` picks which list a row gets. A list offering a string in a point slot is not merely useless — it *helps* build a macro that aborts mid-run, wearing the costume of a hint. Unknown (a name only interpolated into a string, never written) goes with Text: that is exactly the case the list is most useful for — the tag is read in `AddTag`, and the node that will write it is what you are adding.
- **Inside a submacro the PARENT's names are offered too**, because a sub-run gets a *copy* of the parent's variables and reading them there is legitimate. Writing to one is not (nothing comes back), and there is deliberately no second guard here: `ValidateBundle` already warns about exactly that on the call node (F4).
- **Typing a name rebuilds the lists at once**, and the property names are listed explicitly in `OnNodeRowChanged` rather than riding on `Summary`. Find/Wait/Match do not put the variable in their `Summary` at all, so before this the freshly typed name reached neither the variables panel nor the neighbouring fields until some unrelated edit — a pre-existing gap the feature would have inherited.
- ⚠️ **This was an `AutoCompleteBox` first, and it must not become one again.** Picking an item from its dropdown **killed the panel**: `ArgumentOutOfRangeException` inside `AutoCompleteBox.CloseDropDown` → `SelectionModel.SelectedIndex`. Two more turned up before that: the dropdown does not open on focus (it reacts to *text* changes, so a field with a name already in it stayed silent — the "I forget what I called it" case), and its popup rendered in Fluent grey. All three were invisible to build and tests and found by running it. What is there now is `TextBox` + `Button.ghost` + `Flyout` — primitives that already have a Nocturne theme and already work in this panel (same shape as the «+ Нода» menu).
- **No filtering by what is typed.** With one, a field reading «кнопка» would hide «cursor» until cleared — and swapping one variable for another is precisely why the list exists. A macro has a handful of names; there is nothing to hide.

### A foreign bundle is not our bundle (case, and unpacking limits)

Two of the format's guarantees were written from the WRITER's side and then quietly assumed for files we did not write. Import is a file copy by design (F3 — running it through today's writer would lose everything today's format version does not know), so both had to be re-earned by the READER.

- **The writer's duplicate check is `Ordinal`, the same rule as the inventory and the executor.** It was `OrdinalIgnoreCase`, reasoned as «NTFS is case-insensitive, so `Лучник.png` and `лучник.png` would unpack into one file at the recipient». That describes a scenario this program does not have: **templates are never unpacked to disk** — executor, inventory and browser all read archive entries into memory and compare names ordinally, because «Лучник» and «лучник» are different TAGS, and `MacroTemplateInventory` says so out loud. The price of the caution was a dead end with no exit: an imported bundle holding both was read, executed and listed as two rows, but the first «Сохранить» threw «Путь встречается в бандле дважды» — and deleting a template goes through the same writer, so the UI could not repair what the UI had refused to save. A literal duplicate (the same path twice) is still refused.
- ⚠️ **The panel held the mirror image of the same bug, and it was the worse half.** `MacroLibrary.SamePath` compared template paths case-insensitively with the same NTFS reasoning, so adding «Лучник» to a bundle that already held «лучник» **silently deleted** the other one, and deleting either took both. `TemplatesViewModel.NamesIn` compared SET names the same way, promising a replacement that would not happen. All three are Ordinal now: one rule, and the executor's dictionaries are what it matches.
- **The reader has two size ceilings, and they exist because «зип-бомб не бывает» was a claim about OUR writer.** Store-only does mean unpacked size = file size — for files we write. Nothing stopped a *compressed* `.hsm` from being imported, and the first run of such a macro went straight into `ReadAllTemplates` («all the bundle's templates into memory at once») and took the **daemon** down with it — hotkeys, runs and all. `MacroBundleFormat.MaxEntryBytes` (32 MiB) and `MaxBundleBytes` (128 MiB) are checked **inside `WithArchive`, over the central directory, before a single byte is inflated**: one place, so every entry point refuses identically — and `Read()` is one of them, which is what puts the verdict on the library row's red `!` instead of leaving it to a log line. The refusal is **whole-bundle, not per-entry**, on purpose: skipping an oversized entry would mean the next «Сохранить» writes the bundle without it, i.e. the same loss, quieter.
- **There is a SECOND line inside the decompression: an entry must hand over exactly what the central directory declared.** That directory is written by whoever sent the file, so the sum check trusts a number the attacker controls — a hundred entries declaring a kilobyte each could otherwise inflate to sixty megabytes each. (Deflate is capped by .NET itself, which passes the declared size to the inflater; a `Stored` entry is not, and that is the hole this closes.)
- **`MacroBundleTemplateInfo.Bytes` stayed the UNPACKED size; only the claim about it changed.** «Он же размер файла: бандл не сжимается» is false for an imported compressed bundle, and that sentence is gone. The value did **not** become the packed size, deliberately: the browser's 1 MB preview ceiling gates a *decode*, and a decode costs the unpacked bytes — reporting the packed size there would walk a 5 MB PNG through a ceiling that exists to stop exactly that. What did change is that the number is now bounded and verified instead of taken on trust from the archive's own table of contents.

### Cutting a template out of a fresh frame (issue #29) — and a point, and eleven in a row

The button next to a vision node opens a dialog on a **fresh capture of the window** (or a file from disk), you drag a rectangle, and the crop lands in the bundle as a template while the node's `Region` is filled from the same gesture.

**The point is not convenience, it is provenance.** The frame comes through the very path matching uses — `PrintWindow` with `PW_CLIENTONLY | PW_RENDERFULLCONTENT`, window awake — so the template is made of the same pixels the executor will compare. A crop from «Win+Shift+S» differs by compositor colour management, by scale, and by whatever the cursor was covering; a good share of "why does it not match" is that difference.

- **The panel captures, the daemon wakes.** `CapturePng` is in `Native`, which the panel already references, so a ~10 MB frame never crosses the pipe — no base64 envelope ahead of run events, no temp file. But the wake bracket is NOT duplicated: `AcquireCaptureHook`/`ReleaseCaptureHook` open a `WindowHookScope` in the daemon, so the per-window refcount and the transition chain stay in one place. A second bracket in another process could neither wait out somebody else's settle nor hold a window through a running vision tick.
- **The Acquire response means «awake and settled»** — the settle is paid inside the handler, because capturing before it returns a black or stale frame. The lease is short (acquire → capture → release, a few hundred ms), so «the user walked away with the dialog open» is not a case.
- ⚠️ **It is the FOURTH connection-scoped lease**, released by the same `finally` in `ServeConnectionAsync` as the two subscriptions and the hotkey suspension. That list is the whole safety story for a panel killed by Task Manager: **add a per-connection switch, add a line to that `finally`.**
- **`Region` gets a 16 px margin, and the margin is a constant, not a fraction.** The tolerance you need is drift in pixels, not percent of the subject: 25 % of a 600 px crop is 150 px of pointless search area, 25 % of a 24 px icon is nothing. Region exactly equal to the crop turns «find» into «check it is exactly here» — one slide position, lost on a one-pixel shift. The crop itself is exactly the selection; the dialog draws both rectangles so the difference is visible.
- ⚠️ **A slash in the name is a silent trap, closed by round-tripping rather than by a second rule.** `classes/Лучник` typed into a single-template field writes and parses fine — but parses as the PAIR (set `classes`, tag `Лучник`), while `FindElement` looks up a root template literally named `classes/Лучник`. The file lands in the bundle and the executor never finds it. `IsNameUsable` asks `MacroBundleFormat` itself instead of keeping a list of forbidden characters, because a second list is a copy waiting to drift.
- `IRegionCapturePrompt` is the second dialog seam after `IMacroNameConflictPrompt`, and follows it exactly: no seam ⇒ the action quietly does nothing, so the view-models stay headless-testable.
- **The 1 MB preview ceiling does not apply here, and that is now written at the ceiling.** Its reason is «a palm-sized pane holding Skia memory for an arbitrary blob»; the dialog is full-screen, looking at the frame is the whole point, and there is exactly one bitmap, disposed on close. Applying it would cancel the feature. The crop that comes back is an ordinary small template and passes normally.

⚠️ **A new entry in the found-by-eye family: a `Canvas` with explicit `Width`/`Height` inside a `Panel` lays out CENTRED.** With a frame larger than the window the surface origin went negative, so the picture rendered off-position and a selection would have landed somewhere else entirely — invisible to build and to every test, because the arithmetic was right and only the layout disagreed with it. `HorizontalAlignment="Left" VerticalAlignment="Top"` is the fix.

**«Сохранить и дальше» — the same rectangle in the next window, and only for `RegionCaptureKind.Tag`.** A set of classes is eleven crops of ONE place on screen taken from eleven characters; re-dragging the frame for each yields eleven slightly different crops, which is the inconsistency the feature exists to prevent. The button commits the crop **without closing the dialog**, clears the name, and advances the window picker — the selection is not recomputed at all.

- **The EDITOR commits, through a callback** (`RegionCaptureSink` on `AskAsync`). The dialog knows nothing about bundles or the macro library, and accumulating crops until close would lose all of them to an × click. A refusal comes back as a string and is shown inside the dialog, which stays open with the name intact.
- **The taken-names set GROWS during the visit.** `RegionCaptureRequest.ExistingNames` is a snapshot; a second «Лучник» in the same run of eleven is the likely typo, so the dialog keeps its own set and adds every committed name. Still a warning, never a refusal — replacing your own template with a fresh crop is the normal reason to come back.
- **Cancel after saving returns the LAST committed crop, not `null`.** The files are already in the bundle; leaving the node with an empty region next to a full set of templates would be worse than either honesty.
- ⚠️ **A smaller window is a REFUSAL, not a clip, and the selection is no longer dropped silently.** `ShowFrame` used to reset a selection that did not fit the new frame — losing exactly what the button exists for. Clipping was rejected separately: a clipped crop is a template of a DIFFERENT SIZE, and `TM_CCOEFF_NORMED` compares a fixed-size patch, so the set would gain a file that is simply a different template — the same inconsistency, only invisible.

**A third mode of the same window picks a POINT for `ClickNode`** (`RegionCaptureKind.Point`, `IRegionCapturePrompt.AskPointAsync`). It replaces the only way a coordinate could be obtained before: hover the cursor in game, fire a macro, and read out of the log what `CursorPositionProvider` seeded `cursor` with. **A second window was not on the table** — frame, pan, zoom, the from-file door and the wake lease are identical, and two near-identical windows would drift in the most expensive part, the screen→frame conversion. The old route stays and is still needed: it is about a LIVE point during a run, not one frozen into the node.

- ⚠️ **Screen → pixel rounds DOWN, not to nearest.** A rectangle's coordinates sit on pixel BOUNDARIES, where `Math.Round` is right; a point addresses the pixel itself, and pixel `N` occupies `[N, N+1)`. At 722 % that is a seven-screen-pixel square, and `Round` would hand back the neighbour every time the user aims at its right or bottom half. A one-pixel miss is a miss past the button, and it would only show up in game. Verified live at 722 %: the reported pixel advances by exactly one per band, and the crosshair sits at pixel + 0.5 so the chosen square is visible.
- **Picking a point also clears `UseVariable`.** `Point` and `PointVar` are mutually exclusive and the validator errors on both at once, so leaving the switch on would invalidate the node by the very action meant to help it. The variable's *text* stays in the field — `ToNode` does not emit it — so turning the switch back on is one click.

⚠️ **The dialog is now two windows' worth of modes in one, so `RegionCaptureKind` is the mode, not just "what name to ask for".** Anything added here must ask which of the three it belongs to; `ApplyMode()` is the one place that answers.

### The panel owns the library (F3)

**The panel is the only author of macros; the daemon only reads and executes.** The editor lives
in the panel, so the panel writes; the two processes share a filesystem, so **the macro stopped
travelling over the pipe entirely.** Seven requests are gone and must not come back: `GetMacros`,
`SaveMacro`, `DeleteMacro`, `GetTemplates`, `GetTemplateImage`, `AddMacroTemplate`,
`DeleteMacroTemplate` — the last two were introduced by F2 under protest and marked as dying here.
Two protocol conventions went with them: «`SaveMacro` → `[]` means saved» and «warnings on a
successful save are not returned».

**`MacroBundleFolder` (Shared) is the folder-as-a-library, and it is one implementation for two
processes**: enumerate, read a row, validate a name against NTFS rules, save a graph
(read-everything → change one thing → write everything), delete, import. Two readings of one folder
would drift exactly the way the D4 targets badge would have: the panel showing one thing, the engine
running another.

**`MacroGraphStore` is read-only now, and own-write suppression is gone with it** — the folder
signature by last-write time was a sizeable piece of clever code that had nothing left to silence.
The store's invariant grew from «the constructor writes nothing» to «the class writes nothing».

**The panel has its OWN `FileSystemWatcher`, and that is a decision.** The objection is «two
processes watching one folder»; the answer is that they watch for different things and pay
differently for missing them. Without its own watcher the panel would learn about *its own write*
from the daemon — after a 300 ms debounce and a round trip, i.e. exactly the loop F3 exists to
remove. The daemon needs its own regardless (hotkeys, template cache), so the «second» watcher is
really the first. The real hazard of two watchers is two *writers*, and F3 removes that by
construction. No own-write suppression on the panel side either: the editor already compares the
open graph's *content* with what is on disk, which is stronger than any timestamp signature.

**`MacrosChanged` survived with a new meaning: «the daemon re-read the folder and re-registered the
hotkeys».** It says nothing about library contents — the panel knows those first, from its own
watcher. The one thing only the daemon knows is which chords Windows granted, so the only correct
response to the push is to re-read `GetHotkeyFailures`.

**The guarantee changed and had to be serviced.** «It is in the library ⇒ the daemon accepted it»
rested on `SaveMacro`. What holds now is «somebody put a file there», so **the daemon validates
every bundle at load and refuses to arm the triggers of an invalid one** (`MacroGraphStore.Armed`,
which is what `HotkeyListener` reads — **and, since the review, `Orchestrator` too**. It read `All`
for years, so a macro with a validation error had a dead hotkey and an honest red row in the library
— and still booted on every client launch, running half a login sequence across ten windows before
dying on the broken node. Worse than the D4 defect F3 closed: the macro did not go quiet, it did
half the job). Otherwise «the hotkey does nothing» comes back
from the other side — the D4 defect exactly. The validator is shared and the inventory is the same
one, so **the panel reaches the same verdict itself** and needs no new request to say «в макросе
ошибок: N» on the library row. Telling the two causes apart is now the
library row's job alone — the environment diagnostic that used to do it is gone (see «Настройки»).

**The row carries ONE mark, and which one it is was got wrong the first time.** The red `!` means
«с этим макросом беда» in both senses — the bundle does not read, *or* the validator found errors —
and its tooltip says which; the ⚠ next to the chord is left with the only thing `!` cannot know,
that **Windows refused the chord**. Until this was fixed the `!` covered the unreadable case alone
and the errors case rode on the ⚠ as «хоткей не вооружён», which left a hole exactly where nobody
would look: **a macro with a graph error and NO hotkey trigger lit nothing at all.** `IsBroken` was
false (the file reads), there was no hotkey to complain about, and the only trace was a greyed-out
▸ with no word about why — the D4 defect once more, and this time inside the screen that exists to
report it. The nested row of a *function* had shown its `!` for a graph error since F4, so an error
in a function was visible and the same error in its parent was not. ⚠️ Do not "restore" the second
marker for the unarmed case: two marks for one cause is how a person learns to read neither.

**One race F3 introduced.** The panel writes the file; the daemon hears about it through a debounced
watcher. «Сохранить» then immediately «Запустить» can land in the gap. So `RunMacro`, on a miss,
re-reads the folder once (`MacroGraphStore.Refresh`) before refusing — only on a miss, and `Refresh`
raises `MacrosChanged` only when the snapshot actually changed (name + `Metadata.Modified`, which
the writer touches on every write, so a template edit counts). The watcher path raises
unconditionally: its message is «I re-read», not «something differs».

**An unreadable bundle stays in the panel's list**, with a red `!`, the reader's verdict in the
tooltip, and a second line under the name. Before F3 such a file existed in the folder and appeared
nowhere in the UI. The case the format was split for finally reaches the screen: **when
`metadata.json` parses and `nodes.json` does not, the row shows the author's real name and
description** rather than «файл X — ошибка». ▸ is dead on those rows, × is not — it is the only way
to remove the file without opening Explorer.

**Import is a file copy; export hands the file over as it is.** Importing must NOT parse and rewrite
the bundle: run through today's writer it would lose everything today's format version does not know
— the exact loss the format exists to prevent. The file stem wins over the `Name` inside, so the
copy honestly answers to its new name.

**There is no draft state: the panel saves by itself.** «+ Новый макрос» creates the file at once under a free name; `DraftName` is gone. Manual «Сохранить» stays, and is now the ONLY way to rename, to pick a side on a disk conflict, and to write immediately.

- **Writes fire on 3 s of QUIESCENCE, never on a timer.** Every write wakes the daemon: it re-reads the folder, **re-registers every hotkey and drops the whole template cache**. On a timer that is dozens of wake-ups per editing session on an engine that may be driving a live game. 3 s is an order of magnitude above the daemon's 300 ms watcher debounce (so two writes are two events, not a smear), longer than any within-word typing pause, and longer than the 2 s `WatcherLagWindow` in `RunMacro`. The cost is named: a crash loses the last 3 s, where it used to lose everything since the last manual save.
- **Validation errors no longer block saving — manual or automatic.** That is a reversal of a documented rule, and the reason is that a graph is invalid exactly while it is being worked on. The safety net was already there and is not new: `MacroGraphStore.Armed` refuses to arm an invalid macro and the library row shows a red `!`. What DOES block a write is an **input** error (a field the row cannot turn into a node at all) and `ChangedOnDisk` — both visibly, with the toolbar saying which. ⚠️ Note the cost of the first: adding a `Find` node stops autosave until its template name is typed, because an empty field yields no *validation* error and the daemon would otherwise arm a half-built macro.
- **Autosave never renames and never prompts.** It writes to `_loadedName` and substitutes it into the graph, so the reader never sees a stem-vs-`Name` mismatch. A modal «имя занято» every few seconds would be intolerable, so renaming is an explicit gesture — Enter, focus loss, or «Сохранить».
- **The clock lives in the VIEW** (`MacrosView`, `DispatcherTimer` → `TickAutoSave()`), the same seam as the debugger's elapsed counter and for the same reason: every view-model here is exercised headless, and a test must not wait out real seconds. `MacrosView` starts it only under a classic desktop lifetime, so the headless sweeps never get a tick mid-measurement.

⚠️ **A taken name ASKS — it does not resolve itself.** Import used to append `-2` silently and
`Save` used to overwrite silently; the review found the second was destroying macros (rename onto an
existing name produced one file with one macro's graph and another's templates, then deleted the
survivor). Both now go through `IMacroNameConflictPrompt` — a seam, not a `Win32MessageBox` call,
because the view-models must stay headless-testable — and the dialog names what would be lost («будет
потеряно: 11 шаблонов, 2 под-макроса»). `FreeName` did not go away: it is one of the three answers,
now chosen by a person. **The refusal lives in `MacroBundleFolder` as well as in the panel**, and
that is deliberate: the panel asks, the folder makes it impossible to lose the race between the
question and the write.

⚠️ **The 1 MB ceiling was a limit of the PROTOCOL and is gone from import.** It existed so a
multi-megabyte base64 string would not queue ahead of a running macro's events. It survives for the
*preview* only, for a local reason: the preview pane is palm-sized and decoding an arbitrary blob
holds Skia memory.

### Submacros — the calls come home (F4)

**There are no cross-macro calls left.** `RunMacroNode` called any macro in the library BY NAME, and
that was the last hole the `.hsm` format existed to close: hand the file over and it silently does
nothing, because the callee is not in the recipient's library. Its replacement is
`RunSubmacroNode`, which calls a submacro of its OWN bundle by `Guid` — `submacro/{Guid}.json`
inside the `.hsm`, next to `templates/`. The old node type, its `$type` discriminator (`runMacro`),
`{var}` interpolation of the callee, `IMacroGraphResolver` and `VariableSlot.MacroName` are all
deleted, not deprecated.

**Three rules, and all three hold by construction rather than by convention:**

- **Flat.** A parent calls a function, a function calls nobody. This is what makes cycles
  *impossible*, and it replaced two mechanisms that used to catch them at speed: `MaxDepth = 4` and
  cycle detection over a chain of macro names. Both are gone; one check ("we are already inside a
  function") stands in for them. Recursion in a macro editor is a gun that goes off, and nobody
  asked for it.
- **No triggers.** A hotkey on a function turns it into a top-level macro — and `HotkeyListener`
  only ever looks at the top level, so the key would silently do nothing. That is the D4 defect
  exactly, so the validator makes it an Error and the whole macro goes unarmed.
- **No templates of its own.** `templates/` is one folder, at bundle level; the run's template
  source is set once in `Orchestrator.RunAsync` and inherited by the child walk. F2 already built
  it that way "for later" — this is the later.

**The reference is a `Guid`, not a name**, for the same reason node edges are: by name, renaming
breaks the parent, and you would have to either forbid renaming or bring back the graph walk that
repoints everything — the machinery the `Guid` node ids deleted. The one identity exception stays
where it was, on the bundle: **a macro's identity is its file name**, because a file is handed over
and renamed in Explorer; a function is not. The file *stem inside* `submacro/` IS the guid, so
there is no second "file → id" map to drift.

**Extraction refuses rather than guesses, and that is the wave's real decision.** A function has one
entry and returns to its caller, so a selection with two entries, or with exits going to different
places, is not extractable in any defined way. `MacroExtraction` (Shared, next to the validator)
refuses and **names the offending nodes** — «У выделения 2 входа: снаружи ведут рёбра в «x», «y»» is
actionable, «выделение не извлекается» is not. The mixed case is refused too: if some exits end the
run and one leaves the selection, after extraction both would be the same single return, i.e. a
branch the author drew specifically to END the run would start continuing it. Silently rewriting
that is exactly the kind of change the user would discover on a live game with ten clients.
`MacroNodeEdges` (Shared) exists because extraction needs to REWRITE edges and the validator needs
to READ them — two copies of "Find has Found and NotFound" would drift silently.

**Variables still travel as a copy, but the copy stopped being silent.** Sub-runs get a clone and
nothing comes back — the safer behaviour, and it was already there. What F4 added is the honesty:
`ValidateBundle` warns on the call node when the submacro WRITES a variable the parent READS and
never writes itself (the spec's own example: the match node inside, `SetIcon` outside), and the
inspector says so under the function picker. The condition is narrowed to exactly that trap on
purpose — warning about a function that writes something for itself would teach people to stop
reading warnings, which is the D4 targets-badge argument again.

**The breakpoint key grew a third coordinate**: `(macro, submacro, node)`. Node ids of a function
live in the same macro but a different graph, and `SetBreakpoints` replaces one graph's set
wholesale — without the third coordinate, pushing a function's breakpoints would clear the parent's.
`ValidationIssue` and `RunWalkDto` grew the same coordinate, for the same reason and with the same
shape: an issue about a function's node has to switch the canvas before it can highlight anything,
and **a walk of a function and a walk of its parent share one window handle**, so without
`SubmacroName` the run picker would label both «0x140804».

**The library is a TREE now, and prefix grouping is gone.** «pw · 6» / «прочее · 11» derived
structure from how the author had named the files, and it was honest only because no other structure
existed. Now one does — a function is inside its macro's file — so the group header IS the macro row
and the nested rows are its functions. Keeping both would draw a tree with one real level and one
invented one. A function's row is deliberately poorer: no ▸ (it is not a macro, you cannot run it
alone) and no ⤓ (it has no file of its own), just the name, a red `!` if its graph has an error,
and ×.

**The editor holds one bundle and shows one graph of it.** `Nodes`/`Triggers` are whatever is on the
canvas; `_submacros` is the model; `_parkedParent` holds the top-level graph while a function is
open. `CommitCanvasGraph` is the only place canvas content goes back into the model, and every graph
switch must go through it. Dirty-tracking (`SerializeCurrent`) covers the WHOLE bundle — comparing
one graph would call an edit to a function "no changes" until you walked back to the parent.
⚠️ **That was the intent; it did not hold until autosave forced it out.** The baseline was reset on
every canvas switch, not only on bundle load, so editing the parent and then stepping into a
function declared the bundle saved — closing the editor lost the edit. Worse, `_diskJson` then held
the FUNCTION's graph while `ApplyLibrary` compares the top-level one, so any watcher event with a
function open read as a foreign edit and reloaded the whole bundle, discarding the function being
edited. Rare enough to survive unnoticed by hand; autosave would have fired it routinely.
`LoadCanvasGraph(graph, freshBundle:)` is the fix, with a regression test in `SubmacroEditorTests`.
Multi-selection for extraction is `IsMarked`, deliberately separate from `IsSelected`: the latter
drives the inspector (one node), the former is a set that must survive clicking a validation issue.

⚠️ **Four defects here were invisible to build and tests and were found by running it:**
`ShowsTriggers` was computed correctly but never announced when `HasOpenMacro` flipped (so the
Triggers section and «+ Под-макрос» stayed dead after creating a macro); the extract button kept a
stale count after extracting, and stole width from the status line, which then truncated mid-word;
`TypeLabel = "Запустить под-макрос"` collided pixel-for-pixel with the inspector's «двойной клик —
правка на месте» hint; and the call node's dropdown was EMPTY on first open of a macro, because
`SubmacroChoices` was only rebuilt in `RebuildLibrary`, which runs *before* the bundle is loaded.
Each now has a test, but note what the tests could not have found first: three of the four were
about a notification or a pixel, not a value.

### Headless layout sweeps (`tests/SmartMacro.Tests/Ui/`)

Avalonia.Headless with **real Skia rendering** (`UseHeadlessDrawing = false` + `CaptureRenderedFrame()`), taking back the part of "run it and look" that a machine can measure. Eight rules, run over all five modes at 1520×840 and 1920×1040, with the **real view-models against `FakeIpcClient`** and a real `macros/` folder — not stub DataContexts, because the empty state only ever proves the empty state.

⚠️ **A sweep is only as good as its scene, and this was learned the expensive way.** The rule «a name allowed to shorten must still fit one «…»» was written, correct, and silent — because `UiScene` had no long chord in it. On the live panel `Ctrl+Shift+Alt+F12` ate the library row whole and the macro's name vanished *entirely*. Adding one macro to the scene turns the sweep red at both sizes («зазор −24 px между «Ctrl+Shift+Alt+F12» и «▸»»). **When you add a rule, add the content that violates it** — a green rule over content that cannot break it is decoration.

Four mechanics that are not obvious and will be "simplified" back into false greens:

- **Ink rects, not control bounds.** A `TextBlock` is stretched to its whole cell by default, so a bounds-based overlap rule flags every pair of adjacent grid columns. Rules intersect ink (alignment + natural size) with ancestor clips — what scrolled out of view collides with nothing.
- **`DesiredSize` is clamped to the constraint by `MeasureCore`**, so "narrower than it wants" is unmeasurable from a live control. Every width rule measures an off-tree probe `TextBlock`.
- **Skia does subpixel AA by default**, which turns a grey glyph into `#1E4285` pixels and breaks the Foreground rule outright. The render tests force `TextRenderingMode.Antialias`.
- **`VirtualizingStackPanel` recycles containers** across a `DataContext` round-trip, so the missed-`PropertyChanged` detector compares multisets per path, not indexed positions.

**Font resolution is the load-bearing precondition.** `NocturneFontFamily` resolves to Inter (`DesignEmHeight` 2816 — Segoe UI and Cascadia Mono are 2048), same file and same shaper as the panel; a pinned test says so. If that ever stops holding, every width and ink measurement is measuring the wrong thing and the whole directory becomes decoration that drifts silently.

**The TUnit adapter is `Ui.RunAsync`, deliberately not a `TestExecutorAttribute`.** The attribute would put TUnit's own await machinery on the Avalonia dispatcher, and that failure mode is a hang, not a message.

**Reported, not enforced: the panel does not fit its own documented minimum.** At 1280×760 the editor toolbar overflows by 100 px and the status line collapses to zero; at 1100×620 the canvas legend collides with the zoom chip and settings fields overlap by 55–173 px. The remedy is a decision about what the toolbar sacrifices, so it is written into `UiLayoutTests`' class doc rather than fixed — adding an `[Arguments]` line enforces it the day that is decided. **Also not covered: any scaling but 100%** — headless always renders at `RenderScaling = 1`, and the author's screen is 3840×2160.

Glyph substitution is the one place headless is weaker than the real thing: `⏸` is caught, `▶`/`⚠` are not, because both live in Inter and the swap to Segoe UI Emoji happens below Avalonia's font chain in DirectWrite. **The code-point table in `Sources/AxamlGlyphTests` stays the primary guard for that**; the render rule is a second net, not a replacement.

### The log feed (Лог)

One request (`SubscribeLog`) and one push (`LogEntries`) — the **second subscription on D3b's channel**, deliberately reusing its batch shape rather than inventing a mechanism. `Core/Ipc/LogEventPublisher` is the engine half (ring + queue + pump + broadcast); `Daemon/Logging/IpcLogSink` is the Serilog adapter, and it lives in the daemon because **Core has no Serilog reference** and must not grow one. Four things constrain anything built on it:

- **Recursion is cut by construction, in three places.** A sink that ships records over IPC lives inside the process that logs. (1) *Neither the sink nor the publisher has a logger* — and cannot be given one, because the logger is built from the very Serilog pipeline the sink is plugged into, so injecting it is a DI cycle that fails at host build. The emit path is `Append` → ring + `TryWrite`, with no place a second record could come from. (2) The pump sets an `AsyncLocal` suppression flag around the *synchronous* broadcast, which covers the one thing on that path that does log — `IpcServer`'s backlogged-client warning. The record still reaches console and file; only the feed drops it. (3) The 100 ms dwell is the backstop: an escapee logged from another task costs one extra line per flush window, never a loop, because the pump cannot outrun its own timer.
- **The ring buffer exists here and deliberately does not for run events.** Run events have no history because nothing is recorded while unsubscribed, so a buffer would be stale. The log is written whether or not anyone is watching, and «Лог» is opened precisely to see what just happened — so the daemon keeps `LogLimits.HistoryCapacity` (1000) entries always, and `SubscribeLog` answers with them. The cost is named: rendering + storing every record is paid with the panel closed.
- **`SubscribeLog` enables the stream BEFORE snapshotting the ring.** The other order leaves an unfillable gap; this one can only duplicate the single record that lands between the two lines, and `LogEntryDto.Seq` lets the panel drop it. Reconnect **replaces** the feed rather than appending — `Seq` is only monotonic within one daemon lifetime.
- **The subscription lives as long as the panel, not as long as the mode** — the one place this departs from run events (which are scoped to «Макросы»). The question the log answers is "did something go wrong", and it is asked from whatever mode you are in; a counter that only updates while you are looking at it is not a signal. So the rail counter is **problems (Warning+) in the feed**, the only counter that counts a subset of its rows: total entries saturate at the cap and a constant is not information.

Filtering (level + substring) is **panel-side only**, because moving the filter down must *reveal* what already arrived — something a server-side filter cannot do. The lever that actually reduces wire volume is the daemon's own Serilog `MinimumLevel`, and the settings screen does move it — `SetLogLevel` over a `LoggingLevelSwitch`, persisting nothing; see «Settings» for why that one knob deliberately stays out of `settings.json`.

### Settings, and the live settings store (D6)

The screen is mockup **2c** minus its bottom strip (one screen, no scrolling, tiles in the shell), and «Настройки» is a sixth rail row **below a divider, with a gear and no counter** — rail counters answer "how many are there now", and settings have no such number. That slot is now empty: the red dot that filled it reported failed diagnostics, and went with them.

**The mechanism mattered more than the field list.** Every engine knob used to arrive through `IOptions<T>`, computed once at startup, so any change needed a daemon restart — a trap `CLAUDE.md` documented separately. A settings screen on top of that would have been more annoying than Notepad. So `Agent`, `ProcessProfiles` and `Vision` left `appsettings.json` for `Core/Settings/SettingsStore` — **the `MacroGraphStore` pattern, deliberately, down to the debounced `FileSystemWatcher` and own-write suppression**: the daemon owns `settings.json`, the panel edits it over IPC, the daemon raises `SettingsChanged`, subscribers re-read. `appsettings.json` keeps Serilog and nothing else.

Four facts constrain anything built on it:

- **Consumers read `ISettingsSource.Current` AT THE POINT OF USE and never cache it in a field.** That single rule is what makes the knobs live: poll intervals apply on the next tick, vision thresholds on the next match, input method on the next activation cycle, profiles to newly adopted windows. The snapshot is immutable, so a read is one reference. *Verified live*: a profile added by editing the file in a text editor was picked up by the running daemon, `ProcessMonitor` started watching the new process on its next tick, and the window appeared in «Окна» — no restart anywhere.
- **`SettingsStore`'s constructor WRITES when the file is missing** — the one deliberate departure from `MacroGraphStore`, whose "constructor writes nothing" is an invariant. The reasoning inverts: the daemon must work without the panel (the panel is on-demand and may be absent for days), so a first launch on a new machine cannot require "open the panel and press Save". An *existing* file is never rewritten, and an **unreadable** one is neither rewritten nor allowed to reset anything — the user put a stray comma in while editing by hand, and "the program silently restored factory defaults" is losing their work.
  - ⚠️ **That promise was the STORE's, and the next command undid it.** The store honestly did not rewrite the file — but the panel showed defaults *as* the file, so the user read "my settings reset", pressed «Применить», and a file one comma away from working was gone for good. The verdict now travels: `SettingsStore.Fault` → `SettingsSnapshotDto.FileFault` → an amber strip **at the top of the settings form**. Not a diagnostics card (there are no diagnostics) and not a tooltip: a person reads a warning where they are about to press the button. **«Применить» is deliberately NOT disabled** — deciding to overwrite a broken file is legitimate; what was dangerous was the silence, not the possibility. Verified live: breaking `settings.json` in a text editor raises the strip through the ordinary `SettingsChanged` push, and «Применить» then writes the file and clears it.

- **Writing `settings.json` is ATOMIC, and the mechanism is shared with the `.hsm` writer** (`Shared/Io/AtomicFile` — `MacroBundleWriter` delegates to it, and `MacroBundleFolder.Import` uses its `Replace`). It used to be `File.WriteAllTextAsync`, i.e. truncate and write in place: the one write in the project that bypassed the very machinery `MacroBundleWriter` carries a whole paragraph about. A daemon killed mid-write left half a JSON, and half a JSON is unreadable — that is every setting the user has. Both halves of the mechanism came along and both are load-bearing: `File.Replace` rather than `File.Move(overwrite: true)` (measured; the four-way table lives in `AtomicFile` now), and **a temp name that misses the watcher's filter**. ⚠️ Here that filter is the file NAME, not a mask, and `settings.json.tmp` misses it in the long form and in 8.3 (`SETTIN~1.TMP`) — **a third caller must check the same thing against its own filter**, because a temp file that does match means every save wakes the daemon twice.
- **The log level stays in `appsettings.json`, against the general rule of shrinking it.** If the daemon trips on reading the settings file, the only level that can debug that is the one known BEFORE the read; a setting that breaks the diagnosis of its own breakage is the worst kind. So `SetLogLevel` moves a `LoggingLevelSwitch` and persists nothing, the change lasts until the daemon restarts, and the screen says so. `MinimumLevel.ControlledBy` must come AFTER `ReadFrom.Configuration` — the other order and the knob silently stops working.
- **The editor stages; the store is live.** Edits accumulate and go on «Применить». Applying per keystroke would mean "12" is briefly "1", and the daemon would honestly work a second at a one-second interval. Live means "no restart needed", not "state changes under your fingers". The log level is the single exception (it is not part of the file). A `SettingsChanged` push — which also fires for a Notepad edit — **does not clobber a half-filled form**; it swaps the baseline so «Отменить» shows the new file.

**The whole wake bracket is out of the UI now, as a `Hooks` block in `settings.json`.** The profile shrank to what a person actually chooses — which process to watch, and what to type with; the three columns ПОБУДКА / ОСЕДАНИЕ / ДЕАКТИВ. are gone. The bracket is a reverse-engineered crutch for one game, not a setting.

```json
"Hooks": { "elementclient_64": { "ActivationLParam": 37336, "SettleMs": 200, "DeactivateMs": 100, "On": ["Input","Capture"], "Scope": "Action" } }
```

- **Absence of a key is absence of the bracket**, not a bracket of zeroes — non-game profiles depend on exactly that, and `KnownActivationSignals` is still applied **when a hook is CREATED**, never on read.
- **`settings.json`, not `appsettings.json`**, though the knob is deeply technical. The latter is not «the file for technical things», it is the file read ONCE before everything else, and the single survivor there (the log level) is there because a setting that breaks the diagnosis of its own breakage is the worst kind. The lParam has no such property, and moving it would cost the live editing D6 exists for — plus split one process across two files with different reload semantics.
- **`On`, not `Triggers`** — «trigger» in this project already means the thing that starts a macro. An empty `On` is legal: the hook exists, the numbers are kept, it fires nowhere.
- **No flag for the icon**, deliberately. `WM_SETICON` goes through a frozen queue today and works; folding it into the bracket is a behaviour change testable only in game. Same rule as `SendInput`: offering the untested is worse than not offering it.
- **Old files are read once, in memory, and never rewritten.** A `settings.json` whose profiles still carry `ActivationLParam`/`SettleDelayMs`/`DeactivateDelayMs` is lifted into `Hooks` **only when the `Hooks` member is absent entirely** — an empty `"Hooks": {}` is a deliberate «no brackets» and is not overridden. Read through a raw `JsonDocument` so the model does not keep legacy fields, which would leave the concept in two places, i.e. not do the wave at all. ⚠️ Backwards compatibility is off in this project everywhere else; this is the exception, and the reason is that the store's own invariant is «an existing file is never silently reset».
- ⚠️ **The delay ranges are now unreachable from the panel.** `AppSettingsValidator` still bounds `SettleMs`/`DeactivateMs`, but there is no field on screen, so a hand-edited `SettleMs: 20000` is refused by «Применить» with a message about a control the user cannot see. Chosen knowingly — letting a ten-minute settle into the engine is worse — but it is a new rough edge.

**Autostart is the only part applied outside the process, and it is now one checkbox → one mechanism** (issue #27, closing #4 and #5). «Запускать при входе» is a value in the `Run` key in `HKCU`, full stop. «Работать с правами администратора» is not a registration at all: **the daemon's manifest became `asInvoker`, and the daemon checks its own elevation at startup and relaunches itself through `runas`** (`Daemon/ElevationRelaunch`). The Scheduled Task is gone — with it went spawning `schtasks.exe` on every daemon start, a start that hung on a sick Task Scheduler, and reconciling three mechanisms against two checkboxes. **The price is named and was accepted: with both boxes ticked there is a UAC prompt at every logon**, because the `Run` key cannot elevate. Do not "fix" that by bringing the task back.

- **The manifests are ASYMMETRIC and that is deliberate.** The daemon is `asInvoker` — otherwise "check whether we have rights" has no meaning, since `requireAdministrator` means it cannot start without them. ⚠️ **The panel stays `requireAdministrator`**: it captures game windows with `PrintWindow`, and from an unprivileged process UIPI turns that into a silently black frame — which is exactly what the future region-picker dialog would hit.
- **The mutex order is the load-bearing part.** Acquire first (so «the daemon is already running» costs an exit code, not a UAC prompt for a copy that would immediately lose the lock) → **release before starting the copy** (the copy takes the same name while we are still alive; without the release it would see «уже запущено» and leave the user with no daemon at all) → wait until the name is taken, or the copy dies → **take the lock back** if it never came. `SingleInstanceGuard` grew `Release`, `TryReacquire` and `IsTaken` for exactly this, and the polling is deliberately not a second `TryAcquire`: that would steal the name from the copy.
- **Access-denied on the mutex means «taken», not «broken».** The name is `Global\` precisely so it crosses sessions and users, so the owner can be a process whose default DACL does not name us — and then we are not free, we are not allowed to ask. Measured with a deny-DACL: `new Mutex(false, name)` and `Mutex.TryOpenExisting` both throw `UnauthorizedAccessException`; unhandled, that is a crash instead of a clean «уже запущено». ⚠️ **What this is NOT, so nobody re-derives the scare:** «one binary now runs both elevated and not, so the ordinary copy cannot open the elevated one's object» sounds right and **was measured false** — checked against a live elevated daemon from a medium-integrity process of the same user, the ctor opened it and `WaitOne(0)` honestly said "taken". The filtered and full tokens carry the same user SID.
- **A refused UAC prompt does NOT kill the daemon**, and that is the decision, not an oversight. Exiting would lock the user out of the very setting that caused the exit: the panel needs a live daemon to edit `settings.json`, so «uncheck the box» would become unreachable and every launch would repeat the prompt. So the daemon continues unelevated — and **says so through a native message box** (`Win32MessageBox.Warning`, on a background thread so a logon start is not held up by a modal loop), because the environment-diagnostics strip that used to report this class of failure is gone and a log line is the D4 defect. Refusal and «the copy did not come up» are two different messages: the next action differs.
- **Reconciliation is still FULL and still happens at startup.** An unticked box must *delete* the value, or the program keeps launching after being told to stop; and `StartupReconciler` reconciles on start, not only on edit, so an install whose file says "run at logon" but whose registry entry was swept away fixes itself without the panel. It is synchronous now — a registry write costs microseconds, and the detach-from-`await` that `schtasks` needed went with `schtasks`.
- ⚠️ **`asInvoker` also means the write probe now runs unelevated.** An install under `C:\Program Files` that used to be writable because the daemon was always elevated will now fail the probe and exit with code 2 before it ever gets to relaunch. That is consistent with the documented stance («portability rests on being unzipped somewhere writable») and the message already says the right thing, but it is a behaviour change.

**Diagnostics («Проверить среду») are gone — all seven checks, the request, the strip, the rail dot** (issue #26). They existed because almost every failure of this application is environmental and all of them look identical: the macro just doesn't work, and the log is silent. That reasoning was sound and the screen still cost more than it paid — «Проверить среду» sounded harder than it helped.

⚠️ **What that gives up is named, because the owner named it.** Four failures reported themselves ONLY there and now report themselves nowhere the user will look: UIPI blocking input into elevated game windows, a display scale other than 100% (every template is pixel-exact for it), an installation folder that cannot be written to, and hotkeys suspended because «Макросы» is open. A fifth — autostart registration refused for want of admin — is covered above. Two things survive because they were never diagnostics-only: a macro's validation errors still show on its library row, and `GetHotkeyFailures` still puts ⚠ there for a chord Windows would not grant.

**The count-written-out-in-four-places trap closed itself**, and that is worth noting rather than celebrating: it was a real trap (adding the autostart check left all four places saying "five of six"), and it is gone only because there is no count. Do not read its disappearance as a technique.

**`EnvironmentProbe` (`Native/Diagnostics/`) survives with ONE method.** `IsElevated` has a non-diagnostic caller — `AutoStartManager.NeedsElevationRelaunch`, which is a question about *starting*, not about diagnosing. `IsBlockedByUipi`, `DisplayScalePercent` and `IsFolderWritable` went with their only consumer, and so did `AutoStartManager.DescribeMechanism` and the four `Settings_Engine_AutoStart_*` strings behind it — that family was the daemon-side duplicate of `Settings_Startup_Mechanism*`, and the panel computes its own.

**`SendInput` has a slot in the model and no implementation, and the UI does not offer it.** Offering an untested input method is worse than not offering one. A value that reaches the file by hand resolves to `SendMessage` with a warning logged once per process — never silently, or the symptom would be "a setting that does nothing". Input-method wording lives once, in `Contracts/Settings/InputMethodInfo`: the default-input list and the per-profile picker both read it, because a copy in markup drifts invisibly.

Found by eye while running it, none of it visible to build or tests: `ScreenRect`'s computed `TopLeft`/`Right`/`Bottom` were being SERIALIZED into every hand-edited file (settings and macro bundles alike) — three fields that look settable and silently do nothing, now `[JsonIgnore]`; a checkbox whose two-line hint sat inside its content put the box next to the *second* line; the change counter had no home in the markup; «1 окон».

⚠️ **The fixed-height `TextBox` trap.** The theme's field padding is 11.2px vertical and the text sits inside a clipping `ScrollViewer`; a caller-supplied `Height` of 24–26 leaves less room than the line needs and severs descenders exactly at the baseline — letters stay legible, only the tails of «р»/«у»/«д» vanish. Three sites had it (log search, macro name, library search); all now pass `Padding="8,0"` + `VerticalContentAlignment="Center"` alongside their `Height`, and the trap is written up at the theme. Build and tests cannot see this class of defect — it was found by measuring glyph ink rows against a reference `TextBlock`.

**Elevation is now asymmetric.** The panel is `requireAdministrator` and stays that way (`PrintWindow` into an elevated game window); the daemon is `asInvoker` and elevates *itself* when the checkbox asks — see the autostart bullets above. They stay two executables regardless; see «The shipped layout» for why merging them is not on the table. ⚠️ **A medium-integrity shell cannot terminate an elevated process** — `Stop-Process`/`taskkill` return access denied — so the panel, and a daemon that took the UAC prompt, have to be closed from an elevated context. A wedged panel also holds the single-instance mutex and renames locked DLLs to `*.locked<pid>` in its `bin/`; those clear themselves when it finally exits. A daemon started with the rights checkbox OFF is an ordinary process and can be killed normally, which is a real convenience during development.

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

Six projects, two executables:

- `Daemon` (WinExe, tray + hosted engine) → `Core` (all daemon-side domain logic) → `Contracts` → `Shared` → `Native`
- `App` (Avalonia panel) → `Contracts` → `Shared` → `Native` — **and nothing else.** No `SmartMacro.Core` reference: that is the load-bearing constraint of the split, and the reason the panel's output directory contains no OpenCV, no Tesseract and no native vision blobs. If a view-model needs something from Core, the answer is a new IPC message type — or, since F3, the shared domain in `Shared`; never a reference to Core. `App/Macros/MacroLibrary` is the one piece of domain state the panel owns: the `macros/` folder, over `MacroBundleFolder`.

`Native` is Win32 P/Invoke via `LibraryImport`, no dependencies.

**`SmartMacro.Shared` (F1) is the shared DOMAIN**: the macro graph model (`SmartMacro.Macros.Model`), its pure validator (`SmartMacro.Macros.Validation`), the pure analyses (`SmartMacro.Macros.Analysis` — `MacroVariableAnalysis`, `MacroTemplateAnalysis` narrowed to one graph in F2, and `MacroExtraction` added by F4) and the `.hsm` bundle reader/writer/inventory (`SmartMacro.Macros.Bundle`). Namespaces are `SmartMacro.Macros.*` — kept from when these lived in Core, and the reason the move out of Contracts needed **zero `using` edits**; `RootNamespace=SmartMacro` in the csproj is what keeps folder and namespace in step.

**`SmartMacro.Contracts` is the PROTOCOL**: `SmartMacro.Contracts.Dto` (`WindowDto`, `RunningMacroDto`, `ValidationIssueDto`, `HotkeyFailureDto`, `LogEntryDto`, …), `SmartMacro.Contracts.Settings` (`AppSettings` + its validator), and `SmartMacro.Contracts.Ipc` (envelope, `IpcMessageTypes` catalog, `IpcJson`, `IpcPipe`, `IpcConnection`, `InstallationLayout`, `PeerExecutableLocator`).

⚠️ **The two tiers have DIFFERENT rules — do not merge them in your head.** `Contracts`: references `Native` + `Shared`, **no file IO, no registry, no processes**. `Shared`: references `Native`, **file IO IS allowed** — reading and writing `.hsm` is needed on both sides and that means `System.IO.Compression`. The half that is common and load-bearing: **no OpenCV, no Tesseract, in either.** Everything in `Shared` ships inside the panel too, so "files are allowed here, let's also put template decoding via OpenCV here" is exactly the move that undoes the split. Both rules are written in the respective `.csproj` headers.

**Direction is `Contracts → Shared`, and it follows from what was already written**: protocol payloads mention the domain (`ValidationIssueDto` maps `ValidationIssue`, `IpcJson` is a copy of `MacroGraphJson.Options`). There is no mention the other way — the model, the validator and the bundle know nothing about the envelope or the pipe, and must not, because the domain has to be readable with no daemon running. **F3 thinned that dependency without reversing it**: the graph no longer travels at all (`SaveMacroRequest` and its `MacroGraph` are gone), so the copied JSON options survive not for polymorphic nodes but because the rules the wire does need — string enums, out-of-order metadata — are already configured there, and a second set of the same rules would cost more than one.

Anything needing OpenCV/Tesseract belongs in Core; mappers from live Core types to DTOs therefore live in `Core/Ipc/DtoMappers.cs`, not in Contracts.

### IPC

`Core/Ipc/IpcServer` accepts on the named pipe and fans engine events out; `Core/Ipc/IpcRequestDispatcher` turns one envelope into one envelope. `App/Ipc/IpcClient` is the other end: a single reader loop demultiplexing `Id`-bearing responses into pending `TaskCompletionSource`s versus raising events, with a reconnect loop for the process's lifetime.

Four protocol facts every caller has to respect:

- **Responses may arrive out of order.** The server does not await a handler before reading the next request. Correlate by `Id`; never by arrival.
- **A client that stops draining events is dropped.** So `IIpcClient.Connected` is a re-fetch signal, not a nicety — every view-model re-seeds its snapshots there, and reconciles (drops rows the daemon no longer reports) rather than merely upserting.
- **Nothing about macros is asked of the daemon (F3).** Not the library, not writing, not templates: the panel reads and writes `macros/` itself. `SaveSettings` inherited the old `SaveMacro` convention (`[]` means written) because the settings file is still the daemon's.
- **Two of the pushes are opt-in and per-connection**, and they are the only two frequent ones: `RunEvents` (`SubscribeRunEvents`) and `LogEntries` (`SubscribeLog`). Their flags are separate on purpose — one is scoped to «Макросы», the other to the panel's whole life — so neither mode pays the other's traffic. Both are forgotten with the connection, so both must be re-requested on every `Connected`.
- **Hotkey suspension is a THIRD thing forgotten with the connection, and it had to be taught that.** `SuspendHotkeys` edits daemon state, so before the review a panel killed by Task Manager while in «Макросы» left the daemon with zero registered chords — forever, since `OnMacrosChanged` deliberately does not re-register while suspended — and nothing could ask whether they were suspended. It is now a **lease**: a per-connection flag plus a holder count in `HotkeyListener`, released by the same `finally` that drops the two subscriptions. Both halves earn their keep — the flag stops one chatty client leaking a second lease, the count stops a departing panel restoring chords while a second panel still has a key-capture box open. ⚠️ The *reporting* half of that fix is gone with the diagnostics: `IHotkeyRegistration.IsSuspended` is still correct and still there, but no screen reads it any more.

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
- **WindowLifetimeMonitor** (`Core/Windows`) is the whole of window-death handling: ONE `IHostedService` that sweeps the registry snapshot on the window-poll interval from `settings.json` (re-read per tick, so changing it applies live) and unregisters windows whose `IsAlive` went false; `StopAsync` clears the registry entirely. W0.4 replaced `CharacterAgent` + its factory + `AgentMessage`/`AgentStoppingMessage` + the orchestrator's inbox channel with it — four layers and ~270 lines for the fact "a window closed". **Do not fold it into `WindowRegistry`** (which is what the old TODO suggested): the registry is a pure synchronous state holder under one lock, and that is exactly why it is trivial to test.
- **WindowRegistry** (`Core/Windows`) is the sole owner of window tags AND the `hwnd → IGameWindow` lookup. Tag selectors (`RequireTags`/`ExcludeTags`) route every fan-out; "identified" just means "has at least one tag".
- **Orchestrator** (`Core/Orchestration`) turns triggers into runs. Hotkey runs have no context window (macros must route by selector) and are single-flight per macro NAME; process-appeared runs get the new window as context and are single-flight per (macro, window) so N clients launching at once each boot. Both seed the `cursor` variable via `CursorPositionProvider`. Its `OnProcessAppeared` is also the only place a window is *adopted*, and **`Register` and `StartProcessAppearedMacros` are deliberately adjacent, synchronous lines** — a node reaching an unregistered hwnd fails at execution, so nothing may go between them. Ordering at shutdown is the mirror image: `WindowLifetimeMonitor` is registered in the host BEFORE the orchestrator so it stops AFTER it, and windows leave the registry only once in-flight runs have been cancelled.
- **Macros** — the model (polymorphic `$type` nodes + triggers), its validator and the `.hsm` bundle live in `Shared/Macros`; `Core/Macros` keeps the daemon-side halves: `Execution` (`MacroExecutor` walker, `MacroPrimitives`, `MacroRunRegistry`, run variables) and `Storage`. The node catalogue and its semantics are in `docs/spec.md` §5.1; see «The node model» below for the two things about it that constrain callers.
- **`MacroGraphStore`** (`Core/Macros/Storage`) is the daemon's READ-ONLY view of the library: one `.hsm` BUNDLE per macro under `macros/` in the installation root, filename stem = macro name. Its snapshot is a list of `MacroLibraryEntry` (graph + passport + template inventory + the validator's verdict); `All` still hands out bare graphs, `Armed` hands out only the ones without errors, and that second list is what `HotkeyListener` reads. `MacroLibraryEntry` also carries the bundle's SUBMACROS, which is what the orchestrator turns into `MacroRunContext.Submacros` (F4) — the name resolver `IMacroGraphResolver` is gone with cross-macro calls. It supplies `HotkeyListener`'s bindings (re-registered on every change) and tells the orchestrator which graphs a new process should boot. **It writes nothing at all** (F3 grew this from «the constructor writes nothing»): the constructor creates the folder if missing, reads it, and stops. That is an invariant written on the class, not an accident — until backwards compatibility was dropped the same constructor migrated a legacy `macros.json`, renamed it and `hotkeys.json` to `*.migrated`, seeded six `pw-*` examples and dropped a `.examples-seeded` marker, so *constructing the object* meant *changing state on disk*. `DefaultMacroGraphs` and `LegacyMacroMigration` are gone; do not hang start-up side effects back on the ctor.
- **Identification** is no longer built in: it is an ordinary macro the user writes — `KeyPress(C)` → `Delay` → `MatchTemplateSetNode` (template set `"classes"` → `templates/classes/{tag}.png`, writes the variable `tag`) → `AddTagNode` `{tag}` → `SetIconNode` `Assets/ClassIcons/{tag}.png` → `KeyPress(C)`. **`AddTag` is its own node in that chain** — the match node stopped hanging the tag itself. Master/ignored characters are just tags in a selector (`ExcludeTags: ["Лучник", "Шаман"]`).

### The node model: `Guid` identity, `DisplayName` label

A node's `Id` is a **`Guid`** and every edge (`Next`/`Found`/`NotFound`/`Timeout`/`Matched`/`NotMatched`) and `StartNodeId` holds one. What the user sees and types is `DisplayName`, generated as family prefix + a graph-wide running number — `click-1`, `delay-2`, `find-3` — so the number doubles as insertion order. `MacroNodeNames` (Shared) owns the whole rule, including the fallback used when a hand-edited file carries no label: a blank row in the run log is worse than a generic one.

Two consequences worth knowing before touching this:

- **Renaming is not a graph operation.** It raises `PropertyChanged` and nothing else. The machinery that used to repoint every inbound edge, move the start node and re-push breakpoints on rename is **deleted, not idle** — do not reintroduce a "rename" path that walks the graph.
- **A duplicate `DisplayName` is a WARNING on both nodes**, not an error and not on one. Clicking an issue highlights a node, so blaming one of the pair picks arbitrarily; and `ValidationIssue` carries `NodeId` (to find) *and* `NodeName` (to print) because either alone is insufficient — plus, since F4, `SubmacroId`, because finding a node also means opening the right graph.

Vision nodes carry `MatchThreshold?`; `null` means "the default from settings". The inspector shows the word «из настроек» rather than a number — the default lives in the daemon's settings and printing the shipped value would name a threshold the node will not actually run with. Same rule as the targets badge: the panel must not state as fact something it cannot know.

**There is no migration and none is coming.** A pre-`Guid` file simply does not parse, and neither does one carrying `"$type": "recognizeTag"` — the reader names the type instead of crying corruption (see «Matching gives a VALUE»). The move-aside to `*.json.incompatible` that used to soften that is **gone with the bundle format** — see «Templates live inside the macro» for why moving a future-version bundle aside would be a lie. `MacroGraphJson` uses `UnsafeRelaxedJsonEscaping`, so Cyrillic inside `nodes.json` reads as «Лучник», and the bundle is store-only so the whole file greps and diffs like text.

### Win32 input model (the hard-won part — do not "simplify" without re-testing in game)

PW freezes background clients (input + rendering). Every input session is bracketed by `IGameWindow.ActivateAsync` (sends magic `WM_ACTIVATEAPP` with lParam from the window's `ProcessProfile`) and `DeactivateAsync` (drain delay, then deactivate — unless window is foreground). `AgentInputDispatcher` wraps this lifecycle around every keypress/click, one cycle per node.

- **Keyboard = SendMessage, mouse = PostMessage** (`GameWindowFactory` bakes this in). Post'd keys got dropped by the frozen pump; clicks were always reliable.
- **Modifier chords (Shift+1) DO NOT WORK** via message injection: PW reads modifiers with `GetKeyState`, which cross-thread SendMessage never updates. That's why the `pw-assist` example selects the master by *clicking* party-slot-1 instead of Shift+1. There is deliberately no chord node and no chord primitive — stage 4B deleted the callerless `PressChordAsync`/`SendChordAsync` rather than leave working-looking code that silently no-ops in game. Do not add them back.
- **WM_SETICON must be SendMessage** — Post'd icon updates sit unprocessed in frozen queues. `WindowIconService` also retries at +2s/+5s because PW's post-boot init can reset the icon.
- The game runs elevated, so this app has to reach the same integrity level or UIPI blocks input and capture. The panel gets there by manifest (`requireAdministrator`); **the daemon gets there by choice** — its manifest is `asInvoker` and it relaunches itself through `runas` when the settings ask (see «Settings»). Unticking that box does not break anything visible: it silently stops every keypress, click and screenshot from reaching the clients.

**The wake/freeze bracket is a SCOPE, not a pair of calls.** `await using var scope = await window.EnterHookAsync(HookOn.Input, ct)` — `WindowHookScope` is a readonly struct, so a keypress allocates nothing. An empty scope (`IsHolding == false`) is legal and means «this process has no hook». The pair form is what failed before: the review found four brackets in `GameWindow` with no `try/finally`, and a cancellation mid-vision-tick left up to ten clients awake with nobody to re-freeze them. `await using` makes the closing the compiler's job. The closing method has **no cancellation token in its signature** — a cancelled deactivate strands the window, and that rule is now unexpressible-to-break rather than remembered.

- **A refcount per window, plus a transition chain.** Needed because the panel (region-select capture) and the daemon (a run) can want the same window at once. First enter wakes, last exit freezes. ⚠️ The refcount alone would have introduced two defects that did not exist before it: a second enterer skipping the settle and writing into a window that is not awake yet, and a freeze overtaking somebody else's wake. `_transition` chains them; without it «a refcount per window» is worse than none. Granularity is unchanged — sequential nodes still go 0→1→0, so it is still one wake cycle per node.
- **The drain belongs to INPUT, not to the bracket.** `DeactivateMs` is paid on the last exit only if a `HookOn.Input` was inside. That is today's behaviour to the letter: both capture paths froze without draining. Making the drain shared would have added 100 ms × 10 windows to every vision tick.
- **`ActivateAsync`/`DeactivateAsync` left `IGameWindow` but survive on `GameWindow`** as the two halves — they are what `GameWindowActivationTests` pins, and `ACancelledDrainStillRefreezesTheClient` is inexpressible through the scope (whose closing takes no token). They do not touch the refcount.
- **The scope must stay INSIDE `IMacroPrimitives`.** Hoisting it into `MacroExecutor` reads tidier and breaks the debugger-gate argument above. The run-level references are taken by the WALKER, where targets are already resolved — threading the run through `IMacroPrimitives` would be the parameter-poke F2 refused for templates.

### Vision

`IGameWindow.FindElementAsync` (one shot) and `WaitForElementAsync` (poll until timeout) are the generic primitives; both return the CLIENT-SPACE CENTRE of the match, which is what `FoundPointVar` feeds to a later `ClickNode`. Each tick is an active capture (wake → PrintWindow with `PW_CLIENTONLY | PW_RENDERFULLCONTENT` → re-freeze) + grayscale `TM_CCOEFF_NORMED` matching. Grayscale, NOT binarized — game UI sits on semi-transparent backgrounds where binarization is unstable. `ClassMatcher` (stats-window text on solid panel) is the exception that still binarizes.

Templates come out of the running macro's own bundle — see «Templates live inside the macro (F2)» above for the layout, the per-run source, the cache and where the source PNGs went. `TemplateSetProvider` is deleted. All coordinates/templates are pixel-exact for the author's 3840×2160 screen; they live in macro nodes, not config.

**Assets ship from the daemon project.** `src/SmartMacro.Daemon/Assets/**` — after F2 that is per-tag `ClassIcons`, and nothing else that is authored there. Stage 3 moved them out of App because every reader of them runs in the daemon. `ClassIcons/` stays in `daemon/Assets/`: a `SetIconNode` carries the string `Assets/ClassIcons/{tag}.png`, `WindowIconService` resolves it against `AppContext.BaseDirectory`, and moving those files would break every macro that names one.

### The application mark (`assets/logo/`)

Variant **1a «Цикл»** from Claude Design: a repeat arc with an arrowhead and the accent play triangle inside. The three SVGs are stored **verbatim as delivered** (`icon.svg`, `icon-small.svg`, `icon-tray.svg`) so a re-import reads as a diff rather than an investigation.

- **One binary, three placements.** `assets/logo/icon.ico` is `<ApplicationIcon>` for *both* exes and — via `Link="Assets\icon.ico"` — the file the tray loads. `icon-256.png` is the panel's `WindowIcon`, linked in as `avares://SmartMacro/Assets/icon.png`. Nothing is copied into a project folder: a mark duplicated per project is two files obliged to match, and they stop matching the day someone redraws it and updates one.
- **`tools/IconForge` rasterises, and it is deliberately NOT in `SmartMacro.slnx`.** It parses the SVG and feeds the path data straight to `SKPath.ParseSvgPathData`, so the geometry exists once. Re-run it by hand after editing a mark; the outputs are committed, so an ordinary build never needs it. It also emits `preview.png` — every size on a light and a dark strip, small ones pixel-doubled — which is how the contrast below was found.
- ⚠️ **Frames below 256 px are classic DIB, not PNG.** The tray goes through `LoadImage` + `LR_LOADFROMFILE`, which does not read PNG frames inside an ICO on every Windows build (unlike `LoadIconWithScaleDown`). The failure mode would be an empty slot in the tray with nothing in the log. 256 has no choice and is PNG, normal since Vista.
- **The title bar draws the mark as VECTOR, not the PNG.** `MainWindow.axaml` carries the two paths in a `Viewbox` so the mark takes the theme's brushes and stays sharp at any display scale. The 9×9 accent square it replaced was never the window icon — the custom chrome draws its own strip, and `WindowIcon` only ever showed in Alt+Tab and the taskbar.
- **Known limit: on a LIGHT taskbar the ring nearly vanishes.** `#c9c9d1` against `#d9d9de` is about 1.4:1, so at 16 px only the accent triangle carries the mark. The design ships `icon-tray.svg` in `currentColor` precisely for host tinting, but the tray loads one static file. Fixing it properly means a second ICO plus reading `SystemUsesLightTheme` and reacting to `WM_SETTINGCHANGE`; the author's system is dark on both keys, so it is recorded rather than built.

**Tesseract is parked and no longer ships with the daemon (stage 4B).** `TesseractCoordinateReader` (HUD coordinate OCR) still works and is still exercised by `tools/VisionSampleRunner`, but the daemon does not register `ICoordinateReader` — nothing injected it, and the ctor eagerly builds a `TesseractEngine`. `SmartMacro.Core.csproj` marks the package `PrivateAssets="all" ExcludeAssets="build"`, which keeps both the managed dll and the 12 MB of `x64/`+`x86/` natives out of the daemon's output; `src/SmartMacro.Daemon/tessdata/` is gone and the language pack lives only with the sample runner, which carries its own PackageReference. Daemon output: 107 → 91 MB. Un-parking it for stuck detection = drop those two attributes and restore the DI line. ⚠️ Until then `SmartMacro.Core.dll` ships next to the daemon with a metadata reference to an assembly that is not there — harmless because the CLR resolves it lazily and nothing touches that type.

### The shipped layout (panel at the root, daemon in a subfolder)

Distribution is a **zip containing a folder**, and everything is inside it — no `%LOCALAPPDATA%`,
one path for the whole install:

```
SmartMacro/
  SmartMacro.exe      the panel, single-file — THE ONLY thing visible at the root
  macros/  settings.json  logs/
  daemon/
    SmartMacro.Daemon.exe + runtime + appsettings.json + Assets/ + its own logs/
```

`build/portable.proj` expresses it: `dotnet msbuild build\portable.proj` publishes the daemon into
`dist\portable\SmartMacro\daemon\` and the panel into `dist\portable\SmartMacro\`, `-t:Package`
adds the zip. Measured at `HEAD`: **76 files / 117.9 MB** — 1 file / 27.7 MB at the root, 75 /
90.2 MB in `daemon/`. F1 added exactly two files, both in `daemon/` (`SmartMacro.Shared.dll` + its
`.pdb`); F2 removed the 14 template PNGs and the `templates/` folder with them — the tree they made
up is gone, and a macro's PNGs now travel inside its own `.hsm`. **The shipped root is one file
and one folder**, which is as close to the point of the layout as it gets.

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
- **`LiftSharedContent` is gone (F2).** It lifted `templates/` out of `daemon/` into the shipped
  root, because the files came from the daemon project and the daemon publishes into a subfolder.
  There is no global tree to lift any more; a `Move` that matches nothing would be a build error
  over nothing.
- **`Publish` always cleans first**, so `dist/` is a BUILD OUTPUT, not an install: run from there and
  `macros/`, `logs/` and `settings.json` are gone on the next publish.
- **`VerifyNoClobber` and `build/publish-file-list.targets` are gone** with the shared folder — two
  different `PublishDir`s cannot put a file on top of a file. The
  `Microsoft.Extensions.Configuration.Binder` pin in `SmartMacro.App.csproj` stays as hygiene, not
  necessity.
- **The two exes must NOT be merged into one with a `--daemon`/`--panel` switch.** The manifest binds
  to the BINARY, not the mode, and the two manifests now differ: the panel is
  `requireAdministrator` (UIPI blocks `PrintWindow` into elevated clients), the daemon is
  `asInvoker` and elevates itself on request. A single exe can carry only one requested execution
  level, so merging would force both into the same one — and either the panel loses its capture or
  the daemon loses its checkbox. The reason is written in `build/portable.proj` because that file is
  where the temptation lands.

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
exists in order to write (macro library, log), and a live tray icon over an engine
that cannot save a line is a promise it will not keep. The channel is a native message box
(`Native/Dialogs/Win32MessageBox` — a WinExe has no console and the logger does not exist yet) and
the exit code is `2`. The panel has no probe (no shared assembly will take it: Contracts forbids
file IO, Native is P/Invoke only, and Shared — which may touch files — is the macro domain, not a
place to hang a start-up probe) but its logger construction is wrapped in a `try` with the same
box — before that it was the one place in the panel where a failure had nowhere to go and killed the
process silently.

### Runtime state files (in the installation ROOT, gitignored)

`settings.json` (all engine knobs — see «Settings» above), and `macros/*.hsm` (one bundle per macro, templates inside it) both live in the installation ROOT — the folder the panel sits in, one level above the daemon (see «The shipped layout»). The daemon's own `logs/smartmacro-*.log` is the exception: it stays in `daemon/`, next to the exe whose `appsettings.json` names it. `MacroGraphStore` loads on ctor → immutable snapshot → a debounced `FileSystemWatcher` raises `MacrosChanged` → subscribers (`HotkeyListener`, `MacroTemplateCache`, and the panel via the push) re-register live. Since F3 the watcher is the *only* source of change, because the daemon never writes; own-write suppression is gone with the writing. An unparseable file is skipped and logged, never fatal to the load — and shown as a row by the panel, which reads the same folder itself.

The panel writes `logs/smartmacro-ui-*.log` into the root as well, at an absolute path computed in code — in the dev tree the root IS the daemon's output folder, so both logs share one `logs/` there. The panel has no configuration file at all; an optional `appsettings.panel.json` next to the exe overrides the built-in Serilog defaults if the user drops one in. Every engine knob (`Agent`, `ProcessProfiles`, `Vision:*`) is the daemon's, and lives in `settings.json`.

`hotkeys.json` and the single `macros.json` are GONE — and so is the one-shot migrator that used to convert them. Backwards compatibility is off: a file in either legacy format is now just an unknown file the store ignores. A hotkey is a `HotkeyTrigger` inside the macro it starts.

**A fresh install starts with an empty library, and there is nothing to copy from.** Nothing seeds `macros/`, and `src/SmartMacro.Daemon/examples/` — the six `pw-*` graphs that used to ship as a handout — is deleted. After the node-id → `Guid` change they no longer loaded anyway, and a folder you have to copy files out of explains the program's internals instead of giving the user a button. The «Макросы» mode has its own empty state («create one with the button, bottom left», plus the folder path — a `.hsm` dropped in there is picked up live, templates and all), kept separate from «выберите макрос слева» because an empty list with "pick one" on it reads as a broken panel.

### Conventions

- Logging via source-generated `[LoggerMessage]` partial methods, usually split into a sibling `*.Logging.cs` partial file.
- Engine knobs live in `settings.json` next to the daemon, owned by `SettingsStore`, and are read through `ISettingsSource.Current` **at the point of use** — never cached in a field, or the knob stops being live. `IOptions<T>` and the `Config/` folder that held `AgentOptions` / `ProcessProfileOptions` / `ClassMatcherOptions` / `WindowVisionOptions` are gone. `appsettings.json` configures Serilog and nothing else. Coordinates, regions, templates, keys and timeouts belong in macro nodes, NOT in settings.
- `ScreenPoint` / `ScreenRect` record structs (in `Native`) for all pixel coordinates — bind from JSON as `{ "X": .., "Y": .. }` objects.
- Coordinate discovery: **«Указать на снимке…» in the `Click` node's inspector** — a click on a pixel of a fresh `PrintWindow` frame fills X/Y (see «Cutting a template out of a fresh frame»). The old route survives and is about something else: the user hovers the cursor in-game and triggers a macro, `CursorPositionProvider` logs the client-space point it seeds the `cursor` variable with — a LIVE point during a run, not one frozen into the node.
- View-models take `IIpcClient` + `IUiDispatcher` and nothing Avalonia-shaped, so every one of them is exercised headlessly against `FakeIpcClient` (`tests/SmartMacro.Tests/Ipc/`), whose canned answers go through the real `IpcJson` round trip.
- **Comments and xmldoc are in Russian** — the whole tree was translated once, deliberately as the last step of the refactor so the next wave would not re-import English. New code follows: comments, xmldoc, `[LoggerMessage]` templates, validator and abort messages, everything a human reads. **Not** translated: identifiers, test names, and technical names inside a Russian sentence (`SendMessage`, `WM_ACTIVATEAPP`, `hwnd`, `single-flight`).
- **The gate is build + tests, and it is not enough** — even now that `tests/SmartMacro.Tests/Ui/` takes back the measurable half (see «Headless layout sweeps» below). Around two dozen defects in this codebase were invisible to build and tests and found only by running the app and looking: a focus ring clipped to nothing, glyphs rendering as colour emoji and ignoring `Foreground`, a window that silently unmapped itself, a panel that would not start at all, text with its descenders sheared off. If a change touches the UI, run it and look at it — and if you could not, say so instead of implying you did. **The panel is `requireAdministrator`, but the manifest binds to the apphost, so `dotnet SmartMacro.dll` from an unelevated shell runs it anyway** (the panel's assembly is `SmartMacro.dll`, not `SmartMacro.App.dll` — its `AssemblyName` is `SmartMacro`). **The daemon needs no such trick since #27 — it is `asInvoker`** and starts unelevated on its own; set `Startup.RunElevated` to `false` in `settings.json` first, or it will relaunch itself and pop a UAC prompt you cannot answer from a script. Screenshot with `PrintWindow`, click with `mouse_event`; "I could not start it" is almost never true.

`docs/spec.md` (Russian) describes the system as built and is the first place to look; §14 is its decision history and the reason not to re-propose what was already rejected.
