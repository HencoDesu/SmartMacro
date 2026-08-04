using FakeItEasy;
using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.GameWindows;
using SmartMacro.Input;
using SmartMacro.Macros.Execution;
using SmartMacro.Native;
using SmartMacro.Presentation;
using SmartMacro.Vision;
using SmartMacro.Windows;

namespace SmartMacro.Tests.Macros;

// W0.2b: слой примитивов — тот шов, на котором адресованные по hwnd операции обходчика
// превращаются в настоящий ввод, vision и косметику окна. Всё, что ниже шва (IGameWindow,
// IClassMatcher), подделано; проверяется сама обвязка.
//
// Волна F2 убрала отсюда целый пласт: раньше здесь стоял TemplateSetProvider поверх временной
// папки, и половина тестов проверяла РАЗРЕШЕНИЕ ИМЁН — «шаблона нет», «набор пуст», «набор
// classes стал подпапкой». Разрешением больше не занимается этот слой: с переездом шаблонов
// внутрь бандла имя без макроса ничего не значит, и в примитивы приезжают готовые байты
// (см. IMacroPrimitives). Те проверки переехали туда, где решение теперь принимается, —
// MacroExecutorWalkerTests («нода уходит по „не найдено“, не позвав примитив») и
// MacroTemplateCacheTests («что вообще есть в бандле»).
public class MacroPrimitivesTests
{
    private static readonly IntPtr Hwnd = new(0xBEEF);

    /// <summary>Байты «шаблона»: через шов едет массив, а какой именно — проверяют по нему же.</summary>
    private static byte[] Template(string label) => System.Text.Encoding.UTF8.GetBytes(label);

    private sealed class Harness : IDisposable
    {
        public string AssetsRoot { get; }
        public WindowRegistry Registry { get; }
        public IGameWindow Window { get; }
        public IClassMatcher Matcher { get; }
        public MacroPrimitives Primitives { get; }

