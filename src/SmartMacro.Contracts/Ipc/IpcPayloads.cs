using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Settings;
using SmartMacro.Macros.Model;

namespace SmartMacro.Contracts.Ipc;

// Типизированные нагрузки для каталога из IpcMessageTypes. Здесь живут только те формы,
// которым нужны именованные поля: ответы, представляющие собой голый массив (WindowDto[],
// RunningMacroDto[], MacroGraph[], ValidationIssueDto[]) или голый скаляр (DumpCaptures →
// string), сериализуются как есть, а запросы и события без аргументов нагрузки не несут вовсе.

/// <summary>Нагрузка <see cref="IpcMessageTypes.AddTag"/>.</summary>
/// <param name="Hwnd">Хендл окна, которому вешают тег, в том виде, в каком он приходит в <c>WindowDto.Hwnd</c>.</param>
/// <param name="Tag">Добавляемый тег. Свободная строка, регистр важен.</param>
public sealed record AddTagRequest(long Hwnd, string Tag);

/// <summary>Нагрузка <see cref="IpcMessageTypes.RemoveTag"/>.</summary>
/// <param name="Hwnd">Хендл окна, с которого снимают тег, в том виде, в каком он приходит в <c>WindowDto.Hwnd</c>.</param>
/// <param name="Tag">Снимаемый тег. Снять тег, которого у окна нет, — не ошибка, а пустая операция.</param>
public sealed record RemoveTagRequest(long Hwnd, string Tag);

/// <summary>Нагрузка <see cref="IpcMessageTypes.RunMacro"/>.</summary>
/// <param name="Name">Макрос, который надо запустить. На незнакомом имени запрос падает.</param>
public sealed record RunMacroRequest(string Name);

/// <summary>Нагрузка <see cref="IpcMessageTypes.StopMacro"/>.</summary>
/// <param name="RunId">Прогон, который надо отменить, из <c>RunningMacroDto.RunId</c>. Уже завершившийся прогон отменяется молча и успешно.</param>
public sealed record StopMacroRequest(Guid RunId);

/// <summary>Нагрузка <see cref="IpcMessageTypes.SaveMacro"/>.</summary>
/// <param name="Macro">Граф, который надо сохранить. Его <see cref="MacroGraph.Name"/> становится именем файла.</param>
public sealed record SaveMacroRequest(MacroGraph Macro);

/// <summary>Нагрузка <see cref="IpcMessageTypes.DeleteMacro"/>.</summary>
/// <param name="Name">Макрос, который надо удалить. Удалить несуществующий макрос — не ошибка, а пустая операция.</param>
public sealed record DeleteMacroRequest(string Name);

/// <summary>Нагрузка <see cref="IpcMessageTypes.SubscribeRunEvents"/>.</summary>
/// <param name="Enabled">
/// <c>true</c> включает поток для ЭТОГО соединения, <c>false</c> выключает. Настройка
/// на соединение, а не глобальная: второму клиенту, который ни о чём не просил, всплеск
/// вываливать нельзя, а демон, на которого никто не смотрит, вообще не должен платить за эти
/// события.
/// </param>
public sealed record SubscribeRunEventsRequest(bool Enabled);

/// <summary>Нагрузка <see cref="IpcMessageTypes.SubscribeLog"/>.</summary>
/// <param name="Enabled">
/// <c>true</c> включает ленту журнала для ЭТОГО соединения, <c>false</c> выключает. Настройка на
/// соединение, как и у <see cref="SubscribeRunEventsRequest"/>, и по тем же причинам: второму
/// клиенту, который ни о чём не просил, шум демона на <c>Debug</c> вываливать нельзя, а расплата
/// за неразобранную очередь здесь — отключение.
///
/// Уровня в запросе НЕТ намеренно. Фильтр живёт в панели, потому что он обязан работать в обе
/// стороны: сдвинув его вниз, пользователь должен УВИДЕТЬ то, что уже приехало, а серверный
/// фильтр этого не умеет — он может только не прислать. Рычаг, уменьшающий трафик по-настоящему,
/// — это <c>MinimumLevel</c> самого Serilog у демона, и он относится к будущему экрану настроек.
/// </param>
public sealed record SubscribeLogRequest(bool Enabled);

