using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SmartMacro.App.ViewModels;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.App.Views;

/// <summary>
/// «Окна». Всю работу делает view-model; этот файл — та самая проводка событий, которую в
/// этом коде используют вместо команд.
/// </summary>
public partial class WindowsView : UserControl
{
    // Снять N игровых окон — это секунды, а не миллисекунды: демон каждого клиента будит,
    // фотографирует и снова замораживает. Стандартный таймаут запроса в 10 секунд сработал бы
    // задолго до того, как обход девяти клиентов дойдёт до конца.
    private static readonly TimeSpan DumpCapturesTimeout = TimeSpan.FromMinutes(2);

    public WindowsView() => InitializeComponent();

    private ShellViewModel? Vm => DataContext as ShellViewModel;

    // ---- теги ------------------------------------------------------------------------

    private void OnAddTagClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WindowRowViewModel row })
        {
            _ = row.AddTagAsync();
        }
    }

    private void OnBeginAddTagClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WindowRowViewModel row })
        {
            row.BeginAddTag();
        }
    }

    // Enter в поле тега — быстрый путь: набрать тег и потянуться за мышью девять окон подряд
    // надоедает. Escape закрывает встроенное поле на строке, у которой теги уже есть.
    private void OnTagInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: WindowRowViewModel row })
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                _ = row.AddTagAsync();
                break;

            case Key.Escape:
                e.Handled = true;
                row.CancelAddTag();
                break;

            default:
                break;
        }
    }

    // Встроенное поле появляется только после того, как пользователь его попросил, — значит, к
    // моменту появления оно уже должно быть в фокусе, иначе «+» стоит клик И ещё клик.
    private void OnInlineTagBoxAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.Focus();
        }
    }

    private void OnRemoveTagClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TagChipViewModel chip })
        {
            chip.Remove();
        }
    }

    // ---- действия в шапке -----------------------------------------------------------------

    private void OnIdentifyAllClicked(object? sender, RoutedEventArgs e) => Vm?.IdentifyAll();

    // Диагностика: выгружаем то, каким видит каждое живое окно конвейер vision. Сам обход идёт
    // в демоне (ему нужны дескрипторы окон и OpenCV); здесь только просят его сделать и
    // открывают папку, которая приходит в ответ.
    private async void OnDumpCapturesClicked(object? sender, RoutedEventArgs e)
    {
        if (Program.Services is not { } services)
        {
            return;
        }

        string? folder;
        try
        {
            folder = await services.Client.RequestAsync<string>(
                IpcMessageTypes.DumpCaptures,
                timeout: DumpCapturesTimeout);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось выгрузить отладочные снимки");
            return;
        }

        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        // Открываем папку с отладкой, чтобы пользователь сразу увидел результат.
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            // Папку открываем по возможности; файлы на месте в любом случае.
            Serilog.Log.Debug(ex, "Не удалось открыть папку '{Folder}'", folder);
        }
    }
}
