using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SmartMacro.App.ViewModels;

namespace SmartMacro.App;

/// <summary>
/// Окно-оболочка. Всё видимое — это вид режима; этому классу принадлежат ровно три вещи,
/// которых не должно быть у view-model: тик счётчика времени, оконные операции своей строки
/// заголовка и гарантия того, что глобальные хоткеи вернутся до выхода из процесса.
/// </summary>
public partial class MainWindow : Window
{
    // Демон не перерегистрирует хоткеи, когда клиент отваливается, поэтому, выйди мы в момент,
    // когда «Макросы» держат их приостановленными, они останутся мёртвыми до перезапуска
    // демона. Возобновление — это round trip по локальной трубе, который завершается на потоке
    // чтения клиента, так что ограниченная по времени блокировка потока UI не может привести к
    // взаимной блокировке, — а двух секунд named pipe нужно куда меньше.
    private static readonly TimeSpan HotkeyResumeOnExitTimeout = TimeSpan.FromSeconds(2);

    // Время «сколько уже идёт» в полосе прогона и в списке «Прогоны» рисует VM, а тикает оно
    // отсюда: именно то, что DispatcherTimer оставлен в виде, позволяет каждой view-model в
    // этой сборке обходиться без типов Avalonia и потому покрываться модульными тестами.
    private readonly DispatcherTimer _elapsedTimer;

    // Дизайнеру нужен конструктор без параметров; приложение пользуется перегрузкой с (vm).
    public MainWindow()
    {
        InitializeComponent();
        _elapsedTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) => (DataContext as ShellViewModel)?.RefreshElapsed());
        _elapsedTimer.Start();
    }

    public MainWindow(ShellViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private ShellViewModel? Vm => DataContext as ShellViewModel;

    // Закрытие окна завершает процесс панели: трей и движок теперь живут в демоне, так что
    // держать здесь что-то живым в фоне уже незачем.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _elapsedTimer.Stop();
        if (Vm is { } vm)
        {
            // Правки записываются сами через несколько секунд затишья; закрытие окна попадает в
            // эти секунды ровно тогда, когда человек «поправил и пошёл». Дописываем синхронно:
            // запись бандла — это десятки килобайт во временный файл рядом плюс переименование.
            vm.Editor.FlushAutoSave();

            if (vm.HotkeysSuspended)
            {
                // По возможности и с ограничением по времени: лучше двухсекундная заминка на
                // выходе, чем глобальные хоткеи, которые уже не вернутся.
                vm.ResumeHotkeysIfSuspendedAsync().Wait(HotkeyResumeOnExitTimeout);
            }

            vm.Dispose();
        }

        base.OnClosing(e);
    }

    // ---- своя строка заголовка -------------------------------------------------------------

    // ExtendClientAreaToDecorationsHint забрал системный заголовок, так что перетаскивание окна
    // реализуем сами. Только левой кнопкой и только если нажатие не пришлось на одну из кнопок
    // заголовка (они разбираются со своим кликом сами).
    //
    // НЕ в развёрнутом состоянии. Наблюдалось, что BeginMoveDrag на развёрнутом окне с
    // расширенной клиентской областью оставляет окно СКРЫТЫМ (процесс жив, WS_VISIBLE снят,
    // исключения нет) — состояние, из которого приложению уже не выбраться. Собственный жест
    // Windows «потянуть развёрнутое окно, чтобы восстановить его» переизобретать поверх такого
    // не стоит; восстанавливает двойное касание — доступная половина того же взаимодействия.
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button
            || WindowState != WindowState.Normal
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        BeginMoveDrag(e);
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Button)
        {
            return;
        }

        ToggleMaximised();
    }

    private void OnMinimiseClicked(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximiseClicked(object? sender, RoutedEventArgs e) => ToggleMaximised();

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximised() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ---- рейка -------------------------------------------------------------------------------

    // «Настройки» живут вне ListBox рейки (см. разметку), поэтому переход туда — обычный клик, а
    // не смена SelectedItem. IsChecked у кнопки привязан OneWay: состояние решает view-model,
    // иначе повторный клик по уже открытым настройкам их бы «отжал».
    private void OnSettingsClicked(object? sender, RoutedEventArgs e) => Vm?.ShowSettings();

    // ---- полоса прогона ----------------------------------------------------------------------

    private void OnStopPrimaryRunClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { PrimaryRun: { } run } vm)
        {
            _ = vm.Workspace.StopRunAsync(run);
        }
    }

    private void OnStopAllRunsClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            _ = vm.Workspace.StopAllRunsAsync();
        }
    }
}
