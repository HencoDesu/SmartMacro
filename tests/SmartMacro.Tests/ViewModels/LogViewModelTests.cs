using SmartMacro.App.Mvvm;
using SmartMacro.App.ViewModels;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Resources;
using SmartMacro.Tests.Ipc;

namespace SmartMacro.Tests.ViewModels;

// Режим «Лог». Проверяется три вещи, и все три — про то, чем лента отличается от простого
// списка, приехавшего по запросу:
//
//   · ПРЕДЫСТОРИЯ. Панель, подключившаяся к работающему демону, видит, что было до неё.
//   · НОМЕРА. Демон включает ленту ДО того, как снимает предысторию (иначе была бы дыра), так
//     что одна запись законно приезжает дважды, и отличить повтор можно только по Seq.
//   · ФИЛЬТР И ПОТОЛОК. Лента едет сама, и без фильтра, поиска и обрезки её нельзя ни читать, ни
//     держать в памяти.
public class LogViewModelTests
{
    private static LogEntryDto Entry(
        long seq,
        LogLevelDto level = LogLevelDto.Information,
        string message = "строка",
        string? source = "SmartMacro.Windows.WindowRegistry",
        string? exception = null) =>
        new(seq, DateTimeOffset.Now, level, source, message, exception);

    private static FakeIpcClient Client(params LogEntryDto[] history) =>
        new FakeIpcClient().Respond(IpcMessageTypes.SubscribeLog, history);

    private static async Task<LogViewModel> CreateAsync(FakeIpcClient client)
    {
        var vm = new LogViewModel(client, ImmediateUiDispatcher.Instance);
        await vm.SetSubscriptionAsync(true);
        return vm;
    }

    private static void Push(FakeIpcClient client, int dropped, params LogEntryDto[] entries) =>
        client.RaiseEvent(IpcMessageTypes.LogEntries, new LogEntryBatch(entries, dropped));

    // ---- предыстория --------------------------------------------------------------------------

    [Test]
    public async Task Subscribing_SeedsTheFeedWithTheDaemonsHistory()
    {
        // Ради этого кольцо у демона и есть: панель открыли, чтобы увидеть, что ТОЛЬКО ЧТО
        // произошло, а не чтобы ждать следующей записи.
        using var vm = await CreateAsync(Client(
            Entry(1, message: "демон стартовал"),
            Entry(2, LogLevelDto.Warning, "окно не опознано")));

        await Assert.That(vm.Rows.Select(r => r.Message))
            .IsEquivalentTo(new[] { "демон стартовал", "окно не опознано" });
        await Assert.That(vm.IsEmpty).IsFalse();
        await Assert.That(vm.TotalCount).IsEqualTo(2);
    }

    [Test]
    public async Task AnEmptyHistory_IsAnHonestEmptyState()
    {
        using var vm = await CreateAsync(Client());

        await Assert.That(vm.IsEmpty).IsTrue();
        await Assert.That(vm.IsFilteredOut).IsFalse();
        await Assert.That(vm.SummaryText).IsEqualTo(Strings.Log_Header_Empty);
    }

    [Test]
    public async Task Reconnect_ReplacesTheFeed_RatherThanAppendingToIt()
    {
        // Seq сквозной только в пределах жизни ОДНОГО демона: после перезапуска номера начнутся
        // заново и столкнутся с накопленными. Замещение убирает весь этот класс ошибок.
        var client = Client(Entry(10, message: "от прошлого демона"));
        using var vm = await CreateAsync(client);

        client.Respond(IpcMessageTypes.SubscribeLog, new[] { Entry(1, message: "от нового демона") });
        client.RaiseConnected();

        await Assert.That(vm.Rows.Select(r => r.Message)).IsEquivalentTo(new[] { "от нового демона" });
    }

    [Test]
    public async Task Reconnect_DoesNothingWhenTheFeedWasNeverAskedFor()
    {
        var client = Client(Entry(1));
        using var vm = new LogViewModel(client, ImmediateUiDispatcher.Instance);

        client.RaiseConnected();

        await Assert.That(client.CountOf(IpcMessageTypes.SubscribeLog)).IsEqualTo(0);
        await Assert.That(vm.IsEmpty).IsTrue();
    }

    // ---- номера ------------------------------------------------------------------------------

