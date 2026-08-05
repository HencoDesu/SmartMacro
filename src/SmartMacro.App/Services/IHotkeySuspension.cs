using Serilog;
using SmartMacro.App.Ipc;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.App.Services;

/// <summary>
/// Позволяет панели выключать глобальные хоткеи демона на всё время, пока редактор макросов на
/// экране. С волны D2 это означает «пока выбран режим „Макросы“» — скобку ставит
/// <c>ShellViewModel</c>, а раньше ею было открытие и закрытие диалога.
///
/// Win32-функция <c>RegisterHotKey</c> проглатывает нажатия уже зарегистрированного сочетания:
/// окно-владелец получает WM_HOTKEY, и клавишу больше не видит вообще никто. Владелец теперь
/// другой ПРОЦЕСС — демон, — но задачу это не меняет: пока на экране ловушка хоткея-триггера, до
/// неё бы никогда не доходили ровно те сочетания, которые пользователь скорее всего и хочет
/// переназначить (те, что уже привязаны к макросу). Приостановка на время работы редактора
/// по-прежнему единственное лекарство.
///
/// Приостановка — АРЕНДА СОЕДИНЕНИЯ: демон отдаёт её сам, когда труба рвётся, тем же
/// <c>finally</c>, каким снимает две подписки. До ревью её держал глобальный флаг, и панель,
/// снятая диспетчером задач в режиме «Макросы», оставляла демон с нулём зарегистрированных
/// аккордов навсегда. Чистый путь через <see cref="ResumeAsync"/> всё равно правильный и
/// остаётся: он же перечитывает <c>GetHotkeyFailures</c>, а на аварийный выход полагаться
/// незачем.
///
/// Остаётся интерфейсом, чтобы VM редактора можно было тестировать headless, без демона.
/// </summary>
public interface IHotkeySuspension
{
    /// <summary>Снимает регистрацию со всех сочетаний. Идемпотентно.</summary>
    Task SuspendAsync(CancellationToken cancellationToken = default);

    /// <summary>Регистрирует заново по текущей библиотеке макросов. Идемпотентно.</summary>
    Task ResumeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Переправляет приостановку и возобновление демону по управляющей трубе.
///
/// Отказы пишутся в лог и глушатся: ловушка, которой не удалось приостановить хоткеи, всё ещё
/// работает для любого не занятого сочетания, тогда как исключение, вылетевшее из переключения
/// режима, утащило бы за собой всю оболочку.
/// </summary>
public sealed class IpcHotkeySuspension : IHotkeySuspension
{
    private readonly IIpcClient _client;

    public IpcHotkeySuspension(IIpcClient client) => _client = client;

    public Task SuspendAsync(CancellationToken cancellationToken = default) =>
        SendAsync(IpcMessageTypes.SuspendHotkeys, cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken = default) =>
        SendAsync(IpcMessageTypes.ResumeHotkeys, cancellationToken);

    private async Task SendAsync(string type, CancellationToken cancellationToken)
    {
        try
        {
            await _client.RequestAsync(type, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            Log.Warning(ex, "Не удалось выполнить '{Request}'", type);
        }
    }
}
