using SmartMacro.App.Ipc;
using SmartMacro.App.Macros;
using SmartMacro.App.Mvvm;
using SmartMacro.App.Services;
using SmartMacro.App.ViewModels;

namespace SmartMacro.App;

/// <summary>
/// Всё, чем владеет процесс панели: одно IPC-соединение, два тонких переходника поверх него —
/// и, с волны F3, ПАПКА МАКРОСОВ.
///
/// Последнее — единственное доменное состояние, которое здесь есть, и появилось оно намеренно:
/// автором изменений в макросах стала панель, а библиотека — факт файловой системы, а не факт
/// демона, поэтому владеть ею должен процесс, в котором живёт редактор.
///
/// Это пришло на смену полноценному корню композиции на <c>Microsoft.Extensions.Hosting</c>.
/// Контейнер отрабатывал свой хлеб, пока движок жил внутри UI: дюжина синглтонов, у которых
/// порядок разрешения имел значение. Но граф из пяти объектов, где нечем управлять по времени
/// жизни, дешевле прочитать, чем настроить, — а выкинуть пакеты хоста и было одной из целей
/// разделения.
/// </summary>
internal sealed class AppServices : IAsyncDisposable
{
    private readonly IpcClient _client;

    /// <param name="client">Соединение с демоном.</param>
    /// <param name="root">
    /// Корень установки. Отсюда берётся <c>macros/</c>; вычисляет его хост, потому что только он
    /// знает, где на самом деле лежит демон (в дереве разработки корень — его папка).
    /// </param>
    public AppServices(IpcClient client, string root)
    {
        _client = client;
        Library = new MacroLibrary(root);
        MacroLauncher = new IpcMacroLauncher(client);
        HotkeySuspension = new IpcHotkeySuspension(client);
        NameConflicts = new DialogMacroNameConflictPrompt();
    }

    /// <summary>Папка <c>macros/</c>: панель — её единственный автор, демон только читает (F3).</summary>
    public MacroLibrary Library { get; }

    /// <summary>Соединение с демоном. Всё, что показывает UI, пришло отсюда.</summary>
    public IIpcClient Client => _client;

    /// <summary>Перекладывание пушей демона в поток UI.</summary>
    public IUiDispatcher Dispatcher { get; } = AvaloniaUiDispatcher.Instance;

    /// <summary>Шов «запусти этот макрос» для редактора.</summary>
    public IMacroLauncher MacroLauncher { get; }

    /// <summary>Приостановка и возобновление глобальных хоткеев демона вокруг ловушки сочетания.</summary>
    public IHotkeySuspension HotkeySuspension { get; }

    /// <summary>
    /// Вопрос «имя занято» — единственное модальное окно панели. Здесь он потому же, почему здесь
    /// две соседки выше: редактор обязан оставаться проверяемым headless, а вопрос человеку —
    /// это шов, а не деталь view-model.
    /// </summary>
    public IMacroNameConflictPrompt NameConflicts { get; }

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
            CreateLogViewModel(),
            CreateSettingsViewModel());

    /// <summary>Собирает view-model окон и прогонов саму по себе (нужно пути дизайнера).</summary>
    public WorkspaceViewModel CreateWorkspaceViewModel() => new(Client, Dispatcher);

    /// <summary>
    /// Собирает view-model редактора макросов, стоящую за режимом «Макросы». Браузер шаблонов
    /// (волна F2) она заводит себе сама: шаблоны принадлежат макросу, а не приложению, и
    /// отдельной фабрики у них здесь больше нет.
    /// </summary>
    public MacroEditorViewModel CreateMacroEditorViewModel() =>
        new(Client, Library, MacroLauncher, HotkeySuspension, Dispatcher, NameConflicts);

    /// <summary>
    /// Собирает view-model ленты журнала, стоящую за режимом «Лог». Своего пути к файлам она,
    /// как и браузер шаблонов, не получает: <c>logs/</c> лежит рядом с демоном, и панель видит
    /// журнал только через <c>SubscribeLog</c>. Собственный файл этого процесса
    /// (<c>logs/smartmacro-ui-*.log</c>) — другая история и в ленту не попадает.
    /// </summary>
    public LogViewModel CreateLogViewModel() => new(Client, Dispatcher);

    /// <summary>
    /// Собирает view-model настроек, стоящую за режимом «Настройки». Своего пути к
    /// <c>settings.json</c> она, как и две соседки выше, не получает: файлом владеет демон, и
    /// панель видит его только через <c>GetSettings</c> / <c>SaveSettings</c>. Путь для кнопки
    /// «Открыть папку» приезжает В ОТВЕТЕ, а не вычисляется здесь, — иначе панель однажды открыла
    /// бы не ту папку, в которой демон на самом деле живёт.
    /// </summary>
    public SettingsViewModel CreateSettingsViewModel() => new(Client, Dispatcher);

    public ValueTask DisposeAsync()
    {
        Library.Dispose();
        return _client.DisposeAsync();
    }
}
