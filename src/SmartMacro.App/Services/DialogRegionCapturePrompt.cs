using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Serilog;
using SmartMacro.App.Views;

namespace SmartMacro.App.Services;

/// <summary>
/// Боевая реализация выбора области: модальное окно над главным.
///
/// <b>Владельца ищем у приложения, а не получаем в конструкторе</b> — ровно та же причина, что у
/// <see cref="DialogMacroNameConflictPrompt"/>: панель собирает свои объекты в <c>AppServices</c>
/// ДО того, как появляется окно (view-model'и оно и создаёт), так что ссылку пришлось бы либо
/// протаскивать обратным вызовом, либо откладывать сборку редактора.
///
/// Служба захвата, наоборот, приходит конструктором: она нужна не «в момент вопроса», а на всё
/// время жизни панели, и живёт рядом с соединением.
///
/// <b>Окна нет — значит «отменили».</b> Такое возможно только на пути дизайнера и при закрытии
/// процесса.
/// </summary>
public sealed class DialogRegionCapturePrompt : IRegionCapturePrompt
{
    private readonly IWindowCaptureService _capture;

    public DialogRegionCapturePrompt(IWindowCaptureService capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        _capture = capture;
    }

    public async Task<RegionCaptureResult?> AskAsync(RegionCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Owner() is not { } owner)
        {
            Log.Warning("Некому показать выбор области для «{Node}» — главного окна нет", request.NodeName);
            return null;
        }

        // Зовут из обработчика кнопки, то есть уже в потоке UI; проверка оставлена по той же
        // причине, что и у соседа: цена ошибки здесь — исключение посреди правки ноды.
        return Dispatcher.UIThread.CheckAccess()
            ? await Ask(owner, request)
            : await Dispatcher.UIThread.InvokeAsync(() => Ask(owner, request));
    }

    private Task<RegionCaptureResult?> Ask(Window owner, RegionCaptureRequest request) =>
        new RegionCaptureDialog(request, _capture).AskAsync(owner);

    private static Window? Owner() =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
        ?.MainWindow;
}