    [Test]
    public async Task ARecordThatArrivesInBothTheHistoryAndTheFirstBatch_IsShownOnce()
    {
        // Демон включает ленту, и только потом снимает предысторию: наоборот образовалась бы
        // невосполнимая дыра. Расплата — повтор ровно одной записи, и Seq здесь единственное,
        // чем повтор отличается от второй такой же строки.
        var client = Client(Entry(1, message: "одинаковая"), Entry(2, message: "одинаковая"));
        using var vm = await CreateAsync(client);

        Push(client, 0, Entry(2, message: "одинаковая"), Entry(3, message: "новая"));

        await Assert.That(vm.Rows.Select(r => r.Seq)).IsEquivalentTo(new[] { 1L, 2L, 3L });
    }

    [Test]
    public async Task AnEmptyFeed_SaysWhichKindOfEmptyItIs()
    {
        // «Демон пока ничего не записал» после «Очистить» — прямое враньё про демона, который
        // записал предостаточно. Найдено глазами.
        var client = Client(Entry(1));
        using var vm = await CreateAsync(client);
        await Assert.That(vm.WasCleared).IsFalse();

        vm.Clear();

        await Assert.That(vm.IsEmpty).IsTrue();
        await Assert.That(vm.WasCleared).IsTrue();
        // Подсказка после очистки — отдельный ключ: она объясняет, что журнал сервиса цел.
        await Assert.That(vm.EmptyHint).IsEqualTo(Strings.Log_Cleared_Hint);

        // Новая предыстория — новое начало.
        client.Respond(IpcMessageTypes.SubscribeLog, Array.Empty<LogEntryDto>());
        client.RaiseConnected();

        await Assert.That(vm.WasCleared).IsFalse();
    }

    [Test]
    public async Task Clear_DoesNotMakeThePanelAcceptRecordsItAlreadySaw()
    {
        // «Очистить» — это протереть экран, а не забыть, докуда демон досчитал. Сбросив здесь
        // ещё и номер, мы бы приняли те же записи второй раз со следующей предысторией.
        var client = Client(Entry(1), Entry(2));
        using var vm = await CreateAsync(client);

        vm.Clear();
        Push(client, 0, Entry(2), Entry(3, message: "новая"));

        await Assert.That(vm.Rows.Select(r => r.Seq)).IsEquivalentTo(new[] { 3L });
    }

    // ---- фильтр и поиск ------------------------------------------------------------------------

    [Test]
    public async Task LevelFilter_ShowsEverythingByDefault()
    {
        using var vm = await CreateAsync(Client(
            Entry(1, LogLevelDto.Debug),
            Entry(2),
            Entry(3, LogLevelDto.Error)));

        await Assert.That(vm.LevelFilter.Level).IsEqualTo(LogLevelDto.Verbose);
        await Assert.That(vm.Rows).Count().IsEqualTo(3);
    }

    [Test]
    public async Task LevelFilter_KeepsWhatIsAtOrAboveIt_AndCanBeMovedBackDown()
    {
        var client = Client(
            Entry(1, LogLevelDto.Debug, "мелочь"),
            Entry(2, LogLevelDto.Information, "обычное"),
            Entry(3, LogLevelDto.Warning, "тревога"),
            Entry(4, LogLevelDto.Error, "поломка"));
        using var vm = await CreateAsync(client);

        vm.LevelFilter = vm.LevelOptions.Single(o => o.Level == LogLevelDto.Warning);
        await Assert.That(vm.Rows.Select(r => r.Message)).IsEquivalentTo(new[] { "тревога", "поломка" });

        // Обратный ход — и есть причина, по которой фильтр живёт в панели, а не в запросе:
        // серверный фильтр умеет только не прислать, а показать уже приехавшее — нет.
        vm.LevelFilter = vm.LevelOptions.Single(o => o.Level == LogLevelDto.Verbose);
        await Assert.That(vm.Rows).Count().IsEqualTo(4);
    }

    [Test]
    public async Task NewRecordsRespectTheFilterThatIsAlreadySet()
    {
        var client = Client();
        using var vm = await CreateAsync(client);
        vm.LevelFilter = vm.LevelOptions.Single(o => o.Level == LogLevelDto.Error);

        Push(client, 0, Entry(1, LogLevelDto.Information, "шум"), Entry(2, LogLevelDto.Error, "важное"));

        await Assert.That(vm.Rows.Select(r => r.Message)).IsEquivalentTo(new[] { "важное" });
        // Отфильтрованное никуда не делось — оно есть в ленте и вернётся, если фильтр опустить.
        await Assert.That(vm.TotalCount).IsEqualTo(2);
    }

