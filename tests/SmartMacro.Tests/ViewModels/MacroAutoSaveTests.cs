using FakeItEasy;
using SmartMacro.App.Mvvm;
using SmartMacro.App.Services;
using SmartMacro.App.ViewModels;
using SmartMacro.App.ViewModels.Nodes;
using SmartMacro.Macros.Model;
using SmartMacro.Native;
using SmartMacro.Tests.Ipc;

namespace SmartMacro.Tests.ViewModels;

/// <summary>
/// Автосохранение: панель дописывает макрос сама, состояния «черновик» нет.
///
/// <b>Ни одна проверка здесь не ждёт настоящих секунд.</b> Часы автосохранения живут в виде
/// (<c>DispatcherTimer</c> — тип Avalonia), а view-model выставляет наружу
/// <see cref="MacroEditorViewModel.TickAutoSave"/>, который тест дёргает напрямую — тот же приём,
/// что у <c>TickElapsed</c> в полосе отладчика. Проверялось бы это <c>Task.Delay</c>, тесты плавали
/// бы, и первым же плавающим стал бы тот, что важнее прочих.
///
/// <b>Число записей считается по <c>MacroLibrary.Changed</c></b>: библиотека поднимает его после
/// каждой перезагрузки снимка, а перезагружает она его после каждой записи. «Сколько раз мы
/// разбудили демона» — это ровно то, чем автосохранение платит, и мерить надо именно это.
/// </summary>
public class MacroAutoSaveTests
{
    /// <summary>Панель над настоящей папкой макросов плюс счётчик записей.</summary>
    private sealed class Panel : IDisposable
    {
        public Panel()
        {
            Library.Library.Changed += () => Writes++;
        }

        public TempLibrary Library { get; } = new();

        public FakeIpcClient Client { get; } = new();

        /// <summary>Сколько раз папка перечитывалась — то есть сколько раз мы записали файл.</summary>
        public int Writes { get; private set; }

        public MacroGraph? Find(string name) => Library.Library.TryGet(name)?.Graph;

        public int Count => Library.Library.Entries.Count;

        public void Dispose() => Library.Dispose();
    }

    private static MacroEditorViewModel CreateEditor(Panel panel, IMacroNameConflictPrompt? conflicts = null) =>
        new(panel.Client, panel.Library.Library, null, null, ImmediateUiDispatcher.Instance, conflicts);

