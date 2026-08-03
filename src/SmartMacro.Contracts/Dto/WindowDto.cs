namespace SmartMacro.Contracts.Dto;

/// <summary>
/// Проводная форма одного отслеживаемого окна. Зеркало <c>ManagedWindowInfo</c> из Core за
/// вычетом того, что плохо переживает границу процессов: хендл — <see cref="long"/>, а не
/// <c>IntPtr</c> (стабильная ширина на проводе, никакой указательной семантики), а набор
/// тегов — упорядоченный список, а не множество (множеств в JSON нет).
/// </summary>
/// <param name="Hwnd">Нативный хендл окна. Ключ идентичности — интерфейс возвращает его обратно в AddTag/RemoveTag.</param>
/// <param name="ProcessName">Имя процесса ОС, которому принадлежит окно.</param>
/// <param name="Tags">Теги окна на момент снятия снимка.</param>
public sealed record WindowDto(long Hwnd, string ProcessName, IReadOnlyList<string> Tags);
