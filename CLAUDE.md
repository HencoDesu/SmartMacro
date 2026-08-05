# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Windows-only desktop automation tool (C# / .NET 10 / Avalonia). Generic in design, Perfect World in practice: it watches for windows of configured processes, tags them (free-form strings, typically applied by OpenCV template matching), and runs user-authored **macro graphs** against them via Win32 messages — driving ~10 `elementclient_64.exe` clients through login and party-wide actions. UI text and commit messages are in Russian; tags are Cyrillic class names ("Лучник", "Жрец") because that's what the game renders.

## Where it stands

**Nothing is in flight.** The rename to SmartMacro, the node-graph macro model, tags replacing the character roster, the daemon/panel split, the whole UI, the debugger, the settings store, the shipped layout and the `.hsm` format **through wave F4** are all built and green. The format plan is finished; there is no remaining wave.

The refactoring plan that got us here was **deleted** once it was done — not out of tidiness: a plan file sitting in `docs/` reads as a to-do no matter what disclaimer you put on it, and that one had gone further than stale. Its node catalogue still described the string `Id` the model no longer has, and its IPC catalogue listed 21 requests against today's 27. It is in git history if you want it. `docs/design/implementation-plan.md` survives because the mockups next to it are still the visual reference — but it is history too, and several of its claims were disproved on contact (the targets badge needed no new IPC; the breakpoint dot could not live on the box corner). Read it for context, never as a to-do.

**`docs/spec.md` describes the system as built** and is the place to look first. Its most load-bearing part is **§14, the decision history** — tables of «было → стало → почему» covering what was dropped before the refactor (FOLLOW/HOLD/COMBAT state machine, LLM integration, per-character roster, nameplate identification), what the refactor itself changed, and the `.hsm` waves F1–F4. Read it before proposing anything that sounds like a fresh idea; a good share of "obvious improvements" are in there with a reason they were rejected.

⚠️ **§13.1 is gone, and that is the point.** It was the `.hsm` decision written up as something not yet built, and it survived three waves as a to-do. With F4 the format is finished, so its contents moved into the sections that describe what exists — the bundle layout and the library in §5.7, templates inside the macro in §8, submacros in §5.1, the panel's authorship in §5.7 and §6.1 — and the reasoning became rows in §14. Do not recreate a "planned format" section.

Gate: `dotnet run --project tests/SmartMacro.Tests` — **788 tests**, and they are expected green before anything is committed.

The sections below are the constraints that are load-bearing — the things that look arbitrary, are not, and will be "simplified" back into bugs by anyone who does not know why they are there. Wave tags (D4, D3b, …) survive only because commit messages reference them.

### Target selectors and hotkey conflicts (D4)

**The badge needed no new IPC request**, despite what the plan's table said. The matching rule moved out of `SelectorEvaluator` into `TargetSelector.Matches` (Shared since F1; Contracts before that) and now takes TAGS rather than a window, so both processes share one implementation: the daemon still calls `SelectorEvaluator` (kept as the typed wrapper — it is where `ManagedWindowInfo` is known, and Shared must not know it), the panel calls `Matches` directly against its own `WindowDto` snapshot. **Do not reimplement those two loops anywhere.** A badge that disagrees with what the executor actually targets is the one defect that makes the widget worse than nothing.

`MacroEditorViewModel` keeps its own `WindowCatalog` — seeded by `GetWindows`, kept current by the window pushes — and hands it to every node's `TargetSelectorViewModel` the same way it hands out `NodeIdChoices`. It is deliberately NOT `WorkspaceViewModel`'s list: that one holds editable rows with focus state, and injecting it would tie two modes together.

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

A panel connecting mid-run gets the live walks with `FromStart = false` and says so. There is deliberately no per-run history buffer: between a drop and a reconnect nobody was subscribed, so recording had stopped and a buffer would be stale.

### The debugger (D5)

`Core/Macros/Execution/MacroDebugSession` is the whole engine side: breakpoints, per-walk pause state, and the attach count. It reaches the walker as `MacroRunContext.Debugger` — the control sibling of `Observer`, with the same `IsActive` gate, so an undebugged walk pays one volatile read per node and allocates nothing.

