using Serilog;
using SmartMacro.App.Ipc;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.App.Services;

/// <summary>
/// Запускает макрос из UI — ручной эквивалент нажатия его хоткея (контекстного окна нет; граф
/// маршрутизируется тег-селектором, а переменную cursor демон засевает сам). Обоснование
/// тестируемости то же, что у <see cref="IHotkeySuspension"/>: для юнит-теста VM редактора
/// живой демон требоваться не должен.
/// </summary>
public interface IMacroLauncher
{
    /// <summary>Запуск <paramref name="macroName"/> без ожидания результата. Отказы уходят в лог, наружу не летят.</summary>
    void RunMacro(string macroName);
}

/// <summary>
/// Отправляет <see cref="IpcMessageTypes.RunMacro"/> и забывает о нём.
///
/// Отправить и забыть — это верность протоколу, а не срезанный угол: ответ демона означает
/// «запустил», но никогда «закончил» — макрос может работать часами, — так что ждать вызывающему
/// нечего. А вот неизвестное имя макроса запрос всё-таки проваливает, и это попадает в лог.
/// </summary>
public sealed class IpcMacroLauncher : IMacroLauncher
{
    private readonly IIpcClient _client;

    public IpcMacroLauncher(IIpcClient client) => _client = client;

    public void RunMacro(string macroName) => _ = RunAsync(macroName);

    private async Task RunAsync(string macroName)
    {
        try
        {
            await _client.RequestAsync(IpcMessageTypes.RunMacro, new RunMacroRequest(macroName)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            Log.Warning(ex, "Не удалось запустить макрос '{Macro}'", macroName);
        }
    }
}
