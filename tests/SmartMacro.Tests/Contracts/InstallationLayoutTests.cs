using SmartMacro.Contracts.Ipc;

namespace SmartMacro.Tests.Contracts;

// Где лежат macros\, templates\, settings.json и debug\. Ошибка здесь молчаливая и дорогая:
// демон заводит своё состояние в одной папке, панель показывает другую, библиотека выглядит
// пустой — и ни одной строки об ошибке. Поэтому правило проверяется в обеих раскладках сразу,
// а не в той, в которой случайно собран тестовый проект: обе половины выставлены наружу
// параметром shippedLayout, боевые перегрузки лишь подставляют в него #if DEBUG.
public class InstallationLayoutTests
{
    private const string Shipped = @"C:\apps\SmartMacro\daemon";
    private const string ShippedRoot = @"C:\apps\SmartMacro";
    private const string DevTree = @"D:\repo\src\SmartMacro.Daemon\bin\Debug\net10.0-windows";
    private const string PanelDevTree = @"D:\repo\src\SmartMacro.App\bin\Debug\net10.0-windows";

    // ---- демон -----------------------------------------------------------------------------

    [Test]
    public async Task Shipped_TheRootIsTheParentOfTheDaemonFolder()
    {
        await Assert.That(InstallationLayout.RootFromDaemonDirectory(Shipped, shippedLayout: true))
            .IsEqualTo(ShippedRoot);
    }

    [Test]
    public async Task DevTree_TheRootIsTheDaemonsOwnFolder()
    {
        await Assert.That(InstallationLayout.RootFromDaemonDirectory(DevTree, shippedLayout: false))
            .IsEqualTo(DevTree);
    }

    [Test]
    public async Task ATrailingSeparatorDoesNotEatTheParent()
    {
        // AppContext.BaseDirectory всегда приходит с хвостовым разделителем, а
        // Path.GetDirectoryName на такой строке отдаёт саму папку. Без нормализации демон в
        // поставке считал бы корнем daemon\ — то есть завёл бы там свои macros\ и делал бы это
        // молча.
        await Assert.That(InstallationLayout.RootFromDaemonDirectory(Shipped + "\\", shippedLayout: true))
            .IsEqualTo(ShippedRoot);
        await Assert.That(InstallationLayout.RootFromDaemonDirectory(DevTree + "\\", shippedLayout: false))
            .IsEqualTo(DevTree);
    }

    [Test]
    public async Task AtTheDriveRoot_ThereIsNowhereToGoUp()
    {
        // Подниматься некуда — не повод падать: состояние ляжет туда же, где exe.
        await Assert.That(InstallationLayout.RootFromDaemonDirectory(@"C:\", shippedLayout: true))
            .IsEqualTo("C:");
    }

    [Test]
    public async Task EmptyDirectory_Throws()
    {
        await Assert.That(() => InstallationLayout.RootFromDaemonDirectory("", shippedLayout: true))
            .Throws<ArgumentException>();
        await Assert.That(() => InstallationLayout.RootFromDaemonDirectory("   ", shippedLayout: false))
            .Throws<ArgumentException>();
    }

    // ---- панель ----------------------------------------------------------------------------

    [Test]
    public async Task Shipped_ThePanelIsAlreadyInTheRoot()
    {
        // Демона панель для этого не спрашивает: в поставке она сама лежит в корне, и ответ
        // не должен зависеть от того, нашёлся ли сосед.
        await Assert.That(InstallationLayout.RootFromPanelDirectory(ShippedRoot, Shipped, shippedLayout: true))
            .IsEqualTo(ShippedRoot);
        await Assert.That(InstallationLayout.RootFromPanelDirectory(ShippedRoot, null, shippedLayout: true))
            .IsEqualTo(ShippedRoot);
    }

    [Test]
    public async Task DevTree_ThePanelBorrowsTheDaemonsFolder()
    {
        // В своём bin\ у панели нет ни macros\, ни settings.json — они у демона, в соседнем.
        await Assert.That(InstallationLayout.RootFromPanelDirectory(PanelDevTree, DevTree, shippedLayout: false))
            .IsEqualTo(DevTree);
    }

    [Test]
    public async Task DevTree_WithoutADaemon_FallsBackToItsOwnFolder()
    {
        // Демона нет — поднимать панели всё равно нечего; отвечать чем-то надо, и своя папка
        // здесь наименее удивительный ответ.
        await Assert.That(InstallationLayout.RootFromPanelDirectory(PanelDevTree, null, shippedLayout: false))
            .IsEqualTo(PanelDevTree);
    }

    [Test]
    public async Task PanelRoot_HandlesATrailingSeparator()
    {
        await Assert.That(InstallationLayout.RootFromPanelDirectory(ShippedRoot + "\\", null, shippedLayout: true))
            .IsEqualTo(ShippedRoot);
        await Assert.That(InstallationLayout.RootFromPanelDirectory(PanelDevTree, DevTree + "\\", shippedLayout: false))
            .IsEqualTo(DevTree);
    }

    // ---- связка -----------------------------------------------------------------------------

    [Test]
    public async Task DaemonDirectory_IsTheRootsSubfolder()
    {
        await Assert.That(InstallationLayout.DaemonDirectory(ShippedRoot))
            .IsEqualTo(Path.Combine(ShippedRoot, "daemon"));
        await Assert.That(InstallationLayout.DaemonDirectory(ShippedRoot + "\\"))
            .IsEqualTo(Path.Combine(ShippedRoot, "daemon"));
    }

    [Test]
    public async Task TheTwoDirectionsAreEachOthersInverse()
    {
        // Панель спускается в daemon\, демон поднимается на уровень выше — если эти две
        // арифметики разъедутся, процессы перестанут находить друг друга ПЕРВЫМ кандидатом и
        // будут молча жить в разных корнях.
        var daemon = InstallationLayout.DaemonDirectory(ShippedRoot);

        await Assert.That(InstallationLayout.RootFromDaemonDirectory(daemon, shippedLayout: true))
            .IsEqualTo(ShippedRoot);
    }

    [Test]
    public async Task TheProductionOverloadsFollowTheBuildConfiguration()
    {
        // Единственное место, где раскладка берётся из #if DEBUG. Тесты собираются вместе со
        // всем остальным, поэтому ожидаемое считается тем же способом — проверяется не значение
        // флага, а то, что боевые перегрузки ему подчиняются.
        var expectedDaemonRoot = InstallationLayout.IsShippedLayout ? ShippedRoot : Shipped;

        await Assert.That(InstallationLayout.RootFromDaemonDirectory(Shipped)).IsEqualTo(expectedDaemonRoot);
        await Assert.That(InstallationLayout.RootFromPanelDirectory(PanelDevTree, DevTree))
            .IsEqualTo(InstallationLayout.IsShippedLayout ? PanelDevTree : DevTree);
    }
}