**The gate is between two nodes, and that is the safety argument, not a convenience.** Every `ActivateAsync`/`DeactivateAsync` bracket and every vision tick's wake/re-freeze lives entirely inside `IMacroPrimitives`, so by the time control is back in `MacroExecutor` no game window is left woken. Pausing there cannot strand a frozen client; pausing anywhere deeper could. `ABreakpointParksTheWalkBeforeTheNodeRuns` pins it with a primitive-call count.

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
- **The two read paths survive, further apart than before.** The executor's is the cache. The BROWSER's (`MacroGraphStore.TemplateCatalog`/`ReadTemplate`) opens the `.hsm` on every request and never touches the cache — the editor exists precisely to show what is in the bundle *now*.
- ⚠️ **The 14 source PNGs are in `assets/templates/` at the repo root**, outside every project so no glob can drag them back into the run path. Eleven are class names cut by hand from the game at 3840×2160 — hours of work that cannot be redone without a live client. They are the material you drag into a macro with «+ файл…»; they are not shipped and are not read by anything at runtime.

**`ShellMode.Templates` is gone from the rail** and `TemplatesViewModel` folded into `MacroEditorViewModel` — the rail is about entities, and a template stopped being one. Its counter went with it: there is no "how many templates are there" number any more, only "how many does THIS macro have", and that lives in the inspector next to triggers and variables. Selecting the browser's row still costs one image request; the list is still metadata only; there are still deliberately no thumbnails.

**«НЕТ ФАЙЛА» is gone and the validator has it instead, which is a promotion.** A bundle's template set is known statically, so `MacroGraphValidator.Validate(graph, inventory)` says "this node names a template the macro doesn't have" at save time (panel) and at load time (daemon), rather than in a section you had to go and open, or a log line mid-run. It is a **Warning**, not an Error: an error would block saving, and "type the name, then import the file" is a normal order of operations; a missing template does not abort the run either — the node takes «не найдено», which is what that edge is for. ⚠️ **`null` inventory and an empty one are different**: `null` means "the bundle's contents are unknown" and skips the check entirely. Both ends pass a real one — the daemon from the library entry, the panel from `TemplatesViewModel.Inventory` — because two runs of one validator disagreeing is the D4 targets-badge lie again.

**The four template requests are gone (F3), exactly where F2 said they would be.** `GetTemplates`, `GetTemplateImage`, `AddMacroTemplate` and `DeleteMacroTemplate` were retargeted at a macro in F2 and the last two were marked temporary the day they were written; the panel writes the zip itself now. Editing a bundle always goes read-everything → change one thing → write everything, because atomic replacement needs a finished file; so adding a template costs the same as saving the macro.

**Writing is atomic, and that lives in `Shared` next to the writer** — not in the store, because the writer is the panel (F3) and the logic is reused, not rewritten. Temp file in the same folder (`{name}.hsm.tmp`, which misses the `*.hsm` watcher filter in both long and 8.3 form), then replace. ⚠️ **The replace is `File.Replace` (`ReplaceFile`), NOT `File.Move(overwrite: true)` (`MoveFileEx`)** — measured, not read: `Move` throws `UnauthorizedAccessException` when the destination is open *even if the reader opened it with `FileShare.Delete`*; `Replace` succeeds with `FileShare.Delete` and fails without it. So `FileShare.Delete` in `MacroBundleReader` is not decoration, it is the other half of the mechanism. Renaming a macro carries `renamedFrom` so the new bundle inherits templates, submacros and the passport — without it renaming would silently drop the templates, which is exactly what the format exists to prevent. (It was `SaveMacroRequest.RenamedFrom` until F3 took the request away; the parameter stayed.)

