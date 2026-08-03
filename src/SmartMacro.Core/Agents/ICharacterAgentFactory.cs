using System.Threading.Channels;
using SmartMacro.ProcessMonitoring;

namespace SmartMacro.Agents;

// Создаёт CharacterAgent, который владеет временем жизни окна игрового клиента. Агенты
// рождаются без тегов: опознание теперь работа макроса, так что моделировать здесь состояние
// «пока неизвестно» незачем — агент регистрирует своё окно, а теги (если они будут) приезжают
// позже через WindowRegistry.
public interface ICharacterAgentFactory
{
    /// <summary>
    /// Создаёт свежего агента, привязанного к дескриптору окна из <paramref name="info"/>, и
    /// направляет его исходящие сообщения во входящую очередь оркестратора.
    /// </summary>
    /// <exception cref="InvalidOperationException">Бросается, когда у главного окна процесса нулевая клиентская область (обычно это процесс лаунчера без пригодной игровой поверхности).</exception>
    Task<CharacterAgent> CreateAsync(
        ProcessInfo info,
        ChannelWriter<AgentMessage> outbox,
        CancellationToken cancellationToken = default);
}
