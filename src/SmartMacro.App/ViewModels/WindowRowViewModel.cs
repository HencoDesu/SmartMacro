using System.Collections.ObjectModel;
using System.Globalization;
using Serilog;
using SmartMacro.App.Ipc;
using SmartMacro.App.Mvvm;
using SmartMacro.Contracts.Dto;
using SmartMacro.Contracts.Ipc;

namespace SmartMacro.App.ViewModels;

/// <summary>
/// Один тег на окне, нарисованный снимаемым чипом. Несёт ссылку назад на свою строку, чтобы
/// кнопке <c>×</c> у чипа было куда обратиться и виду не приходилось сводить два DataContext.
/// </summary>
public sealed class TagChipViewModel
{
    private readonly WindowRowViewModel _owner;

    public TagChipViewModel(WindowRowViewModel owner, string text)
    {
        _owner = owner;
        Text = text;
    }

    /// <summary>Сам тег. Произвольная строка, регистрозависимая, обычно кириллица.</summary>
    public string Text { get; }

    /// <summary>Просит демон снять этот тег. Без ожидания — чип исчезнет, когда придёт ответный пуш.</summary>
    public void Remove() => _ = _owner.RemoveTagAsync(Text);
}

/// <summary>
/// Одно отслеживаемое окно в главном списке: какому процессу принадлежит, его хэндл и живой
/// набор тегов с возможностью добавить и убрать.
///
/// Собственного состояния тегов у строки нет. Стадия 3 этого не изменила — изменила только то,
/// где живёт владелец: раньше это был <c>WindowRegistry</c> из Core в этом же процессе, теперь
/// демонский, до которого дотягиваются по IPC. Поэтому добавление и удаление отправляют запрос
/// и локально не меняют ничего — видимые чипы пересобирает <see cref="ApplyTags"/>, когда демон
/// пришлёт обратно <c>WindowTagsChanged</c>. Так показанное остаётся честным независимо от
/// того, пришло изменение из этой строки, из другой панели или из ноды макроса.
/// </summary>
public sealed class WindowRowViewModel : ObservableObject
{
    private readonly IIpcClient _client;
    private string _newTagText = string.Empty;
    private bool _isAlternate;
    private bool _isAddingTag;

    public WindowRowViewModel(IIpcClient client, WindowDto window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _client = client;
        Hwnd = window.Hwnd;
        ProcessName = window.ProcessName;
        ApplyTags(window.Tags);
    }

    /// <summary>Нативный хэндл — личность строки, совпадающая с ключом у демона.</summary>
    public long Hwnd { get; }

    /// <summary>Имя процесса-владельца в том виде, в каком его сообщил монитор процессов демона.</summary>
    public string ProcessName { get; }

    /// <summary>Хэндл в той шестнадцатеричной форме, что и в логах, — чтобы строку можно было сопоставить со строкой лога.</summary>
    public string HwndHex => string.Create(CultureInfo.InvariantCulture, $"0x{Hwnd:X}");

    /// <summary>Живые чипы тегов окна, в том порядке, в каком их сообщил демон.</summary>
    public ObservableCollection<TagChipViewModel> Tags { get; } = [];

    /// <summary>Текст поля «добавить тег» у этой строки.</summary>
    public string NewTagText
    {
        get => _newTagText;
        set => SetField(ref _newTagText, value);
    }

    /// <summary>Окно без тегов — это случай «ещё не опознано»: вид гасит такую строку в серое.</summary>
    public bool HasTags => Tags.Count > 0;

    /// <summary>
    /// Каждая вторая строка помеченной группы получает чередующийся фон. Это состояние показа,
    /// его проставляет <c>WorkspaceViewModel</c> при перераскладке: у <c>ItemsControl</c> в
    /// Avalonia нет индекса чередования, так что индексу приходится жить на самом элементе.
    /// </summary>
    public bool IsAlternate
    {
        get => _isAlternate;
        set => SetField(ref _isAlternate, value);
    }

    /// <summary>
    /// На помеченной строке поле тега раскрывается (у непомеченных оно видно всегда — это их
    /// единственное осмысленное действие, поэтому раскладка 1b даёт ему акцентную рамку, а
    /// помеченная строка прячет его за тихим «+»).
    /// </summary>
    public bool IsAddingTag
    {
        get => _isAddingTag;
        set => SetField(ref _isAddingTag, value);
    }

    /// <summary>
    /// Отправляет демону то, что набрано в <see cref="NewTagText"/>. Поле очищается только
    /// тогда, когда запрос действительно ушёл, — так дубликат (или провалившийся вызов)
    /// оставляет текст на месте, чтобы пользователь его поправил, а не тихо его теряет.
    /// </summary>
    /// <returns><c>true</c>, когда демон принял запрос <c>AddTag</c>.</returns>
    public async Task<bool> AddTagAsync()
    {
        var tag = _newTagText.Trim();
        if (tag.Length == 0)
        {
            return false;
        }

        // Проверяем на месте, а не спрашивая: дубликат для демона — тихая пустая операция (ему
        // неоткуда знать, что для текстового поля разница есть), так что отличить «уже есть» от
        // «добавлено» способно только это место.
        if (Tags.Any(chip => string.Equals(chip.Text, tag, StringComparison.Ordinal)))
        {
            return false;
        }

        if (!await SendAsync(IpcMessageTypes.AddTag, new AddTagRequest(Hwnd, tag)).ConfigureAwait(true))
        {
            return false;
        }

        NewTagText = string.Empty;
        IsAddingTag = false;
        return true;
    }

    /// <summary>Открывает встроенное поле тега на строке, у которой чипы уже есть.</summary>
    public void BeginAddTag() => IsAddingTag = true;

    /// <summary>Бросает встроенное поле тега вместе со всем, что в нём успели набрать.</summary>
    public void CancelAddTag()
    {
        NewTagText = string.Empty;
        IsAddingTag = false;
    }

    /// <summary>Просит демон снять с окна один тег.</summary>
    /// <returns><c>true</c>, когда запрос приняли.</returns>
    public Task<bool> RemoveTagAsync(string tag) =>
        SendAsync(IpcMessageTypes.RemoveTag, new RemoveTagRequest(Hwnd, tag));

    /// <summary>
    /// Пересобирает чипы по снимку от демона. Целиком, а не по разнице: тегов у окна от силы
    /// горстка, и у чипов нет состояния, которое стоило бы сберегать.
    /// </summary>
    public void ApplyTags(IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        Tags.Clear();
        foreach (var tag in tags)
        {
            Tags.Add(new TagChipViewModel(this, tag));
        }

        OnPropertyChanged(nameof(HasTags));
    }

    private async Task<bool> SendAsync(string type, object payload)
    {
        try
        {
            await _client.RequestAsync(type, payload).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex is IpcRequestException or TimeoutException or ObjectDisposedException)
        {
            // Повесить тег на окно, умершее мгновением раньше, — обычное дело, и диалога оно не
            // стоит; строка всё равно вот-вот исчезнет.
            Log.Warning(ex, "Не удалось выполнить '{Request}' для окна 0x{Hwnd:X}", type, Hwnd);
            return false;
        }
    }
}
