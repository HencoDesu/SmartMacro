using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using SmartMacro.App.Mvvm;
using SmartMacro.App.ViewModels.Canvas;
using SmartMacro.Macros.Model;
using SmartMacro.Native;

namespace SmartMacro.App.ViewModels.Nodes;

/// <summary>Десять типов нод в том порядке, в каком их предлагает меню «добавить ноду».</summary>
public enum MacroNodeKind
{
    KeyPress,
    Click,
    Delay,
    AddTag,
    RemoveTag,
    SetIcon,
    RunMacro,
    FindElement,
    WaitForElement,
    RecognizeTag,
}

/// <summary>Один пункт всплывающего меню «добавить ноду»: тип плюс его русская подпись.</summary>
/// <param name="Kind">Тип ноды, которую создавать.</param>
/// <param name="Label">Текст пункта меню.</param>
public sealed record MacroNodeKindOption(MacroNodeKind Kind, string Label);

/// <summary>
/// Одно исходящее ребро ноды, нарисованное выпадающим списком id нод.
///
/// Пустая строка здесь — полноправное значение и означает «цели нет, на этом исходе прогон
/// заканчивается», то есть ровно то же самое, что <c>null</c>-ребро в модели. Держать её как
/// <c>""</c>, а не как <c>null</c>, позволяет обойтись обычным <c>ComboBox</c> из строк (null
/// в <c>SelectedItem</c> неотличим от «ещё ничего не выбрали»).
/// </summary>
public sealed class NodeEdgeViewModel : ObservableObject
{
    private string _targetId;
    private ObservableCollection<string> _choices = [];

    public NodeEdgeViewModel(string label, string? targetId)
    {
        Label = label;
        _targetId = targetId ?? string.Empty;
    }

    /// <summary>Название исхода рядом с выпадающим списком («Далее», «Найдено», …).</summary>
    public string Label { get; }

    /// <summary>
    /// То же название, но строчными — так говорит canvas («далее», «нашёл»). Строки коробки
    /// набраны в 9.5px, и заглавная буква там читается как заголовок, а не как подпись порта.
    /// </summary>
    public string ShortLabel => Label.Length == 0
        ? Label
        : string.Concat(char.ToLowerInvariant(Label[0]).ToString(), Label.AsSpan(1));

    /// <summary>Выбранный id ноды; <c>""</c> = конец прогона.</summary>
    [AllowNull]
    public string TargetId
    {
        get => _targetId;
        // ComboBox проталкивает null, когда его SelectedItem выпадает из ItemsSource (например,
        // список пересобрали после удаления ноды). Нормализация к "" превращает это в
        // осмысленное «цели нет» вместо null, который рванул бы позже.
        set
        {
            if (SetField(ref _targetId, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsEnd));
                OnPropertyChanged(nameof(BoxLabel));
            }
        }
    }

    /// <summary>
    /// <c>true</c>, когда этот исход завершает прогон. Это НЕ ошибка и НЕ нода: canvas говорит
    /// об этом прямо в строке порта, а не рисует ребро в терминальную коробку.
    /// </summary>
    public bool IsEnd => _targetId.Length == 0;

    /// <summary>Подпись строки порта на свёрнутой коробке: «нашёл» или «таймаут → конец».</summary>
    public string BoxLabel => IsEnd ? $"{ShortLabel} → конец" : ShortLabel;

    /// <summary>Модельная форма <see cref="TargetId"/>.</summary>
    public string? TargetOrNull => string.IsNullOrEmpty(_targetId) ? null : _targetId;

    /// <summary>
    /// Живой список доступных для выбора id: им владеет редактор, а делят его все рёбра, так
    /// что добавление, переименование или удаление ноды разом обновляет все выпадающие списки.
    /// </summary>
    public ObservableCollection<string> Choices
    {
        get => _choices;
        set => SetField(ref _choices, value);
    }
}

/// <summary>
/// Редактор прямоугольника в клиентских координатах (X / Y / W / H свободным текстом).
///
/// Для необязательных областей нод Find и Wait нулевая (или отрицательная) ширина либо высота
/// отображается обратно в <c>null</c> — то же соглашение «пустой прямоугольник = искать по всей
/// клиентской области», которым слой зрения и так пользуется, так что оба представления значат
/// одно и то же, а round trip не теряет ничего ни для одного прямоугольника, который
/// пользователь способен осмысленно задать.
/// </summary>
public sealed class RegionEditorViewModel : ObservableObject
{
    private string _xText = "0";
    private string _yText = "0";
    private string _widthText = "0";
    private string _heightText = "0";