/// <summary>
/// Нагрузка <see cref="IpcMessageTypes.GetTemplateImage"/>. Пара «набор + имя» и есть
/// идентичность шаблона: имя уникально только внутри своей папки, и <c>Лучник</c> из набора
/// <c>classes</c> — это не тот же файл, что одиночный <c>Лучник</c> в корне дерева.
/// </summary>
/// <param name="Set">Набор (подпапка) либо <c>null</c> — одиночный шаблон в корне <c>templates/</c>.</param>
/// <param name="Name">
/// Основа имени файла из <c>TemplateDto.Name</c>. Сегменты пути в ней запрос отклоняют: это
/// строка, приехавшая по проводу, и <c>..\</c> в ней означает попытку вычитать что-то за
/// пределами дерева <c>templates/</c>, а не шаблон, которого не хватает.
/// </param>
public sealed record GetTemplateImageRequest(string? Set, string Name);

/// <summary>Нагрузка события <see cref="IpcMessageTypes.WindowClosed"/>.</summary>
/// <param name="Hwnd">Хендл исчезнувшего окна. Никакие другие его данные не переживают закрытия.</param>
public sealed record WindowClosedEvent(long Hwnd);

/// <summary>
/// Нагрузка <see cref="IpcMessageTypes.SetBreakpoints"/>. Заменяет весь набор точек останова
/// одного макроса целиком: редактор всегда знает все свои точки останова, поэтому полная
/// замена ценой лишних нескольких байт убирает целый класс ошибок вида «add и remove пришли не
/// в том порядке».
/// </summary>
/// <param name="MacroName">Граф, которому принадлежат точки останова. Макрос может ещё не существовать — их может нести несохранённый черновик.</param>
/// <param name="NodeIds">
/// Ноды, на которых обход должен останавливаться. ПУСТОЙ массив снимает у макроса все точки
/// останова — и отсутствующее поле значит ровно то же самое.
///
/// <b>Помечено nullable намеренно.</b> Тип пришёл бы сюда ненулевым, но нагрузка приезжает
/// из JSON: клиент, не положивший это поле, отдаёт <c>null</c>, и ненулевая аннотация просто
/// соврала бы — а анализатор поверх неё начал бы советовать снять защиту у получателя.
/// </param>
public sealed record SetBreakpointsRequest(string MacroName, IReadOnlyList<Guid>? NodeIds);

/// <summary>Нагрузка <see cref="IpcMessageTypes.DebugCommand"/>.</summary>
/// <param name="WalkId">Обход, к которому применяется команда, из <c>RunWalkDto.WalkId</c>. На неизвестный (завершившийся) обход приходит <c>Accepted = false</c>.</param>
/// <param name="Command">Что сделать.</param>
/// <param name="NodeId">Целевая нода для <see cref="DebugCommand.RunToNode"/>; в остальных случаях игнорируется.</param>
public sealed record DebugCommandRequest(Guid WalkId, DebugCommand Command, Guid? NodeId = null);

/// <summary>
/// Нагрузка <see cref="IpcMessageTypes.SaveSettings"/>. Настройки едут ЦЕЛИКОМ, а не дельтой:
/// панель всегда держит весь снимок, а полная замена убирает целый класс ошибок вида «две
/// правки приехали не в том порядке» — ровно та же логика, что у
/// <see cref="SetBreakpointsRequest"/>.
/// </summary>
/// <param name="Settings">
/// Новое содержимое файла настроек.
///
/// <b>Помечено nullable по той же причине, что <c>NodeIds</c> выше:</b> нагрузка приезжает из
/// JSON, клиент вправе поля не положить, и ненулевая аннотация просто соврала бы. Демон
/// отвечает на это отказом, а не падением.
/// </param>
public sealed record SaveSettingsRequest(AppSettings? Settings);

/// <summary>
/// Нагрузка <see cref="IpcMessageTypes.SetLogLevel"/>.
/// </summary>
/// <param name="Level">
/// Новый минимальный уровень журнала демона. Не сохраняется никуда: живёт до перезапуска демона,
/// потому что постоянный уровень намеренно остался в <c>appsettings.json</c> — см. каталог.
/// </param>
public sealed record SetLogLevelRequest(LogLevelDto Level);
