namespace SmartMacro.Contracts.Dto;

/// <summary>
/// Проводная форма одного идущего прогона макроса. Зеркало <c>MacroRunSnapshot</c> из Core;
/// <see cref="StartedUtc"/> — это <see cref="DateTimeOffset"/>, чтобы смещение пережило JSON,
/// а не зависело от того, угадает ли читатель <c>DateTimeKind</c>.
/// </summary>
/// <param name="RunId">Уникальный id прогона. Возвращается обратно в <c>StopMacro</c>.</param>
/// <param name="MacroName">Имя запущенного макроса.</param>
/// <param name="StartedUtc">Момент начала прогона (UTC).</param>
/// <param name="CurrentNodeName">
/// Подпись ноды, в которую walker вошёл последней; <c>null</c> до первой ноды. Именно ПОДПИСЬ, а
/// не id: единственный потребитель — колонка «где сейчас» в режиме «Прогоны», и адресовать эту
/// ноду ей незачем.
/// </param>
public sealed record RunningMacroDto(Guid RunId, string MacroName, DateTimeOffset StartedUtc, string? CurrentNodeName);