    public string XText
    {
        get => _xText;
        set => SetField(ref _xText, value);
    }

    public string YText
    {
        get => _yText;
        set => SetField(ref _yText, value);
    }

    public string WidthText
    {
        get => _widthText;
        set => SetField(ref _widthText, value);
    }

    public string HeightText
    {
        get => _heightText;
        set => SetField(ref _heightText, value);
    }

    public static RegionEditorViewModel FromRect(ScreenRect? rect)
    {
        var value = rect ?? default;
        return new RegionEditorViewModel
        {
            XText = NodeInput.FormatInt(value.X),
            YText = NodeInput.FormatInt(value.Y),
            WidthText = NodeInput.FormatInt(value.Width),
            HeightText = NodeInput.FormatInt(value.Height),
        };
    }

    /// <summary>Форма «значение есть всегда» — для нод, у которых область обязательна (RecognizeTag).</summary>
    public ScreenRect ToRect() => new(
        NodeInput.ParseInt(_xText) ?? 0,
        NodeInput.ParseInt(_yText) ?? 0,
        NodeInput.ParseInt(_widthText) ?? 0,
        NodeInput.ParseInt(_heightText) ?? 0);

    /// <summary>Необязательная форма: вырожденный прямоугольник значит «всё окно» и хранится как <c>null</c>.</summary>
    public ScreenRect? ToOptionalRect()
    {
        var rect = ToRect();
        return rect.Width <= 0 || rect.Height <= 0 ? null : rect;
    }

    public IEnumerable<string> GetInputErrors(string nodeId)
    {
        foreach (var (label, text) in new[] { ("X", _xText), ("Y", _yText), ("W", _widthText), ("H", _heightText) })
        {
            if (NodeInput.ParseInt(text) is null)
            {
                yield return $"[{nodeId}] регион {label}: «{text}» — не целое число.";
            }
        }
    }
}

/// <summary>Общий разбор «текст ↔ число» для редакторов нод. Инвариантный, запятая допускается как десятичная точка.</summary>
internal static class NodeInput
{
    public static string FormatInt(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Пустая строка идёт за 0; всё неразбираемое — <c>null</c>, чтобы вызывающий мог о нём сообщить.</summary>
    public static int? ParseInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// Миллисекунды, показанные секундами, как в редакторе до графов: инвариантный
    /// <c>"0.##"</c> (1500 → «1.5», 2000 → «2»). В секундах сам PW называет время каста и
    /// отката, так что автор, читающий подсказку умения, набирает то, что видит.
    /// </summary>
    public static string FormatSeconds(int ms) => (ms / 1000.0).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Текст в секундах обратно в миллисекунды. Запятая как десятичный разделитель принимается
    /// (её выдаёт цифровой блок русской раскладки) за счёт предварительной нормализации: разбор
    /// «1,5» инвариантным <see cref="NumberStyles.Any"/> иначе тихо дал бы 15.
    /// </summary>
    public static int? ParseSecondsToMs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var normalized = text.Trim().Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            seconds < 0)
        {
            return null;
        }

        return (int)Math.Round(seconds * 1000.0);
    }
}

/// <summary>
/// Основа для коробок редактора нод — по одной на ноду открытого графа.
///
/// Иерархия сделана полиморфной намеренно: каждая конкретная строка владеет ровно теми
/// параметрами, что есть у её типа ноды, рисуется через неявный <c>DataTemplate</c>,
/// подобранный по её собственному типу, и перекладывается в модель и обратно в одном месте
/// (<see cref="ToNode"/> / <see cref="FromNode"/>). Эта пара — контракт, на котором держится
/// весь редактор, и её же прибивает тест round trip.
///
/// С D3a тот же объект служит и коробкой на canvas: он несёт своё положение
/// (<see cref="X"/>/<see cref="Y"/>, сохраняемое как <c>NodeEditorInfo</c> в модели),
/// свёрнутость либо развёрнутость встроенного редактора и однострочную
/// <see cref="Summary"/>, которую коробка показывает. То, что это лежит на строке, а не в
/// параллельной иерархии «нода на canvas», означает ровно один объект на ноду и никакой
/// синхронизации.
/// </summary>
public abstract class NodeRowViewModel : ObservableObject
{
    private string _nodeId;
    private bool _isSelected;
    private double _x;
    private double _y;
    private bool _hasPosition;
    private bool _isExpanded;
    private bool _isExecuting;
    private bool _hasBreakpoint;
    private bool _isPaused;
    private string? _passedTime;
    private string? _passedOutcome;
    private bool _isVariableSource;
    private bool _isVariableConsumer;

