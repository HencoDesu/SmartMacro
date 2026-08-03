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

    /// <summary>
    /// Как часто <c>WindowLifetimeMonitor</c> обходит реестр окон и проверяет, живы ли они ещё.
    /// Имя досталось в наследство от CharacterAgent'а, который до W0.4 крутил такой опрос сам,
    /// по экземпляру на клиента; переименовывать его — ломать секцию "Agent" в appsettings.json,
    /// так что это отдельная волна.
    /// </summary>
    public int AgentPollIntervalSeconds { get; init; } = 2;
}