    [Test]
    public async Task Search_LooksInTheMessage_TheSourceAndTheStack_IgnoringCase()
    {
        using var vm = await CreateAsync(Client(
            Entry(1, message: "ОКНО зарегистрировано"),
            Entry(2, message: "ничего", source: "SmartMacro.Vision.ClassMatcher"),
            Entry(3, LogLevelDto.Error, "упало", exception: "System.IO.IOException: труба закрыта"),
            Entry(4, message: "посторонняя строка")));

        vm.Search = "окно";
        await Assert.That(vm.Rows.Select(r => r.Seq)).IsEquivalentTo(new[] { 1L });

        vm.Search = "classmatcher";
        await Assert.That(vm.Rows.Select(r => r.Seq)).IsEquivalentTo(new[] { 2L });

        vm.Search = "ioexception";
        await Assert.That(vm.Rows.Select(r => r.Seq)).IsEquivalentTo(new[] { 3L });

        vm.Search = string.Empty;
        await Assert.That(vm.Rows).Count().IsEqualTo(4);
    }

    [Test]
    public async Task NothingMatching_IsItsOwnState_NotTheEmptyOne()
    {
        // «Записей нет» и «ни одна не подошла» — разные новости, и вторая означает, что чинить
        // надо фильтр.
        using var vm = await CreateAsync(Client(Entry(1)));

        vm.Search = "такого нет";

        await Assert.That(vm.Rows).IsEmpty();
        await Assert.That(vm.IsEmpty).IsFalse();
        await Assert.That(vm.IsFilteredOut).IsTrue();
        // Итог склеен из двух ресурсов: всего записей и сколько осталось после фильтра.
        await Assert.That(Msg.Parts(vm.SummaryText, Strings.Log_Header_Entries_One, Strings.Log_Header_Shown))
            .IsEquivalentTo(new[] { "1", "0" });
    }

    // ---- счётчики и дыры ------------------------------------------------------------------------

    [Test]
    public async Task ProblemCount_IsWarningsAndWorse()
    {
        var client = Client(Entry(1, LogLevelDto.Warning), Entry(2));
        using var vm = await CreateAsync(client);
        await Assert.That(vm.ProblemCount).IsEqualTo(1);

        Push(client, 0, Entry(3, LogLevelDto.Error), Entry(4, LogLevelDto.Debug), Entry(5, LogLevelDto.Fatal));

        await Assert.That(vm.ProblemCount).IsEqualTo(3);
        await Assert.That(Msg.Parts(vm.SummaryText, Strings.Log_Header_Entries_Many, Strings.Log_Header_Problems))
            .IsEquivalentTo(new[] { "5", "3" });
    }

    [Test]
    public async Task ProblemCount_IgnoresTheFilter()
    {
        // Счётчик рейки виден из другого режима, где никакого фильтра нет. Считать сквозь чужую
        // настройку значило бы врать тому, кто её не видит.
        using var vm = await CreateAsync(Client(Entry(1, LogLevelDto.Error), Entry(2, LogLevelDto.Debug)));

        vm.Search = "ничего похожего";

        await Assert.That(vm.Rows).IsEmpty();
        await Assert.That(vm.ProblemCount).IsEqualTo(1);
    }

    [Test]
    public async Task ADroppedCount_IsReported_NotSwallowed()
    {
        var client = Client();
        using var vm = await CreateAsync(client);
        await Assert.That(vm.HasGap).IsFalse();

        Push(client, 12, Entry(1));
        await Assert.That(vm.HasGap).IsTrue();
        await Assert.That(Msg.Arg(vm.GapText, Strings.Log_Gap_Count)).IsEqualTo("12");

        // Дыры накапливаются: две пачки с потерями — это одна дыра большего размера, а не
        // забытая первая.
        Push(client, 3, Entry(2));
        await Assert.That(vm.Dropped).IsEqualTo(15);
    }

    // ---- потолок ------------------------------------------------------------------------------

    [Test]
    public async Task TheFeedIsCapped_AndTrimmingKeepsTheVisibleRowsInStep()
    {
        var client = Client();
        using var vm = await CreateAsync(client);

        // На двести строк больше потолка, включая одно предупреждение в самом начале: оно должно
        // уехать вместе со своей строкой, иначе счётчик проблем поехал бы навсегда.
        Push(client, 0, Entry(1, LogLevelDto.Warning, "самая первая"));
        for (var i = 2; i <= LogViewModel.MaxEntries + 200; i++)
        {
            Push(client, 0, Entry(i, LogLevelDto.Information, $"строка {i}"));
        }

        await Assert.That(vm.TotalCount).IsEqualTo(LogViewModel.MaxEntries);
        await Assert.That(vm.Rows).Count().IsEqualTo(LogViewModel.MaxEntries);
        await Assert.That(vm.ProblemCount).IsEqualTo(0);
        await Assert.That(vm.Rows[^1].Message).IsEqualTo($"строка {LogViewModel.MaxEntries + 200}");
    }

