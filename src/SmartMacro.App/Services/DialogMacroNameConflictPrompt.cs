using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Serilog;
using SmartMacro.App.Views;

namespace SmartMacro.App.Services;

/// <summary>
/// Боевая реализация вопроса о занятом имени: модальное окно над главным.
///
/// <b>Владельца ищем у приложения, а не получаем в конструкторе</b>, и это не лень. Панель
/// собирает свои объекты в <c>AppServices</c> ДО того, как появляется окно (view-model'и оно и
/// создаёт), так что ссылку на окно пришлось бы либо протаскивать обратным вызовом, либо
/// откладывать сборку редактора. Обе цены выше, чем одно обращение к
/// <c>ApplicationLifetime.MainWindow</c> в тот момент, когда вопрос уже задан, — то есть когда
/// окно заведомо есть.
///
/// <b>Окна нет — значит «Отмена».</b> Такое возможно только на пути дизайнера и при закрытии
/// процесса; молча заменить чужой макрос, не сумев спросить, — единственный по-настоящему
/// неверный исход.
/// </summary>
public sealed class DialogMacroNameConflictPrompt : IMacroNameConflictPrompt
{
    public async Task<MacroNameConflictChoice> AskAsync(MacroNameConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);

        if (Owner() is not { } owner)
        {
            Log.Warning(
                "Некому задать вопрос о занятом имени «{Macro}» — главного окна нет; считаем отменой",
                conflict.Name);
            return MacroNameConflictChoice.Cancel;
        }

        // Вопрос задаётся из обработчика кнопки, то есть уже в потоке UI; проверка оставлена
        // потому, что цена ошибки здесь — исключение в середине сохранения, а не кривой пиксель.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return await Dispatcher.UIThread.InvokeAsync(() => Ask(owner, conflict));
        }

        return await Ask(owner, conflict);
    }

    private static Task<MacroNameConflictChoice> Ask(Window owner, MacroNameConflict conflict) =>
        new MacroNameConflictDialog(conflict).AskAsync(owner);

    private static Window? Owner() =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
        ?.MainWindow;
}
