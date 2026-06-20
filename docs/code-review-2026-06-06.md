# Code Review — 2026-06-06

After the v1 simplification (state machine stripped, mode broadcasts removed, periodic-screenshot polling now state-scoped), this is a thoughtful audit of what's there. Findings prioritised by **cost vs. impact**: things that block future work or actively confuse readers first, cosmetic stuff last.

The user explicitly asked for **responsibility creep** — classes that quietly accumulated jobs that don't belong to them. That section is first.

---

## 1. Responsibility Creep — classes doing too many jobs

These are the highest-value refactors. Each one is a class that gained a job that doesn't fit its name.

### 🔴 `CharacterAgent` — still doing input dispatch on top of agent lifecycle

After extracting `ClassIconService`, the agent shrank from ~660 to ~470 LOC. But it still owns:

- **State machine config** (3 lines)
- **Lifecycle**: Start / Stop / RunningTask, main run loop, inbox draining, IsAlive checks (~80 LOC)
- **Identification loop**: state-scoped loop, TryIdentify, screenshot+match (~50 LOC)
- **Input dispatch**: `TryFireCharacterAction`, `SendKeySafelyAsync`, `SendAssistSequenceAsync`, `SendClickSafelyAsync` (~120 LOC + 7 logger messages)
- **Identity update**: Identify / UpdateCharacter (~50 LOC)

The **input dispatch** chunk (key/chord/click → Activate→send→Deactivate) is its own concern. It knows about `InterStepDelayMs`, about `Enum.TryParse<VirtualKey>`, about activation cycles, about how to compose an assist chord. None of these are agent-lifecycle concerns.

**Proposed extraction:** `AgentInputDispatcher` (or `WindowActionRunner`) — takes `IGameWindow` + `InterStepDelayMs`, exposes:
```csharp
Task FireKeyAsync(VirtualKey key, string actionLabel);   // for Immunity etc.
Task FireAssistAsync(VirtualKey assistKey);              // Shift+1 + key
Task FireClickAsync(int x, int y, bool doubleClick);
```

Each method internally does `Activate → try { send } finally { Deactivate }`. Logs success/failure under its own logger. CharacterAgent reduces to: parse `Character.ImmunityKey` → call `_input.FireKeyAsync(key, "ImmunityKey")`. About 100 LOC moves out; CharacterAgent becomes "drain inbox, route messages, manage state".

**Priority:** Important — done before adding combat/follow loops, because those will also dispatch input and would otherwise re-bloat the agent.

---

### 🔴 `Orchestrator` — `BroadcastCursorClick` + foreground/cursor guards don't belong

The orchestrator's stated job is "route hotkey presses to broadcasts + maintain agent set". After state-machine stripping it's cleaner but `BroadcastCursorClick` and `IsTrackedAgentHandle` are ~50 LOC of Win32 cursor reading, foreground hwnd resolution, guard logic, coordinate translation — concerns about *the user's mouse and screen*, not about the agent set.

**Proposed extraction:** `CursorClickResolver` (in `Native/` or a new `Core/Input/` namespace) — exposes:
```csharp
(int X, int Y)? TryResolveClickInFocusedAgent(IReadOnlyCollection<IntPtr> agentHandles);
```
Returns null with logged reason if guards fail, returns client coords if all green. Orchestrator just calls it, broadcasts on non-null result.

Removes 4 logger messages from Orchestrator + 50 LOC. Tests become trivial (mock cursor + foreground inputs).

**Priority:** Important.

---

### 🟡 `App.axaml.cs` — tray icon + RelayCommand + window-show plumbing

143 LOC for what should be "wire DI to Avalonia". Currently:
- DI bootstrap (8 LOC, fine)
- Tray icon construction (35 LOC)
- Show/Hide window logic (20 LOC)
- Exit logic with `_exitRequested` flag (15 LOC)
- Inline `RelayCommand` class (15 LOC)

**Proposed extraction:** `TrayIconHost` service — encapsulates tray lifecycle. `App.axaml.cs` becomes ~30 LOC.

`RelayCommand` either moves to its own file in `App/Mvvm/` or gets replaced by `CommunityToolkit.Mvvm` (adds a ~50 KB dependency for one feature, probably not worth it for v1).

**Priority:** Nice-to-have — works fine, just hard to reason about as App grows.

---

### 🟡 `MainWindowViewModel` — events AND reconcile timer doing the same job

After fixing the "agent list doesn't update" bug we added a `DispatcherTimer` that reconciles against `SnapshotAgents` every 2s. The original event-based handlers (AgentStarted/Identified/Stopped) are **still there**. Belt-and-suspenders is fine but:

