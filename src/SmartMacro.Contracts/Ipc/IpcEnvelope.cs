using System.Text.Json;

namespace SmartMacro.Contracts.Ipc;

/// <summary>
/// Вызов «клиент → демон». Один JSON-объект в строке (JSON Lines) в управляющем канале.
///
/// <see cref="Payload"/> остаётся сырым <see cref="JsonElement"/>, чтобы конверт читался без
/// знания о самом сообщении: диспетчер разбирает <see cref="Type"/> и только потом
/// материализует типизированную нагрузку (см. <see cref="IpcJson.Read{T}"/>). Поэтому
/// незнакомый <see cref="Type"/> прекрасно десериализуется — отвергнуть его решает диспетчер,
/// а не парсер.
/// </summary>
/// <param name="Id">Идентификатор для корреляции, уникальный в пределах соединения. Возвращается обратно в <see cref="IpcResponse.Id"/>.</param>
/// <param name="Type">Одна из констант-запросов <see cref="IpcMessageTypes"/>.</param>
/// <param name="Payload">Аргументы запроса или <c>null</c> у запросов без аргументов.</param>
public sealed record IpcRequest(int Id, string Type, JsonElement? Payload = null);

/// <summary>
/// Ответ демона ровно на один <see cref="IpcRequest"/>. <see cref="Ok"/> = <c>false</c> ⇒
/// заполнен <see cref="Error"/>, а <see cref="Payload"/> бессмыслен; <see cref="Ok"/> =
/// <c>true</c> с нагрузкой <c>null</c> — обычная форма ответа на запросы вида «просто сделай».
/// </summary>
/// <param name="Id">Идентификатор корреляции, скопированный из запроса.</param>
/// <param name="Ok">Успешен ли запрос.</param>
/// <param name="Payload">Данные результата, форма — по <see cref="IpcMessageTypes"/>; <c>null</c> у ответов «всё в порядке» без данных.</param>
/// <param name="Error">Описание неудачи, когда <paramref name="Ok"/> = <c>false</c>.</param>
public sealed record IpcResponse(int Id, bool Ok, JsonElement? Payload = null, string? Error = null);

/// <summary>
/// Незапрошенный пуш «демон → клиент». Без корреляции (id нет) — события рассылаются всем
/// подключённым клиентам.
/// </summary>
/// <param name="Type">Одна из констант-событий <see cref="IpcMessageTypes"/>.</param>
/// <param name="Payload">Данные события или <c>null</c> у событий вида «что-то изменилось, перечитай».</param>
public sealed record IpcEvent(string Type, JsonElement? Payload = null);