    /// <summary>Прогоняет часы столько раз, сколько сказано.</summary>
    private static void Tick(MacroEditorViewModel vm, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            vm.TickAutoSave();
        }
    }

    /// <summary>
    /// Столько тиков, сколько нужно для записи после последней правки: тихие тики ПЛЮС тот, на
    /// котором правка замечена. Правка происходит между тиками, так что первый тик после неё
    /// видит изменившееся содержимое и начинает отсчёт заново, а не засчитывается в него.
    /// </summary>
    private static void TickUntilQuiet(MacroEditorViewModel vm) =>
        Tick(vm, MacroEditorViewModel.AutoSaveQuietTicks + 1);

    private static MacroGraph Chain(string name = "цепочка") => new()
    {
        Name = name,
        StartNodeId = Ids.Of("a"),
        Nodes =
        [
            new DelayNode { Id = Ids.Of("a"), DisplayName = "a", Ms = 100, Next = Ids.Of("b") },
            new DelayNode { Id = Ids.Of("b"), DisplayName = "b", Ms = 200 },
        ],
    };

    /// <summary>Открывает существующий макрос — то состояние, из которого начинается правка.</summary>
    private static MacroEditorViewModel Open(Panel panel, MacroGraph graph, IMacroNameConflictPrompt? conflicts = null)
    {
        panel.Library.WriteExternally(graph);
        var vm = CreateEditor(panel, conflicts);
        vm.SelectedMacro = vm.Macros.Single(row => row.Name == graph.Name);
        return vm;
    }

    private static void Edit(MacroEditorViewModel vm, string seconds) =>
        ((DelayNodeRowViewModel)vm.Nodes[0]).SecondsText = seconds;

    // ---- по затиханию, а не по таймеру ---------------------------------------------------

    // Правка записывается не «через N секунд после правки вообще», а через N секунд ПОСЛЕ
    // ПОСЛЕДНЕЙ. Разница не в экономии: каждая запись будит демона — он перечитывает папку,
    // ПЕРЕРЕГИСТРИРУЕТ ВСЕ ХОТКЕИ и целиком сбрасывает кэш шаблонов, — и делает это, возможно,
    // посреди прогона по живым клиентам.
    [Test]
    public async Task NothingIsWrittenUntilTheGraphHasBeenQuietLongEnough()
    {
        using var panel = new Panel();
        using var vm = Open(panel, Chain("тихий"));
        var before = panel.Writes;

        Edit(vm, "9");
        // На один тик меньше, чем нужно: первый видит изменившееся содержимое и начинает отсчёт,
        // дальше идут тихие.
        Tick(vm, MacroEditorViewModel.AutoSaveQuietTicks);

        await Assert.That(panel.Writes).IsEqualTo(before);
        await Assert.That(vm.IsDirty()).IsTrue();
        await Assert.That(vm.SaveStateText).IsEqualTo("правки ещё не записаны");

        Tick(vm);

        await Assert.That(panel.Writes).IsEqualTo(before + 1);
        await Assert.That(vm.IsDirty()).IsFalse();
        await Assert.That(vm.SaveStateText).IsEqualTo("сохранено");
        await Assert.That(((DelayNode)panel.Find("тихий")!.Nodes[0]).Ms).IsEqualTo(9000);
    }

    // Приступ правки любой длины стоит РОВНО ОДНОЙ записи: каждая новая правка начинает отсчёт
    // заново. По таймеру здесь было бы по пробуждению демона на каждые N секунд набора текста.
    [Test]
    public async Task ABurstOfEditsCostsExactlyOneWrite()
    {
        using var panel = new Panel();
        using var vm = Open(panel, Chain("очередь"));
        var before = panel.Writes;

        foreach (var value in new[] { "1", "2", "3", "4", "5" })
        {
            Edit(vm, value);
            // Правим быстрее, чем истекает затишье, — так выглядит обычный набор.
            Tick(vm, MacroEditorViewModel.AutoSaveQuietTicks - 1);
            await Assert.That(panel.Writes).IsEqualTo(before);
        }

        TickUntilQuiet(vm);

        await Assert.That(panel.Writes).IsEqualTo(before + 1);
        await Assert.That(((DelayNode)panel.Find("очередь")!.Nodes[0]).Ms).IsEqualTo(5000);
    }

    // ⚠️ ПОЛОВИНА ДОВОДА ПРО ГОНКУ «Сохранить → Запустить». Обработчик RunMacro у демона
    // перечитывает папку, если бандл записан меньше двух секунд назад (см.
    // RunMacro_AMacroUntouchedForAges_DoesNotRereadTheFolder). Превратись автосохранение в
    // «пишем на каждый тик», каждое нажатие ▸ стоило бы полного чтения библиотеки с разбором
    // каждого бандла. Не превращается: без правок часы не пишут вовсе.
    [Test]
    public async Task AnIdleEditorNeverWrites_NoMatterHowLongItSits()
    {
        using var panel = new Panel();
        using var vm = Open(panel, Chain("праздный"));
        var before = panel.Writes;

        Tick(vm, MacroEditorViewModel.AutoSaveQuietTicks * 20);

        await Assert.That(panel.Writes).IsEqualTo(before);
        await Assert.That(vm.SaveStateText).IsEqualTo("сохранено");
    }

    // Пустой редактор часы тоже переживают: тик до открытия макроса не имеет права ни писать, ни
    // падать.
    [Test]
    public async Task TickingWithNothingOpenDoesNothing()
    {
        using var panel = new Panel();
        using var vm = CreateEditor(panel);

        Tick(vm, MacroEditorViewModel.AutoSaveQuietTicks * 3);

        await Assert.That(panel.Count).IsEqualTo(0);
        await Assert.That(vm.SaveStateText).IsNull();
    }

    // ---- пишем даже с ошибками валидации ---------------------------------------------------

    // Граф невалиден ровно тогда, когда над ним работают: отказ записи и автосохранение
    // несовместимы. Предохранитель — на стороне исполнителя: демон не вооружает такой макрос, а
    // строка библиотеки несёт красный «!».
    [Test]
    public async Task ABrokenGraphIsWrittenAnyway_AndTheLibraryRowGoesRed()
    {
        using var panel = new Panel();
        using var vm = Open(panel, new MacroGraph
        {
            Name = "ломаемый",
            StartNodeId = Ids.Of("find"),
            Nodes = [new FindElementNode { Id = Ids.Of("find"), DisplayName = "find", Template = "Кнопка" }],
        });

        // Пока триггеров нет, макрос библиотечный и условная нода законна. Повесили хоткей — и
        // достижимая условная нода стала ОШИБКОЙ: контекстного окна ей взять неоткуда. Поля при
        // этом заполнены все: недонабранное поле — беда другого рода, см. соседний тест.
        vm.AddTrigger(MacroTriggerKind.Hotkey);
        ((HotkeyTriggerRowViewModel)vm.Triggers[0]).KeyName = nameof(VirtualKey.F13);
        TickUntilQuiet(vm);

        await Assert.That(vm.IsDirty()).IsFalse();
        await Assert.That(panel.Find("ломаемый")!.Triggers).IsNotEmpty();
        // Замечания видны, не дожидаясь нажатия «Сохранить»: раз запись их больше не блокирует,
        // панель проблем — единственное, что про них говорит внутри редактора.
        await Assert.That(vm.Issues.Any(issue => issue.IsError)).IsTrue();
        await Assert.That(vm.Macros.Single(row => row.Name == "ломаемый").ErrorCount).IsGreaterThan(0);
    }

    // Обратная сторона: НЕДОНАБРАННОЕ ПОЛЕ автосохранение останавливает, и это не
    // непоследовательность. Ошибка валидации — про граф, который получился; «две секунды» в поле
    // задержки — про граф, которого не получилось: BuildGraph подставил бы туда что-нибудь, о чём
    // никто не просил, и файл разошёлся бы с экраном МОЛЧА, без единого замечания.
    [Test]
    public async Task AHalfTypedFieldHoldsTheWriteBack_AndSaysWhy()
    {
        using var panel = new Panel();
        using var vm = Open(panel, Chain("недобор"));
        var before = panel.Writes;

        Edit(vm, "две секунды");
        TickUntilQuiet(vm);

        await Assert.That(panel.Writes).IsEqualTo(before);
        await Assert.That(vm.SaveStateText).Contains("поправьте поля");
        await Assert.That(vm.Issues.Any(issue => issue.IsError)).IsTrue();

        // Поправили — записалось само, без единого нажатия.
        Edit(vm, "7");
        TickUntilQuiet(vm);

        await Assert.That(panel.Writes).IsEqualTo(before + 1);
        await Assert.That(((DelayNode)panel.Find("недобор")!.Nodes[0]).Ms).IsEqualTo(7000);
    }

    // ---- никогда не переименовывает и никогда не спрашивает --------------------------------

    // Автосохранение пишет в ЗАГРУЖЕННОЕ имя. Подхватывай оно набираемое — «pw-l», «pw-lo» и
    // «pw-log» лежали бы в папке рядом с «pw-login».
    [Test]
    public async Task TypingANewNameDoesNotBreedFiles()
    {
        using var panel = new Panel();
        var prompt = A.Fake<IMacroNameConflictPrompt>();
        using var vm = Open(panel, Chain("старое"), prompt);
        var before = panel.Writes;

        foreach (var typed in new[] { "н", "но", "нов", "новое" })
        {
            vm.MacroName = typed;
            TickUntilQuiet(vm);
        }

        await Assert.That(panel.Count).IsEqualTo(1);
        // ⚠️ Найдено на живой панели: и ЗАПИСИ тоже не должно быть ни одной. Набранное имя
        // попадало в слепок бандла, значит читалось как правка, — и приступ переименования стоил
        // записи файла с ровно тем же содержимым, то есть лишнего пробуждения демона.
        await Assert.That(panel.Writes).IsEqualTo(before);
        await Assert.That(panel.Find("старое")).IsNotNull();
        // Имя ВНУТРИ файла тоже осталось прежним: иначе читатель встречал бы расхождение «стем
        // файла против поля Name» и писал бы об этом в журнал на каждой загрузке.
        await Assert.That(panel.Find("старое")!.Name).IsEqualTo("старое");
        // Модального вопроса раз в несколько секунд не было и быть не могло.
        A.CallTo(() => prompt.AskAsync(A<MacroNameConflict>._)).MustNotHaveHappened();
    }

    // Незавершённое переименование обязано быть ВИДНО: иначе «я переименовал, а файл не
    // переименовался» читается как поломка.
    [Test]
    public async Task APendingRenameIsAnnouncedInTheToolbar()
    {
        using var panel = new Panel();
        using var vm = Open(panel, Chain("старое"));

        vm.MacroName = "новое";

        await Assert.That(vm.IsRenamePending).IsTrue();
        await Assert.That(vm.SaveStateText).Contains("переименовать");
        await Assert.That(vm.SaveStateText).Contains("новое");
    }

    // Enter (и уход фокуса, и «Сохранить») — три равнозначных способа сказать «имя набрано
    // целиком». Дальше это обычное переименование: записать новый файл, удалить старый.
    [Test]
    public async Task CommitRename_RenamesTheFile_AndTheHintGoesAway()
    {
        using var panel = new Panel();
        using var vm = Open(panel, Chain("старое"));

        vm.MacroName = "новое";
        var renamed = await vm.CommitRenameAsync();

        await Assert.That(renamed).IsTrue();
        await Assert.That(panel.Find("новое")).IsNotNull();
        await Assert.That(panel.Find("старое")).IsNull();
        await Assert.That(vm.IsRenamePending).IsFalse();
        await Assert.That(vm.SaveStateText).IsEqualTo("сохранено");
    }

    // Esc возвращает то, как файл называется на самом деле.
    [Test]
    public async Task CancelRename_PutsTheRealNameBack()
    {
        using var panel = new Panel();
        using var vm = Open(panel, Chain("старое"));

        vm.MacroName = "передумал";
        vm.CancelRename();

        await Assert.That(vm.MacroName).IsEqualTo("старое");
        await Assert.That(vm.IsRenamePending).IsFalse();
    }

    // Уход фокуса не имеет права спрашивать про одно и то же занятое имя снова и снова: щёлкнул
    // мимо поля — модальное окно, щёлкнул ещё раз — оно же. Enter при этом спрашивает всегда: это
    // прямое действие, а не побочный эффект щелчка.
    [Test]
    public async Task LosingFocusAsksAboutATakenNameOnlyOnce_ButEnterAsksAgain()
    {
        using var panel = new Panel();
        panel.Library.WriteExternally(Chain("занято"));
        var asked = 0;
        var prompt = A.Fake<IMacroNameConflictPrompt>();
        A.CallTo(() => prompt.AskAsync(A<MacroNameConflict>._))
            .ReturnsLazily(() =>
            {
                asked++;
                return Task.FromResult(MacroNameConflictChoice.Cancel);
            });

        using var vm = Open(panel, Chain("своё"), prompt);
        vm.MacroName = "занято";

        await vm.CommitRenameAsync();
        await vm.CommitRenameAsync();
        await Assert.That(asked).IsEqualTo(1);

        await vm.CommitRenameAsync(force: true);
        await Assert.That(asked).IsEqualTo(2);
    }

    // ⚠️ ЛОВУШКА: модальный диалог крутит СВОЙ цикл диспетчера, то есть часы автосохранения во
    // время него тикают. Записать посреди открытого вопроса значило бы вести две записи разом.
    [Test]
    public async Task NothingIsWrittenWhileTheNameConflictDialogIsOpen()
    {
        using var panel = new Panel();
        panel.Library.WriteExternally(Chain("занято"));

        MacroEditorViewModel? editor = null;
        var writesDuringTheDialog = -1;
        var prompt = A.Fake<IMacroNameConflictPrompt>();
        A.CallTo(() => prompt.AskAsync(A<MacroNameConflict>._))
            .ReturnsLazily(() =>
            {
                // Пока человек читает вопрос, часы идут.
                var before = panel.Writes;
                Tick(editor!, MacroEditorViewModel.AutoSaveQuietTicks * 3);
                writesDuringTheDialog = panel.Writes - before;
                return Task.FromResult(MacroNameConflictChoice.Cancel);
            });

        editor = Open(panel, Chain("своё"), prompt);
        using var vm = editor;
        Edit(vm, "9");
        vm.MacroName = "занято";
        await vm.CommitRenameAsync();

        await Assert.That(writesDuringTheDialog).IsEqualTo(0);
    }

    // ---- своя запись не читается как чужая --------------------------------------------------

    // ⚠️ Подавления собственных записей у панели НЕТ намеренно: редактор сравнивает СОДЕРЖИМОЕ
    // открытого графа с диском, что сильнее любой отметки времени. С частыми записями это
    // сравнение обязано выдерживать и эхо наблюдателя — иначе «изменён на диске» мигало бы каждые
    // несколько секунд правки.
    [Test]
    public async Task ItsOwnWritesAreNeverMistakenForSomebodyElsesEdit()
    {
        using var panel = new Panel();
        using var vm = Open(panel, Chain("своё"));

        for (var i = 1; i <= 5; i++)
        {
            Edit(vm, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            TickUntilQuiet(vm);
            // Эхо наблюдателя: та же папка, перечитанная спустя гашение дребезга.
            panel.Library.Library.Refresh();

            await Assert.That(vm.ChangedOnDisk).IsFalse();
            await Assert.That(vm.SaveStateText).IsEqualTo("сохранено");
        }
    }

    // Расхождение с диском автосохранение ОСТАНАВЛИВАЕТ: затереть чужую правку через несколько
    // секунд, ничего не спросив, значило бы самому стать той бедой, ради которой этот флаг заведён.
    // Сторону выбирает человек — «Перечитать» либо «Сохранить».
    [Test]
    public async Task AFileChangedOnDiskStopsAutoSave_AndTheForeignEditSurvives()
    {
        using var panel = new Panel();
        using var vm = Open(panel, Chain("спорный"));

        Edit(vm, "9");
        panel.Library.WriteExternally(new MacroGraph
        {
            Name = "спорный",
            StartNodeId = Ids.Of("чужая"),
            Nodes = [new DelayNode { Id = Ids.Of("чужая"), DisplayName = "чужая", Ms = 5000 }],
        });

        await Assert.That(vm.ChangedOnDisk).IsTrue();
        var writes = panel.Writes;
        Tick(vm, MacroEditorViewModel.AutoSaveQuietTicks * 3);

        await Assert.That(panel.Writes).IsEqualTo(writes);
        await Assert.That(panel.Find("спорный")!.Nodes).Count().IsEqualTo(1);
        // Наша работа при этом на месте — её просто ещё не записали.
        await Assert.That(vm.Nodes).Count().IsEqualTo(2);

        // «Сохранить» — это и есть «моя сторона», и после него автосохранение оживает.
        await vm.SaveAsync();
        await Assert.That(vm.ChangedOnDisk).IsFalse();
        await Assert.That(panel.Find("спорный")!.Nodes).Count().IsEqualTo(2);
    }

    // ---- дописать, уходя --------------------------------------------------------------------

    // Переключение на другой макрос попадает в секунды затишья ровно тогда, когда человек
    // «поправил и пошёл дальше». Без дописывания это стоило бы правки — то есть ровно того, от
    // чего автосохранение и заводилось.
    [Test]
    public async Task SwitchingMacrosWritesTheOneWeAreLeaving()
    {
        using var panel = new Panel();
        panel.Library.WriteExternally(Chain("второй"));
        using var vm = Open(panel, Chain("первый"));

        Edit(vm, "9");
        vm.SelectedMacro = vm.Macros.Single(row => row.Name == "второй");

        await Assert.That(((DelayNode)panel.Find("первый")!.Nodes[0]).Ms).IsEqualTo(9000);
        await Assert.That(vm.MacroName).IsEqualTo("второй");
        // Подсветка обязана уехать на новый макрос: запись выше пересобрала строки библиотеки, и
        // выделение легко было оставить на объекте, которого в дереве больше нет.
        await Assert.That(vm.SelectedMacro?.Name).IsEqualTo("второй");
        await Assert.That(vm.Macros.Single(row => row.Name == "второй").IsCurrent).IsTrue();
        await Assert.That(vm.Macros.Single(row => row.Name == "первый").IsCurrent).IsFalse();
    }

    // То же самое делает закрытие окна панели (MainWindow.OnClosing) и создание нового макроса.
    [Test]
    public async Task FlushWritesImmediately_WithoutWaitingOutTheQuietPeriod()
    {
        using var panel = new Panel();
        using var vm = Open(panel, Chain("уходящий"));

        Edit(vm, "4");
        vm.FlushAutoSave();

        await Assert.That(((DelayNode)panel.Find("уходящий")!.Nodes[0]).Ms).IsEqualTo(4000);
        await Assert.That(vm.IsDirty()).IsFalse();
    }

    // Правка ФУНКЦИИ — это правка того же файла, и часы обязаны её видеть: опора «есть
    // несохранённое» с волны F4 покрывает весь бандл, а не открытый граф.
    [Test]
    public async Task ASubmacroEditIsAutoSavedToo()
    {
        using var panel = new Panel();
        using var vm = Open(panel, Chain("с-функцией"));

        vm.AddSubmacro("вынесенное");
        TickUntilQuiet(vm);

        await Assert.That(panel.Library.Library.TryGet("с-функцией")!.Submacros).Count().IsEqualTo(1);

        // Открываем функцию и правим ЕЁ граф — файл тот же.
        vm.OpenSubmacro(vm.Submacros.Single().Id);
        vm.AddNode(MacroNodeKind.Delay);
        var nodes = vm.Nodes.Count;
        TickUntilQuiet(vm);

        await Assert.That(panel.Library.Library.TryGet("с-функцией")!.Submacros.Single().Graph.Nodes)
            .Count().IsEqualTo(nodes);
    }
}