- Two code paths express the same intent → readers must verify both don't race or double-update
- If we ever debug "row appeared twice" it's now 2 places to check
- `FindRow + null check` dedupe is the safety net for both paths

**Proposed:** Keep only the reconcile timer; remove event handlers. Cost: up to 2s lag on agent appear/identify (already that bad in worst case). Win: one code path, smaller class.

**Counter-argument:** Events give snappier UI updates in the common case. Keep both.

**Priority:** Low — decide once the live UX is established. Documenting the duplication so future-us doesn't try to "fix" only one path.

---

### 🟢 `KeyBindingPicker` — boundary of responsibility is OK but grew

300+ LOC control that handles:
- Avalonia Button style override
- Keyboard capture
- Mouse capture (XButton1/2 + Middle)
- Modifier tracking
- Display formatting ("Ctrl+Shift+F1", "Mouse4")

The capture vs. display formatting could split:
- Capture logic stays in the control
- `KeyComboFormatter` static helper for `FormatCombo(modifiers, key/mouse)`

Helps slightly with reusability if another UI surface wants to display combos. Not pressing.

**Priority:** Low.

---

## 2. Dead Code (verified by Explore agent)

| Item | Verdict | Action |
|---|---|---|
| `Core/LLM/` (ILanguageModel, OllamaModel, ClaudeModel) | DEAD | Delete entire folder. No call sites; `AnalyzeAsync` referenced nowhere. `AgentOptions.OllamaModel` field is leftover too. |
| `Core/Vision/CoordinateReader.cs` | DEAD | Stub class with `throw new NotImplementedException`. The real one is `TesseractCoordinateReader`. Delete. |
| `Core/Vision/StuckDetector.cs` | DEAD | Never instantiated; only made sense with the Stuck state we removed. Delete. |
| `Native/Keyboard/SendMessageKeyboardInput.cs` | WIRED-NOT-USED | Implements `IKeyboardInput`, never instantiated. `GameWindowFactory` only news `PostMessageKeyboardInput`. Delete OR add comment explaining alternative-strategy intent. |
| `Native/Keyboard/SendInputKeyboardInput.cs` | WIRED-NOT-USED | Same as above. |
| `Native/Mouse/SendMessageMouseInput.cs` | WIRED-NOT-USED | Same. |
| `Native/Mouse/SendInputMouseInput.cs` | WIRED-NOT-USED | Same. |
| `Native/SendInputOptions.cs` | WIRED-NOT-USED | Configured in DI but only consumed by `SendInputKeyboardInput`. If we delete that, delete this too. |
| `InternalsVisibleTo("PerfectWorldAgent.Tests")` in csproj | DEAD | No Tests project exists. Delete declarations from `Core.csproj` and `Native.csproj`. |
| `Win32HotkeyMonitor.RaiseHotkeyPressed`, `Win32MouseHookMonitor` analogue, `HotkeyListener.RaiseHotkeyPressed`, `ProcessMonitor.RaiseProcessAppeared/Disappeared` | TEST-ONLY HOOKS | Tied to deleted Tests project. Either spin up `PerfectWorldAgent.Tests` (the whole hotkey/process layer needs tests anyway) or delete the hooks. Currently they're harmless internal methods on a public surface. |
| `Coordinates.cs`, `CoordinateReaderOptions.cs`, `ICoordinateReader.cs`, `TesseractCoordinateReader.cs` | WIRED-LIVE-BUT-UNCONSUMED | Reader is in DI but nothing calls `Read` now that `StatusUpdateMessage`/`StuckDetectedMessage` are gone. The reader is a working Tesseract integration we'd want to keep for future combat/stuck logic. **Recommendation:** Keep, document that it's parked. |

**Bulk delete impact:** ~10 files removed. No behavior change.

**Priority:** Important — accumulates as "what is this for?" cost on every future review.

---

## 3. Cross-cutting smells

### Logger message proliferation

`CharacterAgent` has 22 `[LoggerMessage]` partial-method declarations. `Orchestrator` has ~17. `Win32MouseHookMonitor`, `HotkeyListener`, `HotkeyConfigStore` each have 5–10. They're free at runtime (source-generated) but they cost ~25% of file LOC and bury the actual logic.

**Pattern to consider:** Move logger declarations to a sibling `<ClassName>Logging.cs` partial class file. Same compile output, cleaner main file.

**Priority:** Nice-to-have.

### String-typed VirtualKey storage on `Character`

