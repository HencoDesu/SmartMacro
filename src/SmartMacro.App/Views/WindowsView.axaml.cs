using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SmartMacro.App.ViewModels;

namespace SmartMacro.App.Views;

/// <summary>
/// «Окна». Всю работу делает view-model; этот файл — та самая проводка событий, которую в
/// этом коде используют вместо команд.
///
/// Действий над всем списком здесь больше нет — только над строкой. «Опознать все» и «Дамп
/// захватов» ушли вместе со своей причиной: опознание давно не встроено, это обычный макрос,
/// а подбор регионов зрения — задача редактора, а не шапки списка окон.
/// </summary>
public partial class WindowsView : UserControl
{
    public WindowsView() => InitializeComponent();

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
}
