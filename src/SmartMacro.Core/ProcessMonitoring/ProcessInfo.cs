namespace SmartMacro.ProcessMonitoring;

// Snapshot taken at the moment of a Poll() — MainWindowHandle may be IntPtr.Zero if the
// process has just started and the main window isn't created yet. Consumers that need a
// real hwnd should re-query (e.g., via Process.GetProcessById) until it becomes non-zero.
public readonly record struct ProcessInfo(int Pid, string ProcessName, IntPtr MainWindowHandle);