`BurstBuffKey`, `DamageKey`, `ImmunityKey`, `AssistKey` are all `string` ("F1"). Every consumer does `Enum.TryParse<VirtualKey>(...)`. Empty string = "not set".

Options:
- Use `VirtualKey?` (nullable enum) — stronger types, but JSON converter needed for null handling
- Add a `KeyBinding` value type that wraps `string` + parsing
- Leave as-is (works, just verbose at every call site)

**Priority:** Low — refactor when adding more keys (combat rotation will pile on).

### TOCTOU in icon loading

`ClassIconService.TryApply` and `WindowIconCache.GetOrLoad` both `File.Exists` then later read. Race is harmless (user owns icon files; if they delete one mid-flight, we just fail), but the check + later read is two syscalls. Simpler: try the load, catch `FileNotFoundException`.

**Priority:** Very low.

### `MouseButton` enum has irregular values

```csharp
None = 0, XButton1 = 1, XButton2 = 2, Middle = 16
```

Middle's `16` is because we added it later as a non-conflicting bit (XButton values come from `MSLLHOOKSTRUCT.mouseData` high word). JSON serialises by name (`JsonStringEnumConverter`), so values are not on-disk contract. Cosmetic.

**Priority:** Very low.

### `CharacterAgent` re-implements `IsIdentified` semantics

```csharp
public bool IsIdentified => _machine.State != AgentState.AwaitingIdentification;
```

With only 2 states this works but couples behavior to enum. After future state additions ("agent in combat" is also identified), readers must remember to update this. Consider: tag identified states explicitly or just check `_machine.State == AgentState.AwaitingIdentification` directly at the one or two call sites.

**Priority:** Low.

---

## 4. Architectural observations

### DI registration is split across files

`Program.cs` (App) registers ~15 services. `GameWindowFactory` does manual `new PostMessageKeyboardInput()`. `CharacterAgentFactory.CreateAsync` does manual `new CharacterAgent(window, ...)`. Mixing DI and `new` is fine, but the seam between "DI-managed" and "per-agent" objects could be more explicit. Currently it's:

- **Singletons in DI:** orchestrator, provider, roster, matcher, factories, monitors, ClassIconService, hosted services
- **Per-agent (factory-newed):** GameWindow, CharacterAgent, internal IKeyboardInput, IMouseInput

