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
    RunSubmacro,
    FindElement,
    WaitForElement,
    RecognizeTag,
}

/// <summary>Один пункт всплывающего меню «добавить ноду»: тип плюс его русская подпись.</summary>
/// <param name="Kind">Тип ноды, которую создавать.</param>
/// <param name="Label">Текст пункта меню.</param>
public sealed record MacroNodeKindOption(MacroNodeKind Kind, string Label);

/// <summary>
/// Одно исходящее ребро ноды, нарисованное выпадающим списком нод.
///
/// Источник истины — <see cref="TargetId"/> (<c>null</c> = конец прогона, ровно как
/// <c>null</c>-ребро модели); <see cref="Target"/> — то, к чему привязан <c>ComboBox</c>, и
/// разрешается он по общему на весь редактор списку <see cref="Choices"/>. Два поля, а не одно,
/// потому что рёбра загружаются раньше, чем список выбора вообще существует: коробки создаются
/// по графу, и лишь затем редактор собирает <see cref="Choices"/> и зовёт <see cref="Resolve"/>.
/// </summary>
public sealed class NodeEdgeViewModel : ObservableObject
{
    private Guid? _targetId;
    private NodeChoiceViewModel? _target;
    private ObservableCollection<NodeChoiceViewModel> _choices = [];

