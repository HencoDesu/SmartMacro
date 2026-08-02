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

// W0.2b: the primitives layer — the seam where the walker's hwnd-addressed operations
// become real input, vision, and window cosmetics. Everything below the seam
// (IGameWindow, IClassMatcher) is faked; the wiring itself is under test.
public class MacroPrimitivesTests
{
    private static readonly IntPtr Hwnd = new(0xBEEF);

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
            Directory.CreateDirectory(Path.Combine(AssetsRoot, "GameUiElements"));
            Directory.CreateDirectory(Path.Combine(AssetsRoot, "GameClassNames"));

            Registry = new WindowRegistry(NullLogger<WindowRegistry>.Instance);
            Window = A.Fake<IGameWindow>();
            A.CallTo(() => Window.Handle).Returns(Hwnd);
            Registry.Register(Hwnd, "elementclient_64", Window);

            Matcher = A.Fake<IClassMatcher>();
            Primitives = new MacroPrimitives(
                Registry,
                new AgentInputDispatcher(NullLogger<AgentInputDispatcher>.Instance),
                new TemplateSetProvider(AssetsRoot, NullLogger<TemplateSetProvider>.Instance),
                Matcher,
                new WindowIconService(NullLogger<WindowIconService>.Instance),
                NullLogger<MacroPrimitives>.Instance);
        }

        /// <summary>Writes a stand-in template file; only its bytes travel through the seam.</summary>
        public void WriteTemplate(string folder, string stem, string content) =>
            File.WriteAllText(Path.Combine(AssetsRoot, folder, $"{stem}.png"), content);

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
        harness.WriteTemplate("GameUiElements", "ServerSelectButton", "png-bytes");
        var center = new ScreenPoint(640, 480);
        A.CallTo(() => harness.Window.FindElementAsync(A<byte[]>._, A<ScreenRect>._, A<CancellationToken>._))
            .Returns(Task.FromResult<ScreenPoint?>(center));

        var region = new ScreenRect(10, 20, 30, 40);
        var found = await harness.Primitives.FindElementAsync(Hwnd, "ServerSelectButton", region, CancellationToken.None);

        await Assert.That(found).IsEqualTo(center);
        // The node's region is passed through verbatim — cropping is the window's job.
        A.CallTo(() => harness.Window.FindElementAsync(A<byte[]>._, region, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task FindElement_NullRegion_BecomesTheEmptyRect_MeaningWholeClientArea()
    {
        using var harness = new Harness();
        harness.WriteTemplate("GameUiElements", "Anywhere", "png-bytes");

        await harness.Primitives.FindElementAsync(Hwnd, "Anywhere", region: null, CancellationToken.None);

        A.CallTo(() => harness.Window.FindElementAsync(A<byte[]>._, default(ScreenRect), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task FindElement_MissingTemplate_IsNotFound_AndNeverTouchesTheWindow()
    {
        using var harness = new Harness();

        var found = await harness.Primitives.FindElementAsync(Hwnd, "NoSuchTemplate", null, CancellationToken.None);

        await Assert.That(found).IsNull();
        A.CallTo(() => harness.Window.FindElementAsync(A<byte[]>._, A<ScreenRect>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task WaitForElement_ConvertsTheTimeoutToABudget()
    {
        using var harness = new Harness();
        harness.WriteTemplate("GameUiElements", "ChatPanelButtons", "png-bytes");
        A.CallTo(() => harness.Window.WaitForElementAsync(A<byte[]>._, A<ScreenRect>._, A<TimeSpan>._, A<CancellationToken>._))
            .Returns(Task.FromResult<ScreenPoint?>(new ScreenPoint(1, 2)));

        var found = await harness.Primitives.WaitForElementAsync(Hwnd, "ChatPanelButtons", null, 1500, CancellationToken.None);

        await Assert.That(found).IsEqualTo(new ScreenPoint(1, 2));
        A.CallTo(() => harness.Window.WaitForElementAsync(
                A<byte[]>._, A<ScreenRect>._, TimeSpan.FromMilliseconds(1500), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task Recognize_MapsTheSetToItsTemplates_AndReturnsTheWinningTag()
    {
        using var harness = new Harness();
        harness.WriteTemplate("GameClassNames", "Лучник", "archer-template");
        harness.WriteTemplate("GameClassNames", "Жрец", "priest-template");
        A.CallTo(() => harness.Window.CaptureScreenshot()).Returns([1, 2, 3]);

        IReadOnlyDictionary<string, byte[]>? seen = null;
        A.CallTo(() => harness.Matcher.Match(A<byte[]>._, A<IReadOnlyDictionary<string, byte[]>>._, A<ScreenRect>._))
            .Invokes((byte[] _, IReadOnlyDictionary<string, byte[]> templates, ScreenRect _) => seen = templates)
            .Returns(new TagMatch("Лучник", 0.93));

        var region = new ScreenRect(3200, 1060, 160, 35);
        var tag = await harness.Primitives.RecognizeAsync(Hwnd, TemplateSetProvider.ClassesSetName, region, CancellationToken.None);

        await Assert.That(tag).IsEqualTo("Лучник");
        // The "classes" set name resolves onto the shipped GameClassNames folder, keyed by
        // file stem — the stem IS the tag the node applies.
        await Assert.That(seen).IsNotNull();
        await Assert.That(seen!.Keys.Order().ToList()).IsEquivalentTo(new List<string> { "Жрец", "Лучник" });
        A.CallTo(() => harness.Matcher.Match(A<byte[]>._, A<IReadOnlyDictionary<string, byte[]>>._, region))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task Recognize_NoMatch_IsNull()
    {
        using var harness = new Harness();
        harness.WriteTemplate("GameClassNames", "Лучник", "archer-template");
        A.CallTo(() => harness.Window.CaptureScreenshot()).Returns([1]);
        A.CallTo(() => harness.Matcher.Match(A<byte[]>._, A<IReadOnlyDictionary<string, byte[]>>._, A<ScreenRect>._))
            .Returns(null);

        var tag = await harness.Primitives.RecognizeAsync(Hwnd, "classes", new ScreenRect(0, 0, 10, 10), CancellationToken.None);

        await Assert.That(tag).IsNull();
    }

    [Test]
    public async Task Recognize_EmptySet_IsNull_AndSkipsTheCapture()
    {
        using var harness = new Harness();

        var tag = await harness.Primitives.RecognizeAsync(Hwnd, "no-such-set", new ScreenRect(0, 0, 10, 10), CancellationToken.None);

        await Assert.That(tag).IsNull();
        A.CallTo(() => harness.Window.CaptureScreenshot()).MustNotHaveHappened();
    }

    [Test]
    public async Task UnknownWindow_IsALoggedNoOp_NotAFailure()
    {
        using var harness = new Harness();
        var stranger = new IntPtr(0x1234);

        // A fan-out racing window teardown must not blow up the run.
        await harness.Primitives.PressKeyAsync(stranger, VirtualKey.F8, CancellationToken.None);
        await harness.Primitives.ClickAsync(stranger, new ScreenPoint(1, 1), false, CancellationToken.None);
        var found = await harness.Primitives.FindElementAsync(stranger, "whatever", null, CancellationToken.None);
        var tag = await harness.Primitives.RecognizeAsync(stranger, "classes", default, CancellationToken.None);

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

// The icon service owns path resolution for SetIconNode: relative paths resolve against
// the app folder, and PW's Russian-tag → English-file-stem alias keeps the shipped
// Assets/ClassIcons working with a "{tag}.png" template.
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
            // The interpolated "{tag}.png" path doesn't exist, but the shipped English
            // icon for that tag does.
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