This is correct (per-agent state shouldn't live in singleton DI) but undocumented. Worth a 10-line README in `Core/` explaining the lifetimes.

### Orchestrator events vs. message channel — dual upstream pathway

Agent → Orchestrator goes through both:
- Channel `_inbox` (for `AgentIdentifiedMessage`, `AgentStoppingMessage`)
- C# events on `Orchestrator` (`AgentStarted`, `AgentIdentified`, `AgentStopped`)

The orchestrator drains the inbox, then fires its events from the dispatch loop. So the events ARE driven by the channel — they're a notification layer for UI subscribers. Fine, but the duplication of "agent identified" in two places (`AgentIdentifiedMessage` and `AgentIdentified` event) is a bit much for two messages.

**Could simplify:** Drop the AgentMessage layer for lifecycle events. Agents call orchestrator methods directly (`_orchestrator.NotifyIdentified(this)`). Saves an enum branch + a message type. But then agents have a hard dependency on `Orchestrator` (currently they only know `ChannelWriter<AgentMessage>`).

**Priority:** Low — current design is consistent ("everything from agent goes through channel"); just verbose.

### Two parallel options classes for input timing

- `Native/ActivatingInputOptions` — SettleDelayMs, DeactivationDelayMs, InterStepDelayMs, ActivationLParam
- `Native/SendInputOptions` — FocusSettleDelayMs, KeyHoldDurationMs, InterClickDelayMs, RestoreOriginalFocus

Two configs in adjacent enums of files. Second is dead (only consumed by unused `SendInput*Input` classes). Delete or merge.

**Priority:** Bundled with dead-code section.

---

## 5. Suggested refactor priority order

If we want to refactor before adding the next feature (combat mode), in this order:

1. **Delete dead code** (Section 2, ~10 files). 30 minutes, zero risk. Big readability win.
2. **Extract `AgentInputDispatcher`** from `CharacterAgent` (Section 1, item 1). 1.5 hours. Required before combat — combat loop will dispatch input identical to current immunity flow, would otherwise re-bloat the agent.
3. **Extract `CursorClickResolver`** from `Orchestrator` (Section 1, item 2). 1 hour. Easier to add a third broadcast hotkey later (e.g., right-click).
4. **Move logger declarations to sibling files** for `CharacterAgent` and `Orchestrator`. 30 minutes. Mostly cosmetic but makes the next round of additions less cluttered.
5. **`TrayIconHost` extraction** from `App.axaml.cs`. 45 minutes. Low impact but unblocks adding more tray-menu actions (e.g., "Pause all", "Stop").

**Total: ~4 hours** of focused refactoring. After this the codebase is in clean shape to add combat mode.

If we want to ship combat first and refactor later — fine, but expect `CharacterAgent` to grow another 100-150 LOC for the combat loop, and the input dispatcher extraction becomes harder once intertwined.

---

## 6. What's actively good

To balance the criticism — the parts I'd not touch:

- **`PostMessageKeyboardInput` and friends** — clean primitives. The journey from sync-deactivation to PostMessage-deactivation to `Activate/Deactivate` on `IGameWindow` ended up with a well-shaped API.
- **`HotkeyListener` two-monitor coordination** — keyboard + mouse hooks behind one façade, suspend/resume during Settings dialog, runtime re-registration on config change. Complex problem, clean solution.
- **`HotkeyConfigStore` graceful skip** — the two-stage parse (`RawHotkeyBinding` → `HotkeyBinding`) is more code than a single deserialize, but it survives schema evolution. Done right.
- **State machine + per-state loop** pattern (OnEntry/OnExit drives StartIdentificationLoop). Symmetric, extensible. Adding Combat will be a one-screen change.
- **`ProcessMonitor` two-set tracking** (`_knownPids` vs `_pidsLoggedWaiting`). After we found the hwnd-not-ready bug, the fix was minimal and the data structure makes the intent obvious.
- **`Win32NativeWindow` API** — `Activate/Deactivate/SetIconFromFile/CapturePng/SendActivationSignal` all behind a clean interface; implementation is a single file.

---

## 7. Namespace responsibility creep

Same pattern as Section 1 but at the folder/namespace level. A namespace name promises a single concept; over time it accumulates types that don't fit. Cleaning these up costs little (move files, fix `namespace` line + usings) and pays off every time someone looks for where a type lives.

### 🔴 `PerfectWorldAgent.Agents` — became a catch-all

Currently houses **5 distinct concerns**:

| Type | Actual responsibility |
|---|---|
| `CharacterAgent`, `CharacterAgentFactory`, `ICharacterAgentFactory` | Agent lifecycle |
| `AgentState`, `AgentTrigger`, `AgentMessage` | Agent's state machine + messaging contract |
| `CharacterProvider`, `ICharacterProvider` | Identification facade (template match + roster lookup) |
| `ICharacterRoster`, `JsonCharacterRoster` | Persistence (JSON file + template PNGs) |
| `ClassIconService` | Window presentation (taskbar icon per class) |

Last one is on me — I parked `ClassIconService` here in the last refactor without thinking. It's a *window decoration* service, not an agent concern. The fact that `CharacterAgent` calls it doesn't make it an "agent" type any more than `Orchestrator` calling `Win32NativeWindowSystem` makes the window system part of orchestration.

**Proposed split:**

```
Agents/                — CharacterAgent, Factory, State, Trigger, Message  (lifecycle + protocol)
Identification/        — CharacterProvider, ICharacterProvider             (recognition logic)
Persistence/           — ICharacterRoster, JsonCharacterRoster             (storage)
Presentation/ (or Windows/) — ClassIconService                             (visual concerns at window level)
```

After this, `Agents/` is **just** about agent runtime. A reader can answer "where does identification happen?" → `Identification/`. "Where is roster saved?" → `Persistence/`. Currently all three answers are "`Agents/`, dig deeper".

**Priority:** Important — gets harder to undo as more types accumulate.

---

### 🔴 `PerfectWorldAgent.Orchestration` — mixed orchestrator + hotkey infra + process monitor

Currently:

| Type | Actual responsibility |
|---|---|
| `Orchestrator`, `OrchestratorTrigger` | The dispatcher |
| `HotkeyOptions`, `HotkeyConfigStore`, `HotkeyListener` | Hotkey configuration + lifecycle |
| `ProcessMonitor`, `ProcessInfo` | OS process detection |

These three groups don't share concerns. Hotkey infrastructure stands alone — it doesn't know what an orchestrator is, it just emits `OrchestratorTrigger` events (and even that coupling is just an enum name). Process monitoring is a pure OS-level service that the orchestrator happens to consume.

**Proposed split:**

```
Orchestration/         — Orchestrator, OrchestratorTrigger
Hotkeys/               — HotkeyOptions, HotkeyConfigStore, HotkeyListener
ProcessMonitoring/     — ProcessMonitor, ProcessInfo
```

The fact that the orchestrator subscribes to a `HotkeyListener` doesn't put the listener in `Orchestration/` any more than subscribing to `INotifyPropertyChanged` would put it in WPF's namespace.

**Bonus:** `Hotkeys/` makes it obvious where to look when adding a new hotkey trigger. Today the answer is "spread across `Orchestration/`, `Native/Hotkey/`, and ViewModels".

**Priority:** Important.

---

### 🟡 `PerfectWorldAgent.App.ViewModels` — VMs + base class + converters

Currently:

| Type | Actual responsibility |
|---|---|
| `MainWindowViewModel`, `LabelAgentDialogViewModel`, `SettingsDialogViewModel`, `AgentRowViewModel` | Actual view-models |
| `ObservableObject` | MVVM infrastructure base class |
| `IdentifiedConverters` (4 `IValueConverter` types) | XAML value converters for binding |

VMs and converters are both bound from XAML, both produce display-side mappings — but they have different shapes (VMs are stateful, converters are stateless singletons referenced via `x:Static`). A reader looking for "where are the converters" sees `ViewModels/` and assumes "VMs only".

**Proposed split:**

```
ViewModels/    — only VMs
Mvvm/          — ObservableObject (and future RelayCommand if extracted from App.axaml.cs)
Converters/    — IdentifiedConverters split into one file per converter (or keep grouped)
```

**Priority:** Nice-to-have. Less harmful than the previous two because `ViewModels/` only has 6 files total.

---

### 🟡 `PerfectWorldAgent.Core.Core` — double-name is awkward

`Core/Core/` contains `GameWindow`, `IGameWindow`, `GameWindowFactory`, `IGameWindowFactory`. The folder name is meaningless ("the core of Core?"). The actual concept is **game window abstraction**.

**Proposed rename:** `Core/GameWindows/` or just `Core/Windows/`. `PerfectWorldAgent.Core.GameWindows.GameWindow` reads more naturally than `PerfectWorldAgent.Core.Core.GameWindow`.

**Priority:** Low — purely cosmetic, but every time someone reads the namespace they parse `Core.Core` as a typo.

---

### 🟢 Other namespaces are clean

For balance:

- `PerfectWorldAgent.Native` (root + `Hotkey/`, `Keyboard/`, `Mouse/`, `Window/`, `Internal/`) — each subnamespace owns one Win32 concern. Clean.
- `PerfectWorldAgent.Vision` — image processing. NameMatcher + (parked) coordinate reader both fit. Clean.
- `PerfectWorldAgent.Models` — value types / DTOs. `Character`, `CharacterClass`, `Coordinates`. Tiny and consistent.
- `PerfectWorldAgent.Config` — config DTOs. One file (`AgentOptions`). Fine.

---

### Suggested namespace refactor order

If we want to clean namespaces as part of the Section 5 refactor pass:

1. **Move `ClassIconService` out of `Agents/`** — 5 minutes, just done wrong on my part. Goes to new `Core/Presentation/` or merges with whatever house we pick for window-level visual concerns.
2. **Split `Orchestration/` into `Hotkeys/` + `ProcessMonitoring/`** — 20 minutes, mechanical. Big readability win for "where do hotkeys live".
3. **Extract `Identification/` and `Persistence/` from `Agents/`** — 30 minutes. Establishes the SRP boundary so future identification logic (e.g., the AssistKey lookup later) has an obvious home.
4. **Rename `Core/Core/` → `Core/GameWindows/`** — 10 minutes find/replace.
5. **Carve `Converters/` and `Mvvm/` out of `App/ViewModels/`** — 15 minutes. Smallest win.

**Total: ~1.5 hours** of pure namespace work, no behavioral change, no risk.

Could be done in the same session as Section 5's class extractions — they don't conflict.

---

## 8. Open questions for the user

1. Tests project — spin it up now (would justify keeping the `RaiseHotkeyPressed` etc. hooks) or delete the hooks + `InternalsVisibleTo`?
2. `LLM/` folder — fully delete (no plan to integrate Ollama/Claude for inference), or keep behind a `// TODO: future` comment?
3. Reconcile-timer vs events in `MainWindowViewModel` — pick one, or keep belt-and-suspenders?
4. Order of priorities in Section 5 + Section 7 acceptable, or want combat shipped first?
5. Namespace split for `Agents/` — agree with the 4-way split (Agents/Identification/Persistence/Presentation) or prefer to keep flatter?