    protected NodeRowViewModel(string nodeId, TargetSelectorViewModel? target, params NodeEdgeViewModel[] edges)
    {
        _nodeId = nodeId;
        Target = target;
        Edges = edges;
        if (target is not null)
        {
            // Показывает ли коробка чип целей вообще — следствие режима самого селектора, а
            // правят его через отдельную view-model, так что изменение приходится
            // пробрасывать, иначе включение «целей по тегам» оставит коробку на вид прежней.
            // СОДЕРЖИМОЕ чипа пробрасывать не нужно: оно привязано прямо к селектору.
            target.PropertyChanged += (_, _) => OnPropertyChanged(nameof(ShowsTargetChip));
        }
    }

    /// <summary>
    /// Поднимается после изменения <see cref="NodeId"/> и несёт ПРЕЖНИЙ id. Редактор слушает
    /// это, чтобы перенацелить каждое ребро (и стартовую ноду) на новый id.
    /// </summary>
    public event Action<NodeRowViewModel, string>? IdChanged;

    /// <summary>Уникальный внутри графа id. Именно по нему рёбра ссылаются на ноды.</summary>
    [AllowNull]
    public string NodeId
    {
        get => _nodeId;
        set
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0 || string.Equals(trimmed, _nodeId, StringComparison.Ordinal))
            {
                // Пустой id отвергаем сразу: он осиротил бы каждое ребро, указывающее сюда.
                OnPropertyChanged();
                return;
            }

