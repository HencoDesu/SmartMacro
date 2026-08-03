namespace SmartMacro.Config;

/// <summary>
/// Инфраструктурные настройки времени выполнения, привязанные к секции "Agent" в
/// appsettings.json.
///
/// Намеренно крошечные: выбор процессов живёт в <c>ProcessProfiles</c>, идентичность — в тегах
/// <c>WindowRegistry</c>, а каждая координата, область, шаблон, привязка клавиши и таймаут,
/// которые раньше лежали здесь, теперь живут внутри нод макроса, где пользователь правит их, не
/// трогая конфиг. Остался только темп опроса.
/// </summary>
public sealed class AgentOptions
{
    /// <summary>
    /// Как часто ProcessMonitor опрашивает список процессов ОС, чтобы сравнить его с прошлым
    /// снимком.
    /// </summary>
    public int ProcessPollIntervalSeconds { get; init; } = 1;

    /// <summary>Как часто каждый агент проверяет, живо ли ещё его окно.</summary>
    public int AgentPollIntervalSeconds { get; init; } = 2;
}
