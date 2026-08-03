namespace SmartMacro.Config;

/// <summary>
/// Профиль активации на один процесс. Привязывается к элементу массива "ProcessProfiles" в
/// appsettings.json. Пришёл на смену единственной глобальной настройке активации
/// Agent:GameProcessName + Input:Activating — пляска с побудкой через WM_ACTIVATEAPP есть
/// причуда Perfect World, поэтому она живёт при том процессе, которому принадлежит.
/// ProcessMonitor следит за объединением имён процессов из всех профилей; GameWindowFactory
/// подбирает подходящий профиль каждому окну.
/// </summary>
public sealed class ProcessProfile
{
    /// <summary>Имя процесса на уровне ОС (без расширения), например "elementclient_64".</summary>
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>
    /// Магический lParam в паре с WM_ACTIVATEAPP, которым замороженного клиента будят перед
    /// вводом. <c>null</c> = простой ввод: сигнал побудки не отправляется и деактивации следом
    /// не будет.
    /// </summary>
    public uint? ActivationLParam { get; init; }

    /// <summary>Пауза после сигнала побудки перед отправкой ввода (время на разморозку движка).</summary>
    public int SettleDelayMs { get; init; }

    /// <summary>Пауза перед сигналом деактивации, чтобы насос сообщений цели успел разобрать очередь ввода.</summary>
    public int DeactivationDelayMs { get; init; }

    /// <summary>
    /// Запасной профиль для окон, чей процесс не описан в конфиге: простой ввод, без задержек,
    /// без пляски с побудкой.
    /// </summary>
    public static ProcessProfile Inert { get; } = new();
}
