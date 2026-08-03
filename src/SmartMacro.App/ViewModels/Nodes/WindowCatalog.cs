using SmartMacro.Contracts.Dto;

namespace SmartMacro.App.ViewModels.Nodes;

/// <summary>
/// Живая картина того, чем управляет демон, глазами редактора: каждое отслеживаемое окно со
/// своими текущими тегами.
///
/// Нужен, чтобы бейдж целей (макет 1g) отвечал на вопрос «сколько окон сейчас попадёт» без
/// похода по трубе. Единственным экземпляром владеет <c>MacroEditorViewModel</c> и раздаёт его
/// каждой <see cref="TargetSelectorViewModel"/> открытого графа — тем же приёмом, каким редактор
/// уже раздаёт <c>NodeChoices</c> и <c>MacroChoices</c>, так что появление окна разом обновляет
/// все бейджи на canvas.
///
/// <b>Зачем вторая копия списка окон</b> (у <c>WorkspaceViewModel</c> есть своя): это разные
/// формы под разные задачи. Workspace держит редактируемые СТРОКИ с фишками тегов, полем
/// недопечатанного тега и состоянием фокуса; здесь лежат голые DTO и ничего больше, и живёт это
/// ровно столько, сколько редактор. Внедрить workspace в редактор значило бы связать два режима
/// между собой и затащить всю view-model «Окна» в каждый тест редактора.
///
/// Не <c>ObservableObject</c>: напрямую к нему ничто не привязывается. Потребители подписываются
/// на <see cref="Changed"/> и пересчитывают своё.
/// </summary>
public sealed class WindowCatalog
{
    private IReadOnlyList<WindowDto> _windows = [];

    /// <summary>Поднимается после любого изменения набора окон или тегов любого из них.</summary>
    public event Action? Changed;

    /// <summary>Текущий снимок, в том порядке, в каком его сообщил демон.</summary>
    public IReadOnlyList<WindowDto> Windows => _windows;

    /// <summary>Сколько окон демон отслеживает прямо сейчас.</summary>
    public int Count => _windows.Count;

    /// <summary>Заменяет снимок целиком — это ответ на <c>GetWindows</c>.</summary>
    public void Reset(IReadOnlyList<WindowDto> windows)
    {
        // `?? []` не избыточен, что бы ни говорил анализатор. Родословная этого списка —
        // десериализованный ответ демона на GetWindows: аннотация о ненулевости держится на
        // честном слове DTO, а не на проверке во время выполнения, и демон, не положивший поле,
        // приведёт сюда null. Единственный сегодняшний вызывающий уже прикрылся таким же `?? []`
        // у себя, но ловит он свой ответ, а не чужие вызовы этого public-метода.
        _windows = windows ?? [];
        Changed?.Invoke();
    }

    /// <summary>
    /// Добавить или заменить по дескриптору. Обслуживает и <c>WindowAppeared</c>, и
    /// <c>WindowTagsChanged</c> — они несут одну и ту же нагрузку с полным состоянием.
    /// </summary>
    public void Upsert(WindowDto window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var next = new List<WindowDto>(_windows.Count + 1);
        var replaced = false;
        foreach (var existing in _windows)
        {
            if (existing.Hwnd == window.Hwnd)
            {
                next.Add(window);
                replaced = true;
            }
            else
            {
                next.Add(existing);
            }
        }

        if (!replaced)
        {
            next.Add(window);
        }

        _windows = next;
        Changed?.Invoke();
    }

    /// <summary>Выбрасывает закрывшееся окно. Незнакомый дескриптор ничего не делает.</summary>
    public void Remove(long hwnd)
    {
        var next = _windows.Where(window => window.Hwnd != hwnd).ToList();
        if (next.Count == _windows.Count)
        {
            return;
        }

        _windows = next;
        Changed?.Invoke();
    }
}
