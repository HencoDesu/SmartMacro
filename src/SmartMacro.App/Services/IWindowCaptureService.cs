using System.Runtime.Versioning;
using Serilog;
using SmartMacro.App.Ipc;
using SmartMacro.Contracts.Ipc;
using SmartMacro.Native.Window;

namespace SmartMacro.App.Services;

/// <summary>
/// Свежий кадр игрового окна — для диалога выбора области.
///
/// <b>Кадр идёт тем же путём, что и сопоставление, и в этом весь смысл затеи.</b> Шаблон,
/// вырезанный из «Win+Shift+S», отличается от того, что увидит зрение: у композитора рабочего
/// стола своя цветокоррекция, экран мог быть масштабирован, в кадр попадает курсор. Здесь же
/// <c>PrintWindow</c> с теми же флагами (<c>PW_CLIENTONLY | PW_RENDERFULLCONTENT</c>) и по
/// разбуженному окну — то есть ровно то, что через секунду будет сравнивать
/// <c>TM_CCOEFF_NORMED</c>.
///
/// Оставлен интерфейсом, чтобы диалог и его view-model гонялись без демона и без живой игры.
/// </summary>
public interface IWindowCaptureService
{
    /// <summary>
    /// Будит окно, снимает его клиентскую область и замораживает обратно.
    /// </summary>
    /// <param name="hwnd">Окно, как оно приходит в <c>WindowDto.Hwnd</c>.</param>
    /// <param name="cancellationToken">Отмена ожидания побудки; аренда всё равно будет отпущена.</param>
    /// <returns>PNG клиентской области.</returns>
    /// <exception cref="IpcRequestException">Демон не знает такого окна либо соединения нет.</exception>
    /// <exception cref="InvalidOperationException">Окно свёрнуто (нулевая клиентская область) либо <c>PrintWindow</c> отказал.</exception>
    Task<byte[]> CaptureAsync(long hwnd, CancellationToken cancellationToken = default);
}

/// <summary>
/// Боевая реализация: <b>будит демон, снимает панель</b>.
///
/// Разделение не произвольное, и обе половины стоят там, где обязаны.
/// <list type="bullet">
///   <item><b>Снимает панель.</b> <c>PrintWindow</c> живёт в <c>Native</c>, на который панель и
///     так ссылается, так что несущий запрет разделения (в панели нет ни <c>Core</c>, ни OpenCV)
///     не нарушен. Взамен кадр 3840×2160 — это около десяти мегабайт PNG — не покидает памяти
///     панели: ни конверта в трубе (очередь соединения в 256 событий такое не переживёт), ни
///     временного файла.</item>
///   <item><b>Будит демон.</b> Скобка пробуждения — это <c>WindowHookScope</c> со счётчиком
///     областей на окно и цепочкой переходов; второй её экземпляр в другом процессе не смог бы ни
///     дождаться чужой паузы устаканивания, ни удержать окно от заморозки посреди тика зрения
///     идущего прогона. И у скобки уже находили отсутствующий <c>try/finally</c> — заводить ей
///     двойника значит заводить второе место, где его снова однажды не окажется.</item>
/// </list>
///
/// <b>Отдача аренды стоит в <c>finally</c>, и это здесь не гигиена, а половина механизма.</b>
/// Вторая половина — на стороне демона: аренда привязана к СОЕДИНЕНИЮ и отпускается на разрыве,
/// потому что панель уходит не только штатно (её снимают диспетчером задач, и её же выбрасывает
/// сам демон за неразобранную очередь событий). Без обеих половин единственным лекарством от
/// оставшегося разбуженным клиента PW была бы смена фокуса пользователем.
///
/// ⚠️ <b>UIPI.</b> <c>PrintWindow</c> в окно, запущенное с повышением, из процесса без повышения
/// возвращает ЧЁРНЫЙ кадр — молча, без ошибки. В поставке всё сходится (оба exe несут
/// <c>requireAdministrator</c>), а вот запуск через <c>dotnet SmartMacro.dll</c> из обычной
/// оболочки даст именно этот случай: манифест привязан к apphost, а не к сборке.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IpcWindowCaptureService : IWindowCaptureService
{
    private readonly IIpcClient _client;

    public IpcWindowCaptureService(IIpcClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public async Task<byte[]> CaptureAsync(long hwnd, CancellationToken cancellationToken = default)
    {
        var payload = new CaptureHookRequest(hwnd);

        // Ответ означает «окно разбужено и устаканилось»: паузу ProcessHookSettings.SettleMs
        // демон выжидает внутри обработчика. Снимать раньше значит снять замороженный кадр.
        await _client.RequestAsync(IpcMessageTypes.AcquireCaptureHook, payload,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        try
        {
            // В пул потоков: CapturePng синхронен и занимает десятки миллисекунд (GDI+ плюс
            // кодирование PNG кадра в 3840×2160), а зовут его из обработчика кнопки — то есть из
            // потока UI, который на это время замер бы вместе со всей панелью.
            return await Task.Run(
                () => Win32NativeWindowSystem.Open((IntPtr)hwnd).CapturePng(),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Токена здесь нет намеренно — то же правило, что и у IWindowHookHolder.ExitHookAsync:
            // отменить уборку по тому же токену, который её и вызвал, значит не сделать её ровно
            // в том случае, ради которого она нужна. Отказ глушится: демон отпустит аренду сам на
            // разрыве, а исключение отсюда затёрло бы настоящую причину неудачи захвата.
            try
            {
                await _client.RequestAsync(IpcMessageTypes.ReleaseCaptureHook, payload)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Не удалось отпустить побудку окна 0x{Hwnd:X} после снимка", hwnd);
            }
        }
    }
}
