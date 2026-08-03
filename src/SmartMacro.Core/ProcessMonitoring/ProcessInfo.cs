namespace SmartMacro.ProcessMonitoring;

// Снимок на момент вызова Poll() — MainWindowHandle может быть IntPtr.Zero, если процесс только
// что стартовал и главное окно ещё не создано. Потребителям, которым нужен настоящий hwnd,
// следует перезапрашивать (например, через Process.GetProcessById), пока он не станет ненулевым.
public readonly record struct ProcessInfo(int Pid, string ProcessName, IntPtr MainWindowHandle);
