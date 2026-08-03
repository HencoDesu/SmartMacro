namespace SmartMacro.Windows;

/// <summary>
/// Неизменяемый снимок одного окна, за которым следит <see cref="WindowRegistry"/>: нативный
/// дескриптор, имя владеющего процесса и набор тегов на момент снятия снимка. Теги — свободные
/// строки с учётом регистра, живут ровно столько, сколько живёт окно (hwnd эфемерны, так что
/// ничего отсюда не сохраняется на диск).
/// </summary>
/// <param name="Hwnd">Нативный дескриптор окна. Ключ идентичности внутри реестра.</param>
/// <param name="ProcessName">Имя процесса ОС, которому принадлежит окно (как его сообщил ProcessMonitor).</param>
/// <param name="Tags">Снимок тегов окна. Не меняется — последующие правки в реестре порождают новые снимки.</param>
public sealed record ManagedWindowInfo(IntPtr Hwnd, string ProcessName, IReadOnlySet<string> Tags);
