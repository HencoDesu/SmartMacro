using Microsoft.Extensions.Logging;
using SmartMacro.GameWindows;
using SmartMacro.Native;

namespace SmartMacro.Input;

// Оборачивает жизненный цикл активации вокруг каждой отправки ввода в игровое окно. Слой
// примитивов макроса делегирует сюда любое нажатие клавиши и любой клик — так walker занят
// обходом графа, а не заботами вида «разбуди окно, отправь, дай очереди слиться, уложи обратно
// спать».
//
// Все методы устроены одинаково:
//   1. ActivateAsync (разбудить замороженное фоновое окно)
//   2. отправить ввод примитивами через PostMessage
//   3. DeactivateAsync (пауза на слив + уложить окно обратно спать, если оно не на переднем
//      плане)
//
// Безопасно к исключениям: try/finally гарантирует, что Deactivate отработает, даже если
// внутренняя отправка бросила, а сбои отправки пишутся в лог, а не пробрасываются — одно
// недостижимое окно не должно обрывать макрос, который законно нацелился ещё на восемь.
//
// Логирование выстроено вокруг метки действия, которую передаёт вызывающий («Key(F8)»,
// «Click»), чтобы оператор мог грепать строки лога по конкретному действию.
public sealed partial class AgentInputDispatcher
{
    private readonly ILogger<AgentInputDispatcher> _logger;

    public AgentInputDispatcher(ILogger<AgentInputDispatcher> logger)
    {
        _logger = logger;
    }

    /// <summary>Отправка одной клавиши, обёрнутая в один цикл активации.</summary>
    /// <param name="window">Целевое игровое окно.</param>
    /// <param name="key">Отправляемая клавиша.</param>
    /// <param name="agentName">Метка окна в сообщениях лога, чтобы оператор мог грепать активность по окну.</param>
    // Здесь был ещё параметр actionName — «смысловая метка для лога». Единственный вызывающий
    // передавал в него $"Key({key})", то есть пересказ соседнего аргумента, и каждая строка
    // лога выходила вида «Отправляем Key(F8)=F8 на 'Лучник'». Метка осталась от
    // доW0.2b-конвейера, где через неё ехало имя команды («Иммунитет»), а не клавиша; команд
    // больше нет, всё стало макросами. Структурным полем должна быть сама клавиша, а не
    // отформатированная строка вокруг неё.
    public async Task FireKeyAsync(IGameWindow window, VirtualKey key, string agentName)
    {
        try
        {
            LogActionFiring(key, agentName);
            await window.ActivateAsync().ConfigureAwait(false);
            try
            {
                await window.PressKeyAsync(key).ConfigureAwait(false);
            }
            finally
            {
                await window.DeactivateAsync().ConfigureAwait(false);
            }

            LogActionFired(key, agentName);
        }
        catch (Exception ex)
        {
            LogSendKeyFailed(ex, key, agentName);
        }
    }

    /// <summary>
    /// Одиночный или двойной клик левой кнопкой в точку в клиентских координатах. В сборках с
    /// несколькими клиентами одна и та же точка переиспользуется по клиентам одинакового
    /// размера — на этом и держится макрос, рассылающий клик по позиции курсора.
    /// </summary>
    public async Task FireClickAsync(IGameWindow window, ScreenPoint point, bool doubleClick, string agentName)
    {
        try
        {
            await window.ActivateAsync().ConfigureAwait(false);
            try
            {
                if (doubleClick)
                {
                    await window.DoubleClickAsync(point).ConfigureAwait(false);
                }
                else
                {
                    await window.ClickAsync(point).ConfigureAwait(false);
                }
            }
            finally
            {
                await window.DeactivateAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LogSendClickFailed(ex, point, doubleClick, agentName);
        }
    }

    #region Logging

    [LoggerMessage(LogLevel.Information, "Отправляем клавишу {Key} на '{Name}'")]
    partial void LogActionFiring(VirtualKey key, string name);

    [LoggerMessage(LogLevel.Information, "Отправлено {Key} на '{Name}' — OK")]
    partial void LogActionFired(VirtualKey key, string name);

    [LoggerMessage(LogLevel.Error, "PressKeyAsync({Key}) не удался на '{Name}'")]
    partial void LogSendKeyFailed(Exception ex, VirtualKey key, string name);

    [LoggerMessage(LogLevel.Error, "Клик {Point} двойной={DoubleClick} не удался на '{Name}'")]
    partial void LogSendClickFailed(Exception ex, ScreenPoint point, bool doubleClick, string name);

    #endregion
}