        public Harness()
        {
            AssetsRoot = Path.Combine(Path.GetTempPath(), $"smartmacro-assets-{Guid.NewGuid():N}");
            Directory.CreateDirectory(AssetsRoot);

            Registry = new WindowRegistry(NullLogger<WindowRegistry>.Instance);
            Window = A.Fake<IGameWindow>();
            A.CallTo(() => Window.Handle).Returns(Hwnd);
            Registry.Register(Hwnd, "elementclient_64", Window);

            Matcher = A.Fake<IClassMatcher>();
            Primitives = new MacroPrimitives(
                Registry,
                new AgentInputDispatcher(NullLogger<AgentInputDispatcher>.Instance),
                Matcher,
                new WindowIconService(NullLogger<WindowIconService>.Instance),
                NullLogger<MacroPrimitives>.Instance);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(AssetsRoot, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Test]
    public async Task FindElement_ReturnsTheMatchCenter_FromTheWindow()
    {
        using var harness = new Harness();
        var template = Template("ServerSelectButton");
        var center = new ScreenPoint(640, 480);
        A.CallTo(() => harness.Window.FindElementAsync(A<byte[]>._, A<ScreenRect>._, A<double?>._, A<CancellationToken>._))
            .Returns(Task.FromResult<ScreenPoint?>(center));

        var region = new ScreenRect(10, 20, 30, 40);
        var found = await harness.Primitives.FindElementAsync(Hwnd, template, region, matchThreshold: null, CancellationToken.None);

        await Assert.That(found).IsEqualTo(center);
        // Область из ноды передаётся дословно — обрезка это забота окна; байты шаблона тоже
        // уезжают как есть.
        A.CallTo(() => harness.Window.FindElementAsync(template, region, A<double?>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task FindElement_NullRegion_BecomesTheEmptyRect_MeaningWholeClientArea()
    {
        using var harness = new Harness();

        await harness.Primitives.FindElementAsync(Hwnd, Template("Anywhere"), region: null, matchThreshold: null, CancellationToken.None);

        A.CallTo(() => harness.Window.FindElementAsync(A<byte[]>._, default(ScreenRect), A<double?>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task WaitForElement_ConvertsTheTimeoutToABudget()
    {
        using var harness = new Harness();
        A.CallTo(() => harness.Window.WaitForElementAsync(A<byte[]>._, A<ScreenRect>._, A<TimeSpan>._, A<double?>._, A<CancellationToken>._))
            .Returns(Task.FromResult<ScreenPoint?>(new ScreenPoint(1, 2)));

        var found = await harness.Primitives.WaitForElementAsync(Hwnd, Template("ChatPanelButtons"), null, 1500, matchThreshold: null, CancellationToken.None);

        await Assert.That(found).IsEqualTo(new ScreenPoint(1, 2));
        A.CallTo(() => harness.Window.WaitForElementAsync(A<byte[]>._, A<ScreenRect>._, TimeSpan.FromMilliseconds(1500), A<double?>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task Recognize_PassesTheSetThroughToTheMatcher_AndReturnsTheWinningTag()
    {
        using var harness = new Harness();
        var set = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["Лучник"] = Template("archer-template"),
            ["Жрец"] = Template("priest-template"),
        };
        A.CallTo(() => harness.Window.CaptureScreenshot()).Returns([1, 2, 3]);

        IReadOnlyDictionary<string, byte[]>? seen = null;
        A.CallTo(() => harness.Matcher.Match(A<byte[]>._, A<IReadOnlyDictionary<string, byte[]>>._, A<ScreenRect>._, A<double?>._))
            .Invokes((byte[] _, IReadOnlyDictionary<string, byte[]> templates, ScreenRect _, double? _) => seen = templates)
            .Returns(new TagMatch("Лучник", 0.93));

        var region = new ScreenRect(3200, 1060, 160, 35);
        var tag = await harness.Primitives.RecognizeAsync(Hwnd, set, region, matchThreshold: null, CancellationToken.None);

        await Assert.That(tag).IsEqualTo("Лучник");
        // Ключ словаря И ЕСТЬ тег, который навесит нода: сопоставителю он доезжает нетронутым.
        await Assert.That(seen).IsNotNull();
        await Assert.That(seen!.Keys.Order().ToList()).IsEquivalentTo(new List<string> { "Жрец", "Лучник" });
        A.CallTo(() => harness.Matcher.Match(A<byte[]>._, A<IReadOnlyDictionary<string, byte[]>>._, region, A<double?>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task Recognize_NoMatch_IsNull()
    {
        using var harness = new Harness();
        var set = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["Лучник"] = Template("archer") };
        A.CallTo(() => harness.Window.CaptureScreenshot()).Returns([1]);
        A.CallTo(() => harness.Matcher.Match(A<byte[]>._, A<IReadOnlyDictionary<string, byte[]>>._, A<ScreenRect>._, A<double?>._))
            .Returns(null);

        var tag = await harness.Primitives.RecognizeAsync(Hwnd, set, new ScreenRect(0, 0, 10, 10), matchThreshold: null, CancellationToken.None);

        await Assert.That(tag).IsNull();
    }

    [Test]
    public async Task Recognize_EmptySet_IsNull_AndSkipsTheCapture()
    {
        using var harness = new Harness();

        // Пустой словарь доезжает сюда только тогда, когда обходчик не нашёл набора в бандле и
        // уже сказал об этом в журнал. Захват — самая дорогая операция слоя, и ради заведомо
        // безрезультатного сопоставления её делать нельзя.
        var tag = await harness.Primitives.RecognizeAsync(
            Hwnd,
            new Dictionary<string, byte[]>(StringComparer.Ordinal),
            new ScreenRect(0, 0, 10, 10),
            matchThreshold: null,
            CancellationToken.None);

        await Assert.That(tag).IsNull();
        A.CallTo(() => harness.Window.CaptureScreenshot()).MustNotHaveHappened();
    }

    [Test]
    public async Task UnknownWindow_IsALoggedNoOp_NotAFailure()
    {
        using var harness = new Harness();
        var stranger = new IntPtr(0x1234);

        // Веер, попавший в гонку со сносом окна, не имеет права разорвать прогон.
        await harness.Primitives.PressKeyAsync(stranger, VirtualKey.F8, CancellationToken.None);
        await harness.Primitives.ClickAsync(stranger, new ScreenPoint(1, 1), false, CancellationToken.None);
        var found = await harness.Primitives.FindElementAsync(stranger, Template("whatever"), null, matchThreshold: null, CancellationToken.None);
        var tag = await harness.Primitives.RecognizeAsync(
            stranger,
            new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["Лучник"] = Template("archer") },
            default,
            matchThreshold: null,
            CancellationToken.None);

        await Assert.That(found).IsNull();
        await Assert.That(tag).IsNull();
    }

    [Test]
    public async Task SetIcon_AppliesTheFileToTheRegisteredWindow()
    {
        using var harness = new Harness();
        var iconPath = Path.Combine(harness.AssetsRoot, "icon.png");
        File.WriteAllText(iconPath, "icon-bytes");

        await harness.Primitives.SetIconAsync(Hwnd, iconPath, CancellationToken.None);

        A.CallTo(() => harness.Window.SetIconFromFile(iconPath)).MustHaveHappened();
    }
}

// Разрешение путей для SetIconNode принадлежит службе иконок: относительные пути отсчитываются
// от папки приложения, а псевдоним «русский тег → английская основа имени файла» позволяет
// поставляемым Assets/ClassIcons работать с шаблоном "{tag}.png".
public class WindowIconServiceTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"smartmacro-icons-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public async Task ExistingPath_IsAppliedAsIs()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "Лучник.png");
            File.WriteAllText(path, "icon");
            var window = A.Fake<IGameWindow>();
            A.CallTo(() => window.SetIconFromFile(A<string>._)).Returns(true);
            var service = new WindowIconService(NullLogger<WindowIconService>.Instance);

            var applied = service.TryApply(window, path);

            await Assert.That(applied).IsTrue();
            A.CallTo(() => window.SetIconFromFile(path)).MustHaveHappened();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task MissingPath_FallsBackToTheLegacyTagAlias()
    {
        var dir = CreateTempDir();
        try
        {
            // Подставленного пути "{tag}.png" не существует, а вот поставляемая английская
            // иконка для этого тега — существует.
            var aliased = Path.Combine(dir, "archer.png");
            File.WriteAllText(aliased, "icon");
            var window = A.Fake<IGameWindow>();
            A.CallTo(() => window.SetIconFromFile(A<string>._)).Returns(true);
            var service = new WindowIconService(NullLogger<WindowIconService>.Instance);

            var applied = service.TryApply(window, Path.Combine(dir, "Лучник.png"));

            await Assert.That(applied).IsTrue();
            A.CallTo(() => window.SetIconFromFile(aliased)).MustHaveHappened();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task UnresolvablePath_IsAQuietFailure_NotAnException()
    {
        var window = A.Fake<IGameWindow>();
        var service = new WindowIconService(NullLogger<WindowIconService>.Instance);

        var applied = service.TryApply(window, Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.png"));

        await Assert.That(applied).IsFalse();
        A.CallTo(() => window.SetIconFromFile(A<string>._)).MustNotHaveHappened();
    }
}