    public NodeEdgeViewModel(string label, Guid? targetId)
    {
        Label = label;
        _targetId = targetId;
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

    /// <summary>Нода, в которую ведёт исход; <c>null</c> = конец прогона. Модельная форма ребра.</summary>
    public Guid? TargetId
    {
        get => _targetId;
        set
        {
            if (_targetId == value)
            {
                return;
            }

            _targetId = value;
            Resolve();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEnd));
            OnPropertyChanged(nameof(BoxLabel));
        }
    }

    /// <summary>
    /// Выбранный элемент списка. ComboBox проталкивает сюда <c>null</c>, когда его SelectedItem
    /// выпадает из ItemsSource (список пересобрали после удаления ноды), и это нормализуется в
    /// «цели нет» — то же самое, чем такое ребро и стало бы.
    /// </summary>
    [AllowNull]
    public NodeChoiceViewModel Target
    {
        get => _target ?? NodeChoiceViewModel.End;
        set => TargetId = value?.Id;
    }

    /// <summary>
    /// <c>true</c>, когда этот исход завершает прогон. Это НЕ ошибка и НЕ нода: canvas говорит
    /// об этом прямо в строке порта, а не рисует ребро в терминальную коробку.
    /// </summary>
    public bool IsEnd => _targetId is null;

    /// <summary>Подпись строки порта на свёрнутой коробке: «нашёл» или «таймаут → конец».</summary>
    public string BoxLabel => IsEnd ? $"{ShortLabel} → конец" : ShortLabel;

    /// <summary>
    /// Живой список нод, доступных для выбора: им владеет редактор, а делят его все рёбра, так
    /// что добавление, переименование или удаление ноды разом обновляет все выпадающие списки.
    /// </summary>
    public ObservableCollection<NodeChoiceViewModel> Choices
    {
        get => _choices;
        set
        {
            if (SetField(ref _choices, value))
            {
                Resolve();
            }
        }
    }

    /// <summary>
    /// Заново находит элемент списка по <see cref="TargetId"/>. Зовётся редактором после каждой
    /// пересборки <see cref="Choices"/>.
    /// </summary>
    public void Resolve()
    {
        var found = _targetId is null
            ? NodeChoiceViewModel.End
            : _choices.FirstOrDefault(choice => choice.Id == _targetId);
        if (ReferenceEquals(found, _target))
        {
            return;
        }

        _target = found;
        OnPropertyChanged(nameof(Target));
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

    public IEnumerable<string> GetInputErrors(string nodeName)
    {
        foreach (var (label, text) in new[] { ("X", _xText), ("Y", _yText), ("W", _widthText), ("H", _heightText) })
        {
            if (NodeInput.ParseInt(text) is null)
            {
                yield return $"[{nodeName}] регион {label}: «{text}» — не целое число.";
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

    /// <summary>Порог совпадения в текст; <c>null</c> (взять умолчание) — это пустое поле.</summary>
    public static string FormatThreshold(double? value) =>
        value is { } number ? number.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>
    /// Текст обратно в порог. Пустая строка — это ЗАКОННОЕ <c>null</c> («умолчание слоя
    /// зрения»), а не ошибка, поэтому успех и значение приходится разделять: у обычного
    /// <c>TryParse</c> для этого не хватает исходов.
    /// </summary>
    public static bool TryParseThreshold(string? text, out double? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        // Запятая как десятичный разделитель — её выдаёт цифровой блок русской раскладки.
        var normalized = text.Trim().Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        value = number;
        return true;
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
    private string _displayName;
    private bool _isSelected;
    private bool _isMarked;
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

    protected NodeRowViewModel(MacroNode node, TargetSelectorViewModel? target, params NodeEdgeViewModel[] edges)
    {
        ArgumentNullException.ThrowIfNull(node);
        Id = node.Id;
        _displayName = MacroNodeNames.Display(node);
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
    /// Личность ноды. Не показывается и не редактируется: рёбра, стартовая нода, точки останова
    /// и подсветка прогона адресуются ею, и больше она ни для чего не нужна.
    ///
    /// <b>События <c>IdChanged</c> больше нет.</b> Пока рёбра ссылались на ноду строкой, которую
    /// правил пользователь, переименование приходилось разносить по графу: редактор ловил старое
    /// значение и перенацеливал каждое входящее ребро, стартовую ноду и набор точек останова.
    /// Теперь переименование — это <see cref="DisplayName"/>, и оно не трогает ровным счётом
    /// ничего.
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Подпись ноды: то, что видно в шапке коробки, в полосе лога и в замечаниях валидатора.
    /// Пустую строку отвергаем — безымянная нода читалась бы в логе как пропущенная строка.
    /// </summary>
    [AllowNull]
    public string DisplayName
    {
        get => _displayName;
        set
        {
            var trimmed = (value ?? string.Empty).Trim();
            // Имя передаётся ЯВНО: [CallerMemberName] стоит на объявлении базового
            // OnPropertyChanged, а здесь он перекрыт (см. ниже, ради пересчёта Summary), и у
            // перекрытия атрибута нет — безаргументный вызов отсюда уехал бы с null, то есть
            // «изменилось всё», и редактор не узнал бы, что переименовали именно ноду.
            if (trimmed.Length == 0 || string.Equals(trimmed, _displayName, StringComparison.Ordinal))
            {
                // Отказ тоже надо объявить: поле ввода обязано вернуться к прежнему значению.
                OnPropertyChanged(nameof(DisplayName));
                return;
            }

            _displayName = trimmed;
            OnPropertyChanged(nameof(DisplayName));
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

    /// <summary>
    /// Отмечена для группового действия — сегодня оно ровно одно: «выделить в под-макрос»
    /// (волна F4).
    ///
    /// Отдельно от <see cref="IsSelected"/>, а не вместо него, и это решение. Одиночное выделение
    /// управляет ИНСПЕКТОРОМ, у которого одна нода на экране; отметка — это набор, который
    /// набирают Ctrl+кликом и который обязан пережить и клик по замечанию валидатора, и переход
    /// по списку прогонов. Слив их в одно поле, мы получили бы инспектор, показывающий случайную
    /// ноду из семи отмеченных.
    /// </summary>
    public bool IsMarked
    {
        get => _isMarked;
        set => SetField(ref _isMarked, value);
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
            case nameof(IsMarked):
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
            RunSubmacroNode n => new RunSubmacroNodeRowViewModel(n),
            FindElementNode n => new FindElementNodeRowViewModel(n),
            WaitForElementNode n => new WaitForElementNodeRowViewModel(n),
            RecognizeTagNode n => new RecognizeTagNodeRowViewModel(n),
            _ => throw new NotSupportedException($"No editor for node type {node.GetType().Name}."),
        };
        row.Editor = node.Editor;
        return row;
    }

    /// <summary>
    /// Создаёт пустую строку запрошенного вида с разумными умолчаниями и свежесгенерированной
    /// подписью вида <c>click-1</c>.
    ///
    /// Имя выдаётся ЗДЕСЬ, а не редактором, потому что правило «от типа ноды» знает
    /// <see cref="MacroNodeNames.Prefix"/>, а тип нода приобретает ровно в этом switch. Второй
    /// карты «вид меню → префикс» в панели заводить нельзя — она разошлась бы с моделью на
    /// первом же новом типе ноды.
    /// </summary>
    /// <param name="kind">Что создавать.</param>
    /// <param name="usedNames">Подписи, уже занятые в графе, — чтобы номер не повторился.</param>
    public static NodeRowViewModel Create(MacroNodeKind kind, IEnumerable<string> usedNames)
    {
        MacroNode node = kind switch
        {
            MacroNodeKind.KeyPress => new KeyPressNode { Key = VirtualKey.F1 },
            MacroNodeKind.Click => new ClickNode { Point = default(ScreenPoint) },
            MacroNodeKind.Delay => new DelayNode { Ms = 1000 },
            MacroNodeKind.AddTag => new AddTagNode { Tag = string.Empty },
            MacroNodeKind.RemoveTag => new RemoveTagNode { Tag = string.Empty },
            MacroNodeKind.SetIcon => new SetIconNode { IconPath = "Assets/ClassIcons/{tag}.png" },
            MacroNodeKind.RunSubmacro => new RunSubmacroNode { SubmacroId = Guid.Empty },
            MacroNodeKind.FindElement => new FindElementNode { Template = string.Empty },
            MacroNodeKind.WaitForElement => new WaitForElementNode { Template = string.Empty, TimeoutMs = 10_000 },
            MacroNodeKind.RecognizeTag => new RecognizeTagNode { TemplateSet = string.Empty, Region = default },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown node kind."),
        };

        return FromNode(node with { DisplayName = MacroNodeNames.Generate(MacroNodeNames.Prefix(node), usedNames) });
    }

    /// <summary>Пункты меню «добавить ноду» во всплывающем списке, в порядке каталога.</summary>
    public static IReadOnlyList<MacroNodeKindOption> Kinds { get; } =
    [
        new(MacroNodeKind.KeyPress, "Нажать клавишу"),
        new(MacroNodeKind.Click, "Клик"),
        new(MacroNodeKind.Delay, "Пауза"),
        new(MacroNodeKind.AddTag, "Добавить тег"),
        new(MacroNodeKind.RemoveTag, "Снять тег"),
        new(MacroNodeKind.SetIcon, "Сменить иконку"),
        new(MacroNodeKind.RunSubmacro, "Запустить под-макрос"),
        new(MacroNodeKind.FindElement, "Найти элемент"),
        new(MacroNodeKind.WaitForElement, "Ждать элемент"),
        new(MacroNodeKind.RecognizeTag, "Распознать тег"),
    ];
}
