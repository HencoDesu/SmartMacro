namespace SmartMacro.Contracts.Ipc;

/// <summary>
/// Адрес управляющей точки демона. Живёт в Contracts, потому что нужен обоим концам, а
/// другой общей сборки у них нет: <c>IpcServer</c> демона на нём слушает, клиент интерфейса
/// в него звонит. Продублировать литерал на любой из сторон — то самое расхождение, которое
/// провалится в рантайме молча: интерфейс, подключившийся в никуда, выглядит ровно как
/// незапущенный демон.
/// </summary>
public static class IpcPipe
{
    /// <summary>Имя канала. Полный путь: <c>\\.\pipe\smartmacro-control</c>.</summary>
    public const string Name = "smartmacro-control";

    /// <summary>Ограничение со стороны сервера на число одновременных клиентов (интерфейс/CLI).</summary>
    public const int MaxServerInstances = 8;
}