    [Test]
    public async Task Trimming_DoesNotTouchRowsTheFilterHides()
    {
        // Rows — подпоследовательность ленты, и обрезка снизу опирается ровно на это. Ошибись
        // здесь — и из видимого списка вылетала бы не та строка.
        var client = Client();
        using var vm = await CreateAsync(client);
        vm.LevelFilter = vm.LevelOptions.Single(o => o.Level == LogLevelDto.Error);

        Push(client, 0, Entry(1, LogLevelDto.Error, "первая ошибка"));
        for (var i = 2; i <= LogViewModel.MaxEntries; i++)
        {
            Push(client, 0, Entry(i, LogLevelDto.Debug));
        }

        // Лента ровно по потолку — ещё ничего не выбрасывали.
        await Assert.That(vm.Rows.Select(r => r.Message)).IsEquivalentTo(new[] { "первая ошибка" });

        Push(client, 0, Entry(LogViewModel.MaxEntries + 1, LogLevelDto.Debug));

        // А теперь выбросили самую старую — и это была видимая строка.
        await Assert.That(vm.Rows).IsEmpty();
        await Assert.That(vm.TotalCount).IsEqualTo(LogViewModel.MaxEntries);
    }

    // ---- строка ------------------------------------------------------------------------------

    [Test]
    public async Task ARow_ShortensTheSourceAndKeepsTheFullOneForTheTooltip()
    {
        using var vm = await CreateAsync(Client(Entry(1, source: "SmartMacro.Windows.WindowRegistry")));

        await Assert.That(vm.Rows[0].SourceText).IsEqualTo("WindowRegistry");
        await Assert.That(vm.Rows[0].SourceFull).IsEqualTo("SmartMacro.Windows.WindowRegistry");
    }

    [Test]
    public async Task ARow_WithoutASource_LeavesTheColumnEmptyRatherThanInventingOne()
    {
        // Так пишет Program на старте демона — через статический Log, без ForContext.
        using var vm = await CreateAsync(Client(Entry(1, source: null)));

        await Assert.That(vm.Rows[0].SourceText).IsEmpty();
        await Assert.That(vm.Rows[0].SourceFull).IsNull();
    }

    [Test]
    public async Task ARow_UsesTheSameThreeLetterLevelAsTheLogFile()
    {
        // {Level:u3} в шаблоне файла демона. Человек, увидевший строку в панели и пошедший
        // искать её в logs/, ищет то же самое слово.
        LogLevelDto[] levels =
        [
            LogLevelDto.Verbose,
            LogLevelDto.Debug,
            LogLevelDto.Information,
            LogLevelDto.Warning,
            LogLevelDto.Error,
            LogLevelDto.Fatal,
        ];
        using var vm = await CreateAsync(Client([.. levels.Select((level, i) => Entry(i + 1, level))]));

        await Assert.That(vm.Rows.Select(r => r.LevelText))
            .IsEquivalentTo(new[] { "VRB", "DBG", "INF", "WRN", "ERR", "FTL" });
    }

    [Test]
    public async Task ARow_WithAStack_StartsCollapsed()
    {
        using var vm = await CreateAsync(Client(
            Entry(1, LogLevelDto.Error, "упало", exception: "System.IO.IOException: труба закрыта\n   at X()"),
            Entry(2)));

        await Assert.That(vm.Rows[0].HasException).IsTrue();
        await Assert.That(vm.Rows[0].IsExpanded).IsFalse();
        await Assert.That(vm.Rows[1].HasException).IsFalse();
    }

    // ---- разборка -----------------------------------------------------------------------------

    [Test]
    public async Task Dispose_Unsubscribes()
    {
        var client = Client();
        var vm = await CreateAsync(client);
        vm.Dispose();

        Push(client, 0, Entry(1));
        client.RaiseConnected();

        await Assert.That(vm.IsEmpty).IsTrue();
        await Assert.That(client.CountOf(IpcMessageTypes.SubscribeLog)).IsEqualTo(1);
    }
}