            var previous = _nodeId;
            _nodeId = trimmed;
            OnPropertyChanged();
            IdChanged?.Invoke(this, previous);
        }
    }

    /// <summary>
    /// Размещение на canvas, в форме самой модели. <c>null</c> значит «никогда не размещали» —
    /// граф, написанный до появления canvas, — и такие редактор раскладывает при загрузке, а не
    /// сваливает в начало координат.
    /// </summary>
    public NodeEditorInfo? Editor
    {
        get => _hasPosition ? new NodeEditorInfo(_x, _y) : null;
        set
        {
            if (value is null)
            {
                _hasPosition = false;
                _x = 0;
                _y = 0;
            }
            else
            {
                _hasPosition = true;
                _x = value.X;
                _y = value.Y;
            }

            OnPropertyChanged(nameof(X));
            OnPropertyChanged(nameof(Y));
            OnPropertyChanged(nameof(HasPosition));
        }
    }

    /// <summary>X левого верхнего угла коробки в пространстве canvas.</summary>
    public double X
    {
        get => _x;
        set
        {
            _hasPosition = true;
            SetField(ref _x, value);
        }
    }

    /// <summary>Y левого верхнего угла коробки в пространстве canvas.</summary>
    public double Y
    {
        get => _y;
        set
        {
            _hasPosition = true;
            SetField(ref _y, value);
        }
    }

    /// <summary><c>false</c>, пока ноду не разместили — руками, загрузкой или авторазметкой.</summary>
    public bool HasPosition => _hasPosition;

    /// <summary>Двигает коробку. Один вызов — чтобы перетаскивание поднимало два изменения, а не четыре.</summary>
    public void SetPosition(double x, double y)
    {
        X = x;
        Y = y;
    }

    /// <summary>
    /// Высота, которой пользуется МАРШРУТИЗАТОР. Всегда свёрнутая, даже пока коробка
    /// развёрнута: развёрнутая нода — это мимолётное состояние правки, намеренно
    /// перекрывающее соседей, и перекладка каждого ребра в обход неё заставила бы граф
    /// прыгать.
    /// </summary>
    public double LayoutHeight => IsConditional
        ? CanvasMetrics.ConditionalNodeHeight
        : CanvasMetrics.ActionNodeHeight;

    /// <summary>Нарисованная высота: свёрнутая либо авто (<c>NaN</c>), пока коробка развёрнута.</summary>
    public double BoxHeight => _isExpanded ? double.NaN : LayoutHeight;

    /// <summary>Нарисованная ширина — шире, пока развёрнуто, чтобы вместились поля параметров.</summary>
    public double BoxWidth => _isExpanded ? CanvasMetrics.ExpandedNodeWidth : CanvasMetrics.NodeWidth;

    /// <summary>Два исхода вместо одного — от этого зависят высота коробки и глиф в её шапке.</summary>
    public bool IsConditional => Edges.Count > 1;

    /// <summary>
    /// Коробка сама себе редактор (макет 1e). Двойной клик её открывает, Esc закрывает;
    /// инспектор справа при этом не расходится, потому что оба правят один и тот же объект.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(BoxHeight));
                OnPropertyChanged(nameof(BoxWidth));
            }
        }
    }

    /// <summary>
    /// Исполнитель стоит на этой ноде. Проставляется из потока событий прогона (D3b) только для
    /// ВЫБРАННОГО обхода — веер на десять окон обязан зажечь одну коробку, а не десять.
    /// </summary>
    public bool IsExecuting
    {
        get => _isExecuting;
        set
        {
            if (SetField(ref _isExecuting, value))
            {
                OnPropertyChanged(nameof(IsRunningLive));
            }
        }
    }

    /// <summary>
    /// Обход стоит здесь И при этом не припаркован — единственное состояние, которое пульсирует.
    ///
    /// Припаркованная нода тоже «текущая» (walker вошёл в неё раньше затвора), поэтому привязка
    /// пульсации к одному лишь <see cref="IsExecuting"/> анимировала бы остановленный обход. По
    /// последствиям эти два состояния разделяет одно нажатие клавиши, и выглядеть одинаково они
    /// не имеют права.
    /// </summary>
    public bool IsRunningLive => _isExecuting && !_isPaused;

    // ---- отладчик (D5) ------------------------------------------------------------------

    /// <summary>
    /// Красная точка в углу коробки и отмеченная галочка в инспекторе: walker останавливается
    /// ПЕРЕД этой нодой.
    ///
    /// Держится на строке, поэтому набор у демона пересобирается из строк при каждом изменении,
    /// а переименование ноды переносит её точку останова само. В <see cref="ToNode"/> её НЕТ —
    /// точка останова принадлежит сеансу отладки, а не файлу макроса и не диффу.
    /// </summary>
    public bool HasBreakpoint
    {
        get => _hasBreakpoint;
        set => SetField(ref _hasBreakpoint, value);
    }

    /// <summary>Выбранный обход припаркован здесь прямо сейчас. В отличие от <see cref="IsExecuting"/>, нода ещё НЕ началась.</summary>
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (SetField(ref _isPaused, value))
            {
                RaiseRunStamp();
                OnPropertyChanged(nameof(IsRunningLive));
            }
        }
    }

    /// <summary>
    /// Сколько эта нода заняла на выбранном обходе, уже отформатированное («1.2 с»), либо
    /// <c>null</c>, если она не отрабатывала. От этого зависят ✓ и то притушение, которым макет
    /// обозначает «уже пройдено».
    /// </summary>
    public string? PassedTime
    {
        get => _passedTime;
        set
        {
            if (SetField(ref _passedTime, value))
            {
                OnPropertyChanged(nameof(IsPassed));
                RaiseRunStamp();
            }
        }
    }

    // Правый слот шапки один на двоих, поэтому тот из двух, кто изменился, обязан велеть
    // другому посторониться.
    private void RaiseRunStamp()
    {
        OnPropertyChanged(nameof(ShowsRunStamp));
        OnPropertyChanged(nameof(ShowsTargetChip));
    }

    /// <summary>Куда ушли, по-русски, — подсказка на пройденной коробке.</summary>
    public string? PassedOutcome
    {
        get => _passedOutcome;
        set => SetField(ref _passedOutcome, value);
    }

    /// <summary><c>true</c>, как только выбранный обход прошёл через эту ноду.</summary>
    public bool IsPassed => _passedTime is not null;

    /// <summary>Переменная под курсором ЗАПИСЫВАЕТСЯ здесь. Зажигает коробку и один конец пунктирной связи.</summary>
    public bool IsVariableSource
    {
        get => _isVariableSource;
        set => SetField(ref _isVariableSource, value);
    }

    /// <summary>Переменная под курсором ЧИТАЕТСЯ здесь.</summary>
    public bool IsVariableConsumer
    {
        get => _isVariableConsumer;
        set => SetField(ref _isVariableConsumer, value);
    }

    /// <summary>
    /// Подсвечена на canvas и показана в инспекторе. Проставляется кликом по коробке либо по
    /// замечанию валидатора.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    /// <summary>Русская подпись типа, показываемая в шапке коробки.</summary>
    public abstract string TypeLabel { get; }

    /// <summary>
    /// Единственная моноширинная строка под id: то, по чему работа этой ноды опознаётся с
    /// одного взгляда (клавиша, точка, шаблон, задержка). Пересчитывается на любое изменение
    /// свойства — см. <see cref="OnPropertyChanged"/>.
    /// </summary>
    public abstract string Summary { get; }

    /// <summary>
    /// <c>false</c>, когда коробке не следует печатать строку сводки: либо сказать нечего, либо
    /// нода вместо неё рисует <see cref="Keycap"/>, и напечатать оба значило бы показать одну и
    /// ту же клавишу дважды.
    /// </summary>
    public bool HasSummary => Summary.Length > 0 && Keycap is null;

    /// <summary>
    /// Печатает ли коробка чип целей вообще. Только когда нода маршрутизирует по ТЕГАМ: шапка
    /// в 210px не унесёт разом и подпись типа, и чип, а «действует на контекстное окно» — это
    /// умолчание, которое есть у каждой второй ноды; слова стоит именно отступление от него.
    ///
    /// Подавляется, пока показывается штамп прогона. Оба делят единственный правый слот шапки,
    /// и без этого они НАПЕЧАТАЛИСЬ БЫ ДРУГ ПОВЕРХ ДРУГА — «✓ 2 мс» поверх «нет окон» не
    /// читалось ни как то, ни как другое.
    /// </summary>
    public bool ShowsTargetChip => Target?.UseSelector == true && !ShowsRunStamp;

    /// <summary>
    /// В правом слоте шапки показано состояние прогона (✓ со временем либо отметка о парковке),
    /// а не чип целей.
    /// </summary>
    public bool ShowsRunStamp => _passedTime is not null || _isPaused;

    /// <summary>
    /// Не <c>null</c> только у <see cref="KeyPressNodeRowViewModel"/>: коробка рисует кейкап
    /// вместо строки текста, потому что клавиша — это то, на что нажимают, и читается она
    /// именно так.
    /// </summary>
    public virtual string? Keycap => null;

    /// <summary>Исходящие рёбра, в порядке показа.</summary>
    public IReadOnlyList<NodeEdgeViewModel> Edges { get; }

    /// <summary>
    /// Редактор селектора целей либо <c>null</c> у нод, у которых в модели нет <c>Target</c>, —
    /// у условных (по замыслу только контекстное окно) и у <c>DelayNode</c> (пауза общая на
    /// прогон).
    /// </summary>
    public TargetSelectorViewModel? Target { get; }

    /// <summary>Управляет видимостью блока селектора.</summary>
    public bool HasTarget => Target is not null;

    /// <summary>
    /// Склеивает части сводки коробки средней точкой, пропуская пустые. Нода, у которой шаблон
    /// ещё не набрали, обязана читаться как <c>всё окно</c>, а не <c>· всё окно</c>.
    /// </summary>
    protected static string Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>Собирает ноду модели из текущего состояния редактора.</summary>
    public abstract MacroNode ToNode();

    /// <summary>
    /// Любое изменение переподнимает <see cref="Summary"/>.
    ///
    /// Альтернатива — руками написанное поднятие в каждом из ~25 сеттеров параметров по десяти
    /// типам строк, и цена забытого сеттера — коробка, которая тихо показывает устаревший
    /// текст; такая ошибка переживает полный прогон тестов. Чисто оформительские свойства
    /// исключены, чтобы перетаскивание коробки не трепало её текст.
    /// </summary>
    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        switch (propertyName)
        {
            case null:
            case nameof(Summary):
            case nameof(HasSummary):
            case nameof(ShowsTargetChip):
            case nameof(X):
            case nameof(Y):
            case nameof(HasPosition):
            case nameof(IsSelected):
            case nameof(IsExpanded):
            case nameof(IsExecuting):
            case nameof(IsRunningLive):
            case nameof(BoxWidth):
            case nameof(BoxHeight):
            // Состояние прогона и отладчика — всё это оформление: ни точка останова, ни штамп с
            // прошедшим временем не имеют права заставлять коробку перерисовывать сводку по
            // параметрам.
            case nameof(HasBreakpoint):
            case nameof(IsPaused):
            case nameof(IsPassed):
            case nameof(PassedTime):
            case nameof(PassedOutcome):
            case nameof(ShowsRunStamp):
            case nameof(IsVariableSource):
            case nameof(IsVariableConsumer):
                return;
            default:
                base.OnPropertyChanged(nameof(Summary));
                base.OnPropertyChanged(nameof(HasSummary));
                return;
        }
    }

    /// <summary>
    /// Претензии по полям («X — не число»), по-русски; пусто, когда строка чиста. Проверяются
    /// до того, как отработает валидатор графа, потому что <see cref="ToNode"/> снисходителен
    /// (неразбираемое число становится нулём) и иначе тихо сохранил бы этот ноль.
    /// </summary>
    public virtual IEnumerable<string> GetInputErrors() => [];

    /// <summary>Загружает ноду модели в строку подходящего типа.</summary>
    /// <exception cref="NotSupportedException">У этого типа ноды нет редактора (сюда попадать не должно).</exception>
    public static NodeRowViewModel FromNode(MacroNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        NodeRowViewModel row = node switch
        {
            KeyPressNode n => new KeyPressNodeRowViewModel(n),
            ClickNode n => new ClickNodeRowViewModel(n),
            DelayNode n => new DelayNodeRowViewModel(n),
            AddTagNode n => new AddTagNodeRowViewModel(n),
            RemoveTagNode n => new RemoveTagNodeRowViewModel(n),
            SetIconNode n => new SetIconNodeRowViewModel(n),
            RunMacroNode n => new RunMacroNodeRowViewModel(n),
            FindElementNode n => new FindElementNodeRowViewModel(n),
            WaitForElementNode n => new WaitForElementNodeRowViewModel(n),
            RecognizeTagNode n => new RecognizeTagNodeRowViewModel(n),
            _ => throw new NotSupportedException($"No editor for node type {node.GetType().Name}."),
        };
        row.Editor = node.Editor;
        return row;
    }

    /// <summary>Создаёт пустую строку запрошенного вида с разумными умолчаниями.</summary>
    public static NodeRowViewModel Create(MacroNodeKind kind, string nodeId) => kind switch
    {
        MacroNodeKind.KeyPress => new KeyPressNodeRowViewModel(new KeyPressNode { Id = nodeId, Key = VirtualKey.F1 }),
        MacroNodeKind.Click => new ClickNodeRowViewModel(new ClickNode { Id = nodeId, Point = default(ScreenPoint) }),
        MacroNodeKind.Delay => new DelayNodeRowViewModel(new DelayNode { Id = nodeId, Ms = 1000 }),
        MacroNodeKind.AddTag => new AddTagNodeRowViewModel(new AddTagNode { Id = nodeId, Tag = string.Empty }),
        MacroNodeKind.RemoveTag => new RemoveTagNodeRowViewModel(new RemoveTagNode { Id = nodeId, Tag = string.Empty }),
        MacroNodeKind.SetIcon => new SetIconNodeRowViewModel(new SetIconNode
            { Id = nodeId, IconPath = "Assets/ClassIcons/{tag}.png" }),
        MacroNodeKind.RunMacro => new RunMacroNodeRowViewModel(new RunMacroNode
            { Id = nodeId, MacroName = string.Empty }),
        MacroNodeKind.FindElement => new FindElementNodeRowViewModel(new FindElementNode
            { Id = nodeId, Template = string.Empty }),
        MacroNodeKind.WaitForElement => new WaitForElementNodeRowViewModel(new WaitForElementNode
            { Id = nodeId, Template = string.Empty, TimeoutMs = 10_000 }),
        MacroNodeKind.RecognizeTag => new RecognizeTagNodeRowViewModel(new RecognizeTagNode
            { Id = nodeId, TemplateSet = string.Empty, Region = default }),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown node kind."),
    };

    /// <summary>Пункты меню «добавить ноду» во всплывающем списке, в порядке каталога.</summary>
    public static IReadOnlyList<MacroNodeKindOption> Kinds { get; } =
    [
        new(MacroNodeKind.KeyPress, "Нажать клавишу"),
        new(MacroNodeKind.Click, "Клик"),
        new(MacroNodeKind.Delay, "Пауза"),
        new(MacroNodeKind.AddTag, "Добавить тег"),
        new(MacroNodeKind.RemoveTag, "Снять тег"),
        new(MacroNodeKind.SetIcon, "Сменить иконку"),
        new(MacroNodeKind.RunMacro, "Запустить макрос"),
        new(MacroNodeKind.FindElement, "Найти элемент"),
        new(MacroNodeKind.WaitForElement, "Ждать элемент"),
        new(MacroNodeKind.RecognizeTag, "Распознать тег"),
    ];
}