**`*.json.incompatible` is gone.** Moving an unparseable file aside was introduced when node ids became `Guid`s and every old file stopped parsing at once. The bundle removes that failure by format: the reader distinguishes "corrupt" from "made by another format version", and moving a *future-version* bundle aside would be the exact lie the version field exists to prevent. The file stays put and the reader's verdict goes to the log in full.

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
which is what `HotkeyListener` reads — never `All`). Otherwise «the hotkey does nothing» comes back
from the other side — the D4 defect exactly. The validator is shared and the inventory is the same
one, so **the panel reaches the same verdict itself** and needs no new request to say «хоткей не
вооружён — в макросе ошибок: N» on the library row. The environment diagnostic distinguishes the two
causes, because the user's next action differs: rebind the chord, or fix the graph.

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
— the exact loss the format exists to prevent. A taken name gets a `-2` suffix, and the file stem
wins over the `Name` inside, so the copy honestly answers to its new name.

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
never writes itself (the spec's own example: `RecognizeTag` inside, `SetIcon` outside), and the
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

**Diagnostics («Проверить среду») are seven checks, six in the daemon** (elevation, templates named by nodes vs what is in each macro's bundle, display scale, hotkeys, folder writability, autostart) **and one in the panel** — the channel round-trip, because the daemon cannot honestly measure its own response time. They exist because almost every failure of this application is environmental and all of them look identical: the macro just doesn't work, and the log is silent. The elevation check is a **real `WM_NULL` send per window**, not an inference from "we are not elevated": if the game is also running unelevated everything works, and a false alarm teaches people to ignore the strip. Problems are sorted FIRST — a failure card wedged between two green pills is as good as absent.

⚠️ **The count is written out in four places** (`EnvironmentDiagnostics`, `IpcMessageTypes.RunDiagnostics`, the summary line, the button's tooltip) and when the autostart check was added, none of them were updated — every one still said "five of six", and the user-facing enumerations omitted autostart entirely, so there was no way to learn from the UI that it was checked at all. Add a check, walk all four. And note that "read-only" is not quite true: the elevation check sends `WM_NULL` and the folder check creates and deletes a probe file, because inferring writability from ACLs is exactly the deduction that lies in the interesting cases.

**`SendInput` has a slot in the model and no implementation, and the UI does not offer it.** Offering an untested input method is worse than not offering one. A value that reaches the file by hand resolves to `SendMessage` with a warning logged once per process — never silently, or the symptom would be "a setting that does nothing". Input-method wording lives once, in `Contracts/Settings/InputMethodInfo`: the default-input list and the per-profile picker both read it, because a copy in markup drifts invisibly.

Found by eye while running it, none of it visible to build or tests: `ScreenRect`'s computed `TopLeft`/`Right`/`Bottom` were being SERIALIZED into every hand-edited file (settings and macro bundles alike) — three fields that look settable and silently do nothing, now `[JsonIgnore]`; a checkbox whose two-line hint sat inside its content put the box next to the *second* line; the change counter had no home in the markup; «1 окон».

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
- **Identification** is no longer built in: it is an ordinary macro the user writes — `KeyPress(C)` → `Delay` → `RecognizeTagNode` (template set `"classes"` → `templates/classes/{tag}.png`) → `SetIconNode` → `KeyPress(C)`. Master/ignored characters are just tags in a selector (`ExcludeTags: ["Лучник", "Шаман"]`).

### The node model: `Guid` identity, `DisplayName` label

A node's `Id` is a **`Guid`** and every edge (`Next`/`Found`/`NotFound`/`Timeout`/`Matched`/`NotMatched`) and `StartNodeId` holds one. What the user sees and types is `DisplayName`, generated as family prefix + a graph-wide running number — `click-1`, `delay-2`, `find-3` — so the number doubles as insertion order. `MacroNodeNames` (Shared) owns the whole rule, including the fallback used when a hand-edited file carries no label: a blank row in the run log is worse than a generic one.

Two consequences worth knowing before touching this:

- **Renaming is not a graph operation.** It raises `PropertyChanged` and nothing else. The machinery that used to repoint every inbound edge, move the start node and re-push breakpoints on rename is **deleted, not idle** — do not reintroduce a "rename" path that walks the graph.
- **A duplicate `DisplayName` is a WARNING on both nodes**, not an error and not on one. Clicking an issue highlights a node, so blaming one of the pair picks arbitrarily; and `ValidationIssue` carries `NodeId` (to find) *and* `NodeName` (to print) because either alone is insufficient — plus, since F4, `SubmacroId`, because finding a node also means opening the right graph.

Vision nodes carry `MatchThreshold?`; `null` means "the default from settings". The inspector shows the word «из настроек» rather than a number — the default lives in the daemon's settings and printing the shipped value would name a threshold the node will not actually run with. Same rule as the targets badge: the panel must not state as fact something it cannot know.

**There is no migration and none is coming.** A pre-`Guid` file simply does not parse. The move-aside to `*.json.incompatible` that used to soften that is **gone with the bundle format** — see «Templates live inside the macro» for why moving a future-version bundle aside would be a lie. `MacroGraphJson` uses `UnsafeRelaxedJsonEscaping`, so Cyrillic inside `nodes.json` reads as «Лучник», and the bundle is store-only so the whole file greps and diffs like text.

### Win32 input model (the hard-won part — do not "simplify" without re-testing in game)

PW freezes background clients (input + rendering). Every input session is bracketed by `IGameWindow.ActivateAsync` (sends magic `WM_ACTIVATEAPP` with lParam from the window's `ProcessProfile`) and `DeactivateAsync` (drain delay, then deactivate — unless window is foreground). `AgentInputDispatcher` wraps this lifecycle around every keypress/click, one cycle per node.

- **Keyboard = SendMessage, mouse = PostMessage** (`GameWindowFactory` bakes this in). Post'd keys got dropped by the frozen pump; clicks were always reliable.
- **Modifier chords (Shift+1) DO NOT WORK** via message injection: PW reads modifiers with `GetKeyState`, which cross-thread SendMessage never updates. That's why the `pw-assist` example selects the master by *clicking* party-slot-1 instead of Shift+1. There is deliberately no chord node and no chord primitive — stage 4B deleted the callerless `PressChordAsync`/`SendChordAsync` rather than leave working-looking code that silently no-ops in game. Do not add them back.
- **WM_SETICON must be SendMessage** — Post'd icon updates sit unprocessed in frozen queues. `WindowIconService` also retries at +2s/+5s because PW's post-boot init can reset the icon.
- Both the game and this app run elevated (`requireAdministrator` in app.manifest) — UIPI blocks input/capture into elevated windows otherwise.

### Vision

`IGameWindow.FindElementAsync` (one shot) and `WaitForElementAsync` (poll until timeout) are the generic primitives; both return the CLIENT-SPACE CENTRE of the match, which is what `FoundPointVar` feeds to a later `ClickNode`. Each tick is an active capture (wake → PrintWindow with `PW_CLIENTONLY | PW_RENDERFULLCONTENT` → re-freeze) + grayscale `TM_CCOEFF_NORMED` matching. Grayscale, NOT binarized — game UI sits on semi-transparent backgrounds where binarization is unstable. `ClassMatcher` (stats-window text on solid panel) is the exception that still binarizes.

Templates come out of the running macro's own bundle — see «Templates live inside the macro (F2)» above for the layout, the per-run source, the cache and where the source PNGs went. `TemplateSetProvider` is deleted. All coordinates/templates are pixel-exact for the author's 3840×2160 screen; they live in macro nodes, not config.

**Assets ship from the daemon project.** `src/SmartMacro.Daemon/Assets/**` — after F2 that is per-tag `ClassIcons` and the tray `icon.ico`, and nothing else. Stage 3 moved them out of App because every reader of them runs in the daemon; App keeps exactly one, `Assets/icon.png`, embedded as an `AvaloniaResource` for the window icon. `ClassIcons/` and `icon.ico` stay in `daemon/Assets/`: a `SetIconNode` carries the string `Assets/ClassIcons/{tag}.png`, `WindowIconService` resolves it against `AppContext.BaseDirectory`, and moving those files would break every macro that names one.

**Tesseract is parked and no longer ships with the daemon (stage 4B).** `TesseractCoordinateReader` (HUD coordinate OCR) still works and is still exercised by `tools/VisionSampleRunner`, but the daemon does not register `ICoordinateReader` — nothing injected it, and the ctor eagerly builds a `TesseractEngine`. `SmartMacro.Core.csproj` marks the package `PrivateAssets="all" ExcludeAssets="build"`, which keeps both the managed dll and the 12 MB of `x64/`+`x86/` natives out of the daemon's output; `src/SmartMacro.Daemon/tessdata/` is gone and the language pack lives only with the sample runner, which carries its own PackageReference. Daemon output: 107 → 91 MB. Un-parking it for stuck detection = drop those two attributes and restore the DI line. ⚠️ Until then `SmartMacro.Core.dll` ships next to the daemon with a metadata reference to an assembly that is not there — harmless because the CLR resolves it lazily and nothing touches that type.

### The shipped layout (panel at the root, daemon in a subfolder)

Distribution is a **zip containing a folder**, and everything is inside it — no `%LOCALAPPDATA%`,
one path for the whole install:

```
SmartMacro/
  SmartMacro.exe      the panel, single-file — THE ONLY thing visible at the root
  macros/  settings.json  logs/  debug/
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
the exit code is `2`. The panel has no probe (no shared assembly will take it: Contracts forbids
file IO, Native is P/Invoke only, and Shared — which may touch files — is the macro domain, not a
place to hang a start-up probe) but its logger construction is wrapped in a `try` with the same
box — before that it was the one place in the panel where a failure had nowhere to go and killed the
process silently.

### Runtime state files (in the installation ROOT, gitignored)

`settings.json` (all engine knobs — see «Settings» above), `macros/*.hsm` (one bundle per macro, templates inside it) and `debug/` all live in the installation ROOT — the folder the panel sits in, one level above the daemon (see «The shipped layout»). The daemon's own `logs/smartmacro-*.log` is the exception: it stays in `daemon/`, next to the exe whose `appsettings.json` names it. `MacroGraphStore` loads on ctor → immutable snapshot → a debounced `FileSystemWatcher` raises `MacrosChanged` → subscribers (`HotkeyListener`, `MacroTemplateCache`, and the panel via the push) re-register live. Since F3 the watcher is the *only* source of change, because the daemon never writes; own-write suppression is gone with the writing. An unparseable file is skipped and logged, never fatal to the load — and shown as a row by the panel, which reads the same folder itself.

The panel writes `logs/smartmacro-ui-*.log` into the root as well, at an absolute path computed in code — in the dev tree the root IS the daemon's output folder, so both logs share one `logs/` there. The panel has no configuration file at all; an optional `appsettings.panel.json` next to the exe overrides the built-in Serilog defaults if the user drops one in. Every engine knob (`Agent`, `ProcessProfiles`, `Vision:*`) is the daemon's, and lives in `settings.json`.

`hotkeys.json` and the single `macros.json` are GONE — and so is the one-shot migrator that used to convert them. Backwards compatibility is off: a file in either legacy format is now just an unknown file the store ignores. A hotkey is a `HotkeyTrigger` inside the macro it starts.

**A fresh install starts with an empty library, and there is nothing to copy from.** Nothing seeds `macros/`, and `src/SmartMacro.Daemon/examples/` — the six `pw-*` graphs that used to ship as a handout — is deleted. After the node-id → `Guid` change they no longer loaded anyway, and a folder you have to copy files out of explains the program's internals instead of giving the user a button. The «Макросы» mode has its own empty state («create one with the button, bottom left», plus the folder path — a `.hsm` dropped in there is picked up live, templates and all), kept separate from «выберите макрос слева» because an empty list with "pick one" on it reads as a broken panel.

### Conventions

- Logging via source-generated `[LoggerMessage]` partial methods, usually split into a sibling `*.Logging.cs` partial file.
- Engine knobs live in `settings.json` next to the daemon, owned by `SettingsStore`, and are read through `ISettingsSource.Current` **at the point of use** — never cached in a field, or the knob stops being live. `IOptions<T>` and the `Config/` folder that held `AgentOptions` / `ProcessProfileOptions` / `ClassMatcherOptions` / `WindowVisionOptions` are gone. `appsettings.json` configures Serilog and nothing else. Coordinates, regions, templates, keys and timeouts belong in macro nodes, NOT in settings.
- `ScreenPoint` / `ScreenRect` record structs (in `Native`) for all pixel coordinates — bind from JSON as `{ "X": .., "Y": .. }` objects.
- Coordinate discovery workflow: user hovers cursor in-game and triggers a macro; `CursorPositionProvider` logs the client-space point it seeds the `cursor` variable with, which then goes into a node.
- "Dump captures" in the «Окна» mode header sends `DumpCaptures` (with a generous timeout — it screenshots every client) and opens the folder the daemon replies with: per-agent `debug/*-full.png` and `*-class-bin.png` for tuning vision regions.
- View-models take `IIpcClient` + `IUiDispatcher` and nothing Avalonia-shaped, so every one of them is exercised headlessly against `FakeIpcClient` (`tests/SmartMacro.Tests/Ipc/`), whose canned answers go through the real `IpcJson` round trip.
- **Comments and xmldoc are in Russian** — the whole tree was translated once, deliberately as the last step of the refactor so the next wave would not re-import English. New code follows: comments, xmldoc, `[LoggerMessage]` templates, validator and abort messages, everything a human reads. **Not** translated: identifiers, test names, and technical names inside a Russian sentence (`SendMessage`, `WM_ACTIVATEAPP`, `hwnd`, `single-flight`).
- **The gate is build + tests, and it is not enough** — even now that `tests/SmartMacro.Tests/Ui/` takes back the measurable half (see «Headless layout sweeps» below). Around two dozen defects in this codebase were invisible to build and tests and found only by running the app and looking: a focus ring clipped to nothing, glyphs rendering as colour emoji and ignoring `Foreground`, a window that silently unmapped itself, a panel that would not start at all, text with its descenders sheared off. If a change touches the UI, run it and look at it — and if you could not, say so instead of implying you did. **Both exes are `requireAdministrator`, but the manifest binds to the apphost, so `dotnet SmartMacro.Daemon.dll` / `dotnet SmartMacro.dll` from an unelevated shell runs them both** (the panel's assembly is `SmartMacro.dll`, not `SmartMacro.App.dll` — its `AssemblyName` is `SmartMacro`). Screenshot with `PrintWindow`, click with `mouse_event`; "I could not start it" is almost never true.

`docs/spec.md` (Russian) describes the system as built and is the first place to look; §14 is its decision history and the reason not to re-propose what was already rejected.
