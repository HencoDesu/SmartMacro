using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SmartMacro.App.ViewModels;

namespace SmartMacro.App.Views;

/// <summary>
/// «Лог» — лента журнала демона.
///
/// Code-behind делает ровно две вещи, обе про поведение вида, а не про данные: доводит
/// автопрокрутку и раскрывает стек по шеврону.
/// </summary>
public partial class LogView : UserControl
{
    private LogViewModel? _model;
    private bool _scrollQueued;

    public LogView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_model is not null)
        {
            _model.Rows.CollectionChanged -= OnRowsChanged;
        }

        _model = DataContext as LogViewModel;

        if (_model is not null)
        {
            _model.Rows.CollectionChanged += OnRowsChanged;
        }
    }

    /// <summary>
    /// Прокрутка КОАЛЕСЦИРУЕТСЯ. Пачка от демона приезжает четырьмя сотнями отдельных Add, и
    /// <c>ScrollIntoView</c> на каждый из них — это четыреста проходов раскладки на одном
    /// конверте. Флаг сводит их к одному вызову, отложенному до момента, когда список уже
    /// перестроился.
    /// </summary>
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_model is not { AutoScroll: true } || e.Action == NotifyCollectionChangedAction.Remove || _scrollQueued)
        {
            return;
        }

        _scrollQueued = true;
        Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Background);
    }

    private void ScrollToEnd()
    {
        _scrollQueued = false;
        // Условие перечитывается ЗДЕСЬ, а не только при постановке: между ней и этим вызовом
        // пользователь мог снять галочку — и тогда прокрутка вырвала бы у него из-под глаз ту
        // строку, ради чтения которой он её и снял.
        if (_model is { AutoScroll: true, Rows.Count: > 0 })
        {
            LogList.ScrollIntoView(_model.Rows[^1]);
        }
    }

    private void OnClearClicked(object? sender, RoutedEventArgs e) => _model?.Clear();

    private void OnToggleExceptionClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: LogRowViewModel row })
        {
            row.IsExpanded = !row.IsExpanded;
        }
    }
}
