namespace SmartMacro.Macros.Execution;

/// <summary>
/// Почему обход припаркован. Определяет, какое событие получит панель и как она его нарисует.
///
/// Кнопки названы так, как они подписаны в интерфейсе, и без глифов. Волна D5 от глифов
/// макета (⏸ / ⤼ / ▷|) сознательно отказалась: у U+23F8 выставлено свойство
/// <c>Emoji_Presentation</c>, и он приезжает цветной картинкой мимо <c>Foreground</c> — та же
/// ловушка, в которую D1 попала на U+25B6, а D4 на U+26A0. Ссылаться на них здесь значило бы
/// отправить читателя искать в интерфейсе кнопки, которых там нет.
/// </summary>
public enum DebugPauseReason
{
    /// <summary>Пользователь нажал «Пауза», пока обход был внутри ноды.</summary>
    Requested,

    /// <summary>Отработал ровно один «Шаг».</summary>
    Step,

    /// <summary>«До курсора» дошло до той ноды, в которую целилось.</summary>
    Cursor,

    /// <summary>На ноде стоит точка останова.</summary>
    Breakpoint,
}

/// <summary>
/// Обход, припаркованный у ноды; выдаётся из <see cref="IMacroDebugger.Arm"/>.
///
/// Два вызова, а не один <c>PauseIfNeededAsync</c>, потому что между решением встать на паузу и
/// ожиданием walker обязан ОБЪЯВИТЬ об этой паузе — иначе панель узнаёт о припаркованном обходе
/// только по отсутствию дальнейших событий, а это неотличимо от медленной ноды. Взведение и
/// ожидание при этом остаются атомарными относительно гонки с Resume: затвор существует (и,
/// значит, его можно отпустить) с той секунды, как <see cref="IMacroDebugger.Arm"/> вернул
/// управление.
/// </summary>
public sealed class MacroDebugGate
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal MacroDebugGate(Guid walkId, Guid nodeId, string nodeName, DebugPauseReason reason)
    {
        WalkId = walkId;
        NodeId = nodeId;
        NodeName = nodeName;
        Reason = reason;
    }

    /// <summary>Удерживаемый обход.</summary>
    public Guid WalkId { get; }

    /// <summary>Нода, ПЕРЕД которой обход припаркован. Она ещё не выполнялась.</summary>
    public Guid NodeId { get; }

    /// <summary>Её подпись — то, что уедет в событие паузы и в лог.</summary>
    public string NodeName { get; }

    /// <summary>Почему.</summary>
    public DebugPauseReason Reason { get; }

    /// <summary>
    /// Завершается, когда обход отпускают. Уважает <paramref name="cancellationToken"/>, так что
    /// «■ Стоп» (и выключение демона, которое отменяет каждый прогон) распускает обход, стоящий
    /// на паузе, а не оставляет его заклиненным: возникающее
    /// <see cref="OperationCanceledException"/> — это обычный путь отмены, и обход заканчивается
    /// как <c>Cancelled</c>.
    /// </summary>
    public Task WaitAsync(CancellationToken cancellationToken) => _released.Task.WaitAsync(cancellationToken);

    internal void Release() => _released.TrySetResult();
}

/// <summary>
/// Канал управления для walker'а, близнец докладного канала
/// <see cref="IMacroRunObserver"/>: тот говорит, что произошло, этот решает, можно ли обходу
/// идти дальше.
///
/// <b><see cref="IsActive"/> живёт по той же дисциплине, что и <c>IsEnabled</c>.</b> Walker
/// читает его один раз на ноду, прежде чем тронуть что бы то ни было ещё, и демон без
/// подключённой панели платит за это одним volatile-чтением. Свойство обязано быть честным:
/// константный <c>true</c> поставил бы поиск по словарю и блокировку на путь того, что
/// управляет живой игрой.
///
/// <b>Вызывается с потоков движка, сразу с нескольких</b> — разветвление обходит N графов
/// параллельно, и у каждого свой затвор. Реализации обязаны быть потокобезопасными.
/// </summary>
public interface IMacroDebugger
{
    /// <summary>Подключён ли вообще хоть один отладчик. Проверяется на каждой ноде, а значит, обязано быть дёшево.</summary>
    bool IsActive { get; }

    /// <summary>
    /// Решает, встанет ли обход перед <paramref name="nodeId"/>, и, если да, взводит затвор, на
    /// котором ему предстоит ждать. <c>null</c> = идти дальше.
    /// </summary>
    /// <param name="walkId">Обход в том виде, в каком о нём доложили в <see cref="IMacroRunObserver.WalkStarted"/>.</param>
    /// <param name="macroName">Обходимый граф — точки останова ключуются парой (макрос, нода).</param>
    /// <param name="nodeId">Нода, которая вот-вот выполнится.</param>
    /// <param name="nodeName">Её подпись — сессия несёт её дальше в событие паузы и в лог.</param>
    MacroDebugGate? Arm(Guid walkId, string macroName, Guid nodeId, string nodeName);

    /// <summary>
    /// Забывает затвор — и когда его отпустили штатно, и когда его бросили из-за отмены. Walker
    /// вызывает это в <c>finally</c>; без этого отменённый обход оставил бы сессию в убеждении,
    /// что он всё ещё припаркован.
    /// </summary>
    void Disarm(MacroDebugGate gate);

    /// <summary>
    /// Стирает всякий след завершившегося обхода. Вызывается по разу на обход с пути выхода из
    /// исполнителя, так что сессия, повидавшая десять тысяч обходов, не держит состояния ни по
    /// одному из них.
    /// </summary>
    void WalkFinished(Guid walkId);
}
