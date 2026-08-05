using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SmartMacro.App.Services;

namespace SmartMacro.App.Views;

/// <summary>
/// Окно вопроса «имя занято».
///
/// Кода здесь ровно столько, сколько требует окно без украшений ОС: перетаскивание за свою
/// строку заголовка и три кнопки. Ни грамма знания о макросах — оно всё в view-model редактора, а
/// формулировки в <see cref="MacroNameConflict"/>.
/// </summary>
public partial class MacroNameConflictDialog : Window
{
    /// <summary>
    /// Ответ. ПОЛЕ С УМОЛЧАНИЕМ, а не результат <c>ShowDialog&lt;T&gt;</c>: закрыть окно можно
    /// крестиком, Esc, Alt+F4 и системой, и все эти пути обязаны означать «Отмена». Полагаться
    /// на <c>default(T)</c> значило бы поставить сохранность чужого макроса в зависимость от
    /// порядка членов перечисления.
    /// </summary>
    private MacroNameConflictChoice _choice = MacroNameConflictChoice.Cancel;

    // Дизайнеру нужен конструктор без параметров; приложение пользуется перегрузкой с вопросом.
    public MacroNameConflictDialog()
    {
        InitializeComponent();
    }

    public MacroNameConflictDialog(MacroNameConflict conflict) : this()
    {
        DataContext = conflict;
    }

    /// <summary>Показывает вопрос модально над <paramref name="owner"/> и ждёт ответа.</summary>
    public async Task<MacroNameConflictChoice> AskAsync(Window owner)
    {
        await ShowDialog(owner);
        return _choice;
    }

    /// <summary>
    /// Фокус на «Отмене»: пробел и Enter в только что открывшемся окне обязаны не навредить.
    ///
    /// Две подробности, обе увидены на живой панели, обе невидимы сборке и тестам.
    /// <c>NavigationMethod.Tab</c>, а не голый <c>Focus()</c>: акцентное кольцо в 2px Avalonia
    /// рисует ТОЛЬКО для клавиатурного и направленного фокуса, а без кольца ничто не говорит,
    /// что сделает Enter. И <c>Post</c>, а не вызов прямо здесь: активация окна происходит ПОСЛЕ
    /// <c>OnOpened</c> и гасит признак «фокус пришёл с клавиатуры» — кнопка фокус получала (Tab
    /// уводил на следующую), а кольца не было.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(
            () => CancelButton.Focus(NavigationMethod.Tab),
            DispatcherPriority.Loaded);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) =>
        Answer(MacroNameConflictChoice.Cancel);

    private void OnFreeNameClicked(object? sender, RoutedEventArgs e) =>
        Answer(MacroNameConflictChoice.FreeName);

    private void OnReplaceClicked(object? sender, RoutedEventArgs e) =>
        Answer(MacroNameConflictChoice.Replace);

    private void Answer(MacroNameConflictChoice choice)
    {
        _choice = choice;
        Close();
    }
}
