using SmartMacro.App.Ipc;
using SmartMacro.App.Mvvm;
using SmartMacro.App.Services;
using SmartMacro.App.ViewModels;

namespace SmartMacro.App;

/// <summary>
/// Всё, чем владеет процесс панели, — а после стадии 3 список очень короткий: одно
/// IPC-соединение и три тонких переходника поверх него.
///
/// Это пришло на смену полноценному корню композиции на <c>Microsoft.Extensions.Hosting</c>.
/// Контейнер отрабатывал свой хлеб, пока движок жил внутри UI: дюжина синглтонов, у которых
/// порядок разрешения имел значение. Но граф из четырёх объектов, где нечем управлять по времени
/// жизни, дешевле прочитать, чем настроить, — а выкинуть пакеты хоста и было одной из целей
/// разделения.
/// </summary>
internal sealed class AppServices : IAsyncDisposable
{
    private readonly IpcClient _client;

    public AppServices(IpcClient client, string macroFolderPath)
    {
        _client = client;
        MacroFolderPath = macroFolderPath;
        MacroLauncher = new IpcMacroLauncher(client);
        HotkeySuspension = new IpcHotkeySuspension(client);
    }

    /// <summary>Соединение с демоном. Всё, что показывает UI, пришло отсюда.</summary>
    public IIpcClient Client => _client;

    /// <summary>Перекладывание пушей демона в поток UI.</summary>
    public IUiDispatcher Dispatcher { get; } = AvaloniaUiDispatcher.Instance;

    /// <summary>Шов «запусти этот макрос» для редактора.</summary>
    public IMacroLauncher MacroLauncher { get; }

    /// <summary>Приостановка и возобновление глобальных хоткеев демона вокруг ловушки сочетания.</summary>
    public IHotkeySuspension HotkeySuspension { get; }

    /// <summary>
    /// Абсолютный путь к папке <c>macros/</c> демона — для кнопки «открыть папку» в редакторе.
    /// Вычисляется от того места, где на самом деле лежит исполняемый файл демона: в дереве
    /// разработки это соседний каталог bin, а не наш.
    /// </summary>
    public string MacroFolderPath { get; }

    /// <summary>
    /// Собирает оболочку целиком — рейку режимов и обе режимные view-models. Намеренно фабрика, а
    /// не свойство: VM начинают отправлять работу через диспетчер Avalonia сразу после создания,
    /// значит, появляться на свет до инициализации фреймворка им нельзя.
    ///
    /// Волна D2 вложила редактор макросов внутрь оболочки: раньше он строился на каждый диалог, а
    /// теперь строится здесь один раз и живёт столько же, сколько окно, — именно это и позволяет
    /// недоправленному графу пережить поход в другой режим.
    /// </summary>
    public ShellViewModel CreateShellViewModel() =>
        new(
            CreateWorkspaceViewModel(),
            CreateMacroEditorViewModel(),
            CreateTemplatesViewModel(),
            CreateLogViewModel(),
            MacroLauncher);

    /// <summary>Собирает view-model окон и прогонов саму по себе (нужно пути дизайнера).</summary>
    public WorkspaceViewModel CreateWorkspaceViewModel() => new(Client, Dispatcher);

    /// <summary>Собирает view-model редактора макросов, стоящую за режимом «Макросы».</summary>
    public MacroEditorViewModel CreateMacroEditorViewModel() =>
        new(Client, MacroLauncher, HotkeySuspension, Dispatcher, MacroFolderPath);

    /// <summary>
    /// Собирает view-model браузера шаблонов, стоящую за режимом «Шаблоны». Пути к ассетам она не
    /// получает и получить не может: дерево живёт у демона, и панель видит его только через
    /// <c>GetTemplates</c> / <c>GetTemplateImage</c>.
    /// </summary>
    public TemplatesViewModel CreateTemplatesViewModel() => new(Client, Dispatcher);

    /// <summary>
    /// Собирает view-model ленты журнала, стоящую за режимом «Лог». Своего пути к файлам она,
    /// как и браузер шаблонов, не получает: <c>logs/</c> лежит рядом с демоном, и панель видит
    /// журнал только через <c>SubscribeLog</c>. Собственный файл этого процесса
    /// (<c>logs/smartmacro-ui-*.log</c>) — другая история и в ленту не попадает.
    /// </summary>
    public LogViewModel CreateLogViewModel() => new(Client, Dispatcher);

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
