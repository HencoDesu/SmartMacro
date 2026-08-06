using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Serilog;
using SmartMacro.App.Services;
using SmartMacro.Contracts.Dto;
using SmartMacro.Native;
using SmartMacro.Resources;

namespace SmartMacro.App.Views;

/// <summary>
/// Окно «выдели область на свежем снимке окна».
///
/// Слой взаимодействия и ничего кроме: панорама, масштаб, выделение мышью, вырезка. Ни одного
/// знания о макросах, бандлах и нодах — всё это приезжает в <see cref="RegionCaptureRequest"/> и
/// уезжает обратно в <see cref="RegionCaptureResult"/>. Кто снимает кадр, окно тоже не знает:
/// снимает <see cref="IWindowCaptureService"/>.
///
/// <b>Координаты.</b> Поверхность (<c>Surface</c>) живёт в пикселях КАДРА, а масштаб и панорама —
/// это её <c>RenderTransform</c>. Так выделение сразу считается в тех числах, которые уйдут в
/// ноду, и второго перевода — а значит, и второго места, где он может разъехаться, — не
/// существует. Экран → кадр: <c>(точка − панорама) / масштаб</c>, ровно как в
/// <see cref="MacrosView"/>, и тоже в одном месте (<see cref="ToFrame"/>).
/// </summary>
public partial class RegionCaptureDialog : Window
{
    /// <summary>Щелчок колеса → множитель масштаба. Тот же шаг, что у канвы редактора.</summary>
    private const double ZoomStep = 1.12;

    /// <summary>Клик по кнопке → множитель масштаба. Грубее колеса, и это намеренно.</summary>
    private const double ZoomButtonStep = 1.25;

    private const double MinZoom = 0.05;
    private const double MaxZoom = 8;

    private enum Gesture
    {
        None,
        Pan,
        Select,
        Pick,
    }

    private readonly RegionCaptureRequest _request;
    private readonly IWindowCaptureService? _capture;
    private readonly RegionCaptureSink? _commit;

    /// <summary>
    /// Имена, уже занятые в наборе. Начинается снимком из запроса и РАСТЁТ на каждое сохранение.
    ///
    /// ⚠️ Без роста предупреждение «такое имя уже есть» работало бы только на первом заходе, а
    /// кнопка «Сохранить и дальше» существует ради одиннадцати заходов подряд — и вторая
    /// «Лучник» там куда вероятнее первой. Сравнение регистронезависимое: файловая система у
    /// бандла всё равно такая, и «лучник» затёр бы «Лучник» молча.
    /// </summary>
    private readonly HashSet<string> _taken;

    private Bitmap? _frame;
    private int _frameWidth;
    private int _frameHeight;

    private double _zoom = 1;
    private double _panX;
    private double _panY;

    private Gesture _gesture;
    private Point _gestureOrigin;
    private double _panOriginX;
    private double _panOriginY;
    private Point _selectionAnchor;

    private ScreenRect _selection;
    private ScreenPoint? _point;
    private int _saved;
    private bool _busy;

    /// <summary>
    /// Ответ. ПОЛЕ С УМОЛЧАНИЕМ, а не результат <c>ShowDialog&lt;T&gt;</c>, — по той же причине,
    /// что и в вопросе о занятом имени: закрыть окно можно крестиком, Esc, Alt+F4 и системой, и
    /// все эти пути обязаны означать «ничего не вырезали».
    ///
    /// ⚠️ После «Сохранить и дальше» здесь лежит последняя УЛОЖЕННАЯ вырезка, и «Отмена» её не
    /// стирает: файлы уже в бандле, и вернуть <c>null</c> значило бы оставить ноду с пустой
    /// областью при полном наборе шаблонов.
    /// </summary>
    private RegionCaptureResult? _result;

    /// <summary>Ответ режима точки. Та же логика поля с умолчанием.</summary>
    private ScreenPoint? _pointResult;

    /// <summary><c>true</c> — окно открыто ради координат клика, а не ради вырезки.</summary>
    private bool IsPointMode => _request.Kind == RegionCaptureKind.Point;

    // Дизайнеру нужен конструктор без параметров; приложение пользуется перегрузкой с запросом.
    public RegionCaptureDialog()
        : this(
            new RegionCaptureRequest(RegionCaptureKind.Template, string.Empty, string.Empty, null,
                string.Empty, [], []),
            capture: null,
            commit: null)
    {
    }

    public RegionCaptureDialog(
        RegionCaptureRequest request,
        IWindowCaptureService? capture,
        RegionCaptureSink? commit)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;
        _capture = capture;
        _commit = commit;
        _taken = new HashSet<string>(request.ExistingNames, StringComparer.OrdinalIgnoreCase);

        InitializeComponent();
        DataContext = request;

        NameBox.Text = request.SuggestedName;
        WindowPicker.ItemsSource = request.Windows.Select(WindowChoice.From).ToList();
        WindowPicker.SelectedIndex = request.Windows.Count > 0 ? 0 : -1;

        ApplyMode();
        RefreshFrameChrome();
        RefreshMarks();
        RefreshVerdict();
    }

    /// <summary>
    /// Расставляет то, что зависит только от режима и за время жизни окна не меняется.
    ///
    /// Одним методом и в конструкторе, а не привязками: режимов три, различий между ними
    /// пять, и разбросанные по разметке <c>IsVisible</c> с конвертерами читались бы хуже, чем
    /// пять строк подряд.
    /// </summary>
    private void ApplyMode()
    {
        NameRow.IsVisible = !IsPointMode;
        // Кнопка «дальше» — только у набора: у одиночного шаблона второго файла не бывает, а у
        // точки нет файла вовсе. Без обратного вызова класть тоже некуда (путь дизайнера).
        SaveAndNextButton.IsVisible = _request.Kind == RegionCaptureKind.Tag && _commit is not null;

        if (!IsPointMode)
        {
            return;
        }

        TitleText.Text = Strings.Dialog_Region_TitlePoint;
        AcceptButton.Content = Strings.Dialog_Region_AcceptPoint;
        ViewHintText.Text = Strings.Dialog_Region_ViewHintPoint;
    }

    /// <summary>Показывает окно модально над <paramref name="owner"/>. <c>null</c> — отменили.</summary>
    public async Task<RegionCaptureResult?> AskAsync(Window owner)
    {
        await ShowDialog(owner);
        return _result;
    }

    /// <summary>То же самое для режима точки. <c>null</c> — отменили.</summary>
    public async Task<ScreenPoint?> AskPointAsync(Window owner)
    {
        await ShowDialog(owner);
        return _pointResult;
    }

    /// <summary>
    /// Первый снимок делается САМ, сразу после открытия, — иначе окно встречает пользователя
    /// пустотой и кнопкой, которую он обязан догадаться нажать. Если окон нет вовсе, вместо кадра
    /// стоит пустое состояние, ведущее к двери «файл с диска».
    ///
    /// Фокус на поле имени тем же приёмом, что и в вопросе о занятом имени: <c>Post</c> с
    /// <c>DispatcherPriority.Loaded</c>, потому что активация окна происходит ПОСЛЕ
    /// <c>OnOpened</c> и гасит признак «фокус пришёл с клавиатуры».
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(() => NameBox.Focus(NavigationMethod.Tab), DispatcherPriority.Loaded);

        if (WindowPicker.SelectedItem is WindowChoice)
        {
            _ = CaptureSelectedWindowAsync();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // Кадр 3840×2160 — это около 33 МБ неуправляемой памяти Skia. Ждать финализатора здесь
        // незачем: окно закрылось, картинка больше никому не нужна.
        _frame?.Dispose();
        _frame = null;
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    // ---- откуда кадр --------------------------------------------------------------------

    private void OnWindowPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && WindowPicker.SelectedItem is WindowChoice)
        {
            _ = CaptureSelectedWindowAsync();
        }
    }

    private void OnRecaptureClicked(object? sender, RoutedEventArgs e) => _ = CaptureSelectedWindowAsync();

    /// <summary>
    /// Снимает выбранное окно и показывает кадр.
    ///
    /// Выделение при этом СОХРАНЯЕТСЯ: «снять заново» существует ровно затем, чтобы поймать
    /// нужное состояние интерфейса игры, не потеряв уже намеченную рамку.
    /// </summary>
    private async Task CaptureSelectedWindowAsync()
    {
        if (_busy || _capture is null || WindowPicker.SelectedItem is not WindowChoice choice)
        {
            return;
        }

        SetBusy(true, Strings.Dialog_Region_Capturing);
        try
        {
            ShowFrame(await _capture.CaptureAsync(choice.Hwnd), fit: _frame is null);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Не удалось снять окно 0x{Hwnd:X} для выбора области", choice.Hwnd);
            ShowProblem(string.Format(CultureInfo.CurrentCulture,
                Strings.Dialog_Region_CaptureFailed, ex.Message));
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    /// <summary>
    /// Дверь «взять файл с диска». Она существует не для удобства, а потому, что <b>окон может не
    /// быть вовсе</b>: игра не запущена — снимать нечего, и без этой двери диалог был бы тупиком.
    /// Заодно ею кладут в макрос картинку, вырезанную когда-то раньше.
    /// </summary>
    private async void OnFromFileClicked(object? sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        IReadOnlyList<IStorageFile> picked;
        try
        {
            picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Strings.Dialog_Region_FilePickerTitle,
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(Strings.Dialog_Region_FileType)
                    {
                        Patterns = ["*.png", "*.bmp", "*.jpg", "*.jpeg"],
                    },
                ],
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Диалог выбора кадра не открылся");
            return;
        }

        if (picked.Count == 0)
        {
            return;
        }

        try
        {
            await using var stream = await picked[0].OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            // Кадр из файла — это ЧУЖОЙ кадр: окно демона к нему отношения не имеет, и оставленный
            // выбор окна сбивал бы с толку («снять заново» подменило бы картинку молча).
            WindowPicker.SelectedIndex = -1;
            ShowFrame(buffer.ToArray(), fit: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Файл {File} не открылся как кадр", picked[0].Name);
            ShowProblem(string.Format(CultureInfo.CurrentCulture,
                Strings.Dialog_Region_FileFailed, ex.Message));
        }
    }

    private void ShowFrame(byte[] png, bool fit)
    {
        Bitmap bitmap;
        try
        {
            using var stream = new MemoryStream(png, writable: false);
            bitmap = new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Кадр не декодировался");
            ShowProblem(string.Format(CultureInfo.CurrentCulture,
                Strings.Dialog_Region_FileFailed, ex.Message));
            return;
        }

        _frame?.Dispose();
        _frame = bitmap;
        _frameWidth = bitmap.PixelSize.Width;
        _frameHeight = bitmap.PixelSize.Height;

        FrameImage.Source = bitmap;
        FrameImage.Width = _frameWidth;
        FrameImage.Height = _frameHeight;
        Surface.Width = _frameWidth;
        Surface.Height = _frameHeight;

        // ⚠️ Выделение и точка переживают пересъёмку — ради этого «снять эту же область в другом
        // окне» и существует, — и НЕ СБРАСЫВАЮТСЯ на кадре меньшего размера, хотя раньше
        // сбрасывались молча. Теперь это ОТКАЗ вслух (см. RefreshVerdict): кадр другого размера
        // означает окно другого размера, а набор, у которого одиннадцать шаблонов сняты одной
        // рамкой, ровно этим и ценен.
        //
        // Почему отказ, а не обрезка: подрезанная по краю кадра рамка дала бы шаблон ДРУГОГО
        // РАЗМЕРА, а сопоставление сравнивает патч фиксированного размера — то есть в наборе
        // появился бы файл, который просто другой. Это та же несогласованность, от которой
        // кнопка спасает, только незаметная.
        if (fit)
        {
            ZoomToFit();
        }
        else
        {
            ApplyTransform();
        }

        ShowProblem(null);
        RefreshFrameChrome();
        RefreshMarks();
        RefreshVerdict();
    }

    /// <summary>
    /// Помещается ли уже намеченная рамка (или точка) в текущий кадр.
    ///
    /// Пустая рамка помещается всегда: «ещё ничего не выделили» — это не отказ, а начало работы.
    /// </summary>
    private bool MarkFitsFrame()
    {
        if (_frame is null)
        {
            return true;
        }

        if (_point is { } point && (point.X >= _frameWidth || point.Y >= _frameHeight))
        {
            return false;
        }

        return _selection is not { Width: > 0, Height: > 0 }
               || (_selection.X + _selection.Width <= _frameWidth
                   && _selection.Y + _selection.Height <= _frameHeight);
    }

    // ---- панорама, масштаб, выделение ----------------------------------------------------

    /// <summary>Экран → пиксели кадра. Единственное место перевода; см. заметку на классе.</summary>
    private Point ToFrame(Point screen) => new((screen.X - _panX) / _zoom, (screen.Y - _panY) / _zoom);

    /// <summary>
    /// Экран → НОМЕР ПИКСЕЛЯ кадра, для режима точки.
    ///
    /// ⚠️ <b>Округление вниз, а не к ближайшему, и это не мелочь.</b> У рамки координаты лежат
    /// НА ГРАНИЦАХ пикселей, поэтому там <c>Math.Round</c> прав. Точка же адресует САМ ПИКСЕЛЬ, а
    /// пиксель <c>N</c> занимает на кадре промежуток <c>[N, N+1)</c>: при увеличении в восемь раз
    /// он показан квадратом в 8 экранных точек, и <c>Round</c> отдавал бы соседний пиксель
    /// каждый раз, когда человек целится в правую или нижнюю половину того, на который смотрит.
    /// На кадре 3840×2160 промах на пиксель — это промах мимо кнопки, ради которой сюда и
    /// пришли, и заметен он был бы только в игре.
    ///
    /// Подрезка по <c>размер − 1</c>, а не по размеру: пикселя с номером <c>_frameWidth</c> не
    /// существует, а щелчок точно по правому краю кадра — обычное дело.
    /// </summary>
    private ScreenPoint ToPixel(Point screen)
    {
        var frame = ToFrame(screen);
        return new ScreenPoint(
            Math.Clamp((int)Math.Floor(frame.X), 0, Math.Max(0, _frameWidth - 1)),
            Math.Clamp((int)Math.Floor(frame.Y), 0, Math.Max(0, _frameHeight - 1)));
    }

    private void OnSurfacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_frame is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(Viewport);
        _gestureOrigin = point.Position;

        // Панорама — средняя ИЛИ правая кнопка. Левая занята выделением, и это главный жест
        // окна; отдавать ему модификатор значило бы требовать второй руки от того, кто целится
        // мышью в иконку размером в палец.
        if (point.Properties.IsMiddleButtonPressed || point.Properties.IsRightButtonPressed)
        {
            _gesture = Gesture.Pan;
            _panOriginX = _panX;
            _panOriginY = _panY;
            e.Pointer.Capture(Viewport);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (IsPointMode)
        {
            // Тащить разрешено намеренно: прицелиться в пиксель проще, поставив точку рядом и
            // подведя её, чем попадая с первого щелчка.
            _gesture = Gesture.Pick;
            _point = ToPixel(point.Position);
        }
        else
        {
            _gesture = Gesture.Select;
            _selectionAnchor = ToFrame(point.Position);
            _selection = default;
        }

        RefreshMarks();
        RefreshVerdict();
        e.Pointer.Capture(Viewport);
        e.Handled = true;
    }

    private void OnSurfacePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_gesture == Gesture.None)
        {
            return;
        }

        var position = e.GetPosition(Viewport);
        if (_gesture == Gesture.Pan)
        {
            _panX = _panOriginX + (position.X - _gestureOrigin.X);
            _panY = _panOriginY + (position.Y - _gestureOrigin.Y);
            ApplyTransform();
            return;
        }

        if (_gesture == Gesture.Pick)
        {
            _point = ToPixel(position);
        }
        else
        {
            _selection = Between(_selectionAnchor, ToFrame(position));
        }

        RefreshMarks();
        RefreshVerdict();
    }

    private void OnSurfacePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_gesture == Gesture.None)
        {
            return;
        }

        _gesture = Gesture.None;
        e.Pointer.Capture(null);
    }

    /// <summary>
    /// Колесо масштабирует ВОКРУГ УКАЗАТЕЛЯ, а не вокруг центра: на кадре 3840×2160 центр окна —
    /// это, как правило, не то место, куда пользователь целится.
    /// </summary>
    private void OnSurfaceWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_frame is null)
        {
            return;
        }

        ZoomAt(e.GetPosition(Viewport), e.Delta.Y > 0 ? ZoomStep : 1 / ZoomStep);
        e.Handled = true;
    }

    private void OnZoomInClicked(object? sender, RoutedEventArgs e) => ZoomAtCentre(ZoomButtonStep);

    private void OnZoomOutClicked(object? sender, RoutedEventArgs e) => ZoomAtCentre(1 / ZoomButtonStep);

    private void OnZoomFitClicked(object? sender, RoutedEventArgs e) => ZoomToFit();

    private void ZoomAtCentre(double factor) =>
        ZoomAt(new Point(Viewport.Bounds.Width / 2, Viewport.Bounds.Height / 2), factor);

    private void ZoomAt(Point anchor, double factor)
    {
        var next = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(next - _zoom) < 0.0001)
        {
            return;
        }

        // Точка кадра под указателем обязана остаться под указателем: считаем её ДО смены
        // масштаба и возвращаем панорамой после.
        var frame = ToFrame(anchor);
        _zoom = next;
        _panX = anchor.X - (frame.X * _zoom);
        _panY = anchor.Y - (frame.Y * _zoom);
        ApplyTransform();
    }

    /// <summary>Вписать кадр целиком — состояние, в котором окно открывается.</summary>
    private void ZoomToFit()
    {
        if (_frameWidth <= 0 || _frameHeight <= 0)
        {
            return;
        }

        var width = Viewport.Bounds.Width;
        var height = Viewport.Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            // Окно ещё не разложено (первый снимок приходит из OnOpened). Досчитаем, когда
            // разложится: Loaded уже прошёл, поэтому просто откладываем на следующий проход.
            Dispatcher.UIThread.Post(ZoomToFit, DispatcherPriority.Loaded);
            return;
        }

        _zoom = Math.Clamp(Math.Min(width / _frameWidth, height / _frameHeight), MinZoom, MaxZoom);
        _panX = (width - (_frameWidth * _zoom)) / 2;
        _panY = (height - (_frameHeight * _zoom)) / 2;
        ApplyTransform();
    }

    private void ApplyTransform()
    {
        Surface.RenderTransform = new TransformGroup
        {
            Children =
            {
                new ScaleTransform(_zoom, _zoom),
                new TranslateTransform(_panX, _panY),
            },
        };
        Surface.RenderTransformOrigin = RelativePoint.TopLeft;

        // Обводки рисуются В КООРДИНАТАХ КАДРА, то есть масштабируются вместе с ним; без обратной
        // компенсации рамка выделения на масштабе 12% превращалась бы в волосок, а на 400% — в
        // жирную полосу, съедающую те самые пиксели, ради которых туда и целятся. Пунктир
        // компенсировать не надо: его шаг задан В ТОЛЩИНАХ обводки, то есть уже относителен.
        var hairline = 1 / _zoom;
        SelectionBox.StrokeThickness = hairline;
        RegionBox.StrokeThickness = hairline;
        PointPixel.StrokeThickness = hairline;

        ZoomText.Text = string.Create(CultureInfo.InvariantCulture, $"{Math.Round(_zoom * 100)}%");

        // Перекрестье живёт в тех же координатах кадра, поэтому его толщина тоже компенсируется:
        // иначе на 12% оно исчезало бы, а на 800% закрывало бы половину пикселя, в который целятся.
        if (_point is not null)
        {
            PlacePointMarks();
        }
    }

    /// <summary>Прямоугольник между двумя точками кадра — целыми пикселями и в его границах.</summary>
    private ScreenRect Between(Point a, Point b)
    {
        var left = (int)Math.Round(Math.Min(a.X, b.X));
        var top = (int)Math.Round(Math.Min(a.Y, b.Y));
        var right = (int)Math.Round(Math.Max(a.X, b.X));
        var bottom = (int)Math.Round(Math.Max(a.Y, b.Y));

        left = Math.Clamp(left, 0, Math.Max(0, _frameWidth));
        top = Math.Clamp(top, 0, Math.Max(0, _frameHeight));
        right = Math.Clamp(right, 0, Math.Max(0, _frameWidth));
        bottom = Math.Clamp(bottom, 0, Math.Max(0, _frameHeight));

        return new ScreenRect(left, top, right - left, bottom - top);
    }

    // ---- подписи и состояние -------------------------------------------------------------

    private void OnNameChanged(object? sender, TextChangedEventArgs e) => RefreshVerdict();

    private void RefreshFrameChrome()
    {
        var has = _frame is not null;
        FrameImage.IsVisible = has;
        EmptyState.IsVisible = !has;
        EmptyState.Text = _request.Windows.Count == 0
            ? Strings.Dialog_Region_NoWindows
            : Strings.Dialog_Region_NoFrame;
        RecaptureButton.IsEnabled = WindowPicker.SelectedItem is WindowChoice;
        FrameText.Text = has
            ? string.Format(CultureInfo.CurrentCulture, Strings.Dialog_Region_Frame, _frameWidth, _frameHeight)
            : string.Empty;
        // Масштабировать нечего, пока нет кадра, — и группа целиком уходит вместе с процентами.
        ZoomGroup.IsVisible = has;
    }

    /// <summary>
    /// Обводки на кадре: рамка выделения с областью-запасом либо перекрестье точки. Один метод на
    /// оба режима, потому что взаимоисключающи они целиком: что не показано, то и погашено.
    /// </summary>
    private void RefreshMarks()
    {
        var hasPoint = IsPointMode && _point is not null;
        PointCrossH.IsVisible = hasPoint;
        PointCrossV.IsVisible = hasPoint;
        PointPixel.IsVisible = hasPoint;
        if (hasPoint)
        {
            PlacePointMarks();
        }

        var live = !IsPointMode && _selection is { Width: > 0, Height: > 0 };
        SelectionBox.IsVisible = live;
        RegionBox.IsVisible = live;
        ShowShade(live);

        if (!live)
        {
            return;
        }

        Place(SelectionBox, _selection);
        Place(RegionBox, SearchRegion.Around(_selection, _frameWidth, _frameHeight));

        // Вуаль вокруг выделения — четыре полосы до краёв кадра.
        Place(ShadeTop, new ScreenRect(0, 0, _frameWidth, _selection.Y));
        Place(ShadeBottom, new ScreenRect(0, _selection.Y + _selection.Height, _frameWidth,
            Math.Max(0, _frameHeight - _selection.Y - _selection.Height)));
        Place(ShadeLeft, new ScreenRect(0, _selection.Y, _selection.X, _selection.Height));
        Place(ShadeRight, new ScreenRect(_selection.X + _selection.Width, _selection.Y,
            Math.Max(0, _frameWidth - _selection.X - _selection.Width), _selection.Height));
    }

    /// <summary>
    /// Ставит перекрестье и квадратик выбранного пикселя.
    ///
    /// Полосы перекрестья тоньше одного пикселя кадра и растут обратно масштабу — на 12% они
    /// иначе исчезли бы вовсе, а на 800% закрыли бы тот самый пиксель, в который целятся.
    /// </summary>
    private void PlacePointMarks()
    {
        if (_point is not { } point)
        {
            return;
        }

        var hairline = Math.Min(1, 1 / _zoom);
        var centreX = point.X + 0.5;
        var centreY = point.Y + 0.5;

        Canvas.SetLeft(PointCrossH, 0);
        Canvas.SetTop(PointCrossH, centreY - (hairline / 2));
        PointCrossH.Width = Math.Max(0, _frameWidth);
        PointCrossH.Height = hairline;

        Canvas.SetLeft(PointCrossV, centreX - (hairline / 2));
        Canvas.SetTop(PointCrossV, 0);
        PointCrossV.Width = hairline;
        PointCrossV.Height = Math.Max(0, _frameHeight);

        Place(PointPixel, new ScreenRect(point.X, point.Y, 1, 1));
    }

    private void ShowShade(bool visible)
    {
        ShadeTop.IsVisible = visible;
        ShadeBottom.IsVisible = visible;
        ShadeLeft.IsVisible = visible;
        ShadeRight.IsVisible = visible;
    }

    private static void Place(Shape shape, ScreenRect rect)
    {
        Canvas.SetLeft(shape, rect.X);
        Canvas.SetTop(shape, rect.Y);
        shape.Width = Math.Max(0, rect.Width);
        shape.Height = Math.Max(0, rect.Height);
    }

    /// <summary>
    /// Пересчитывает всё, что зависит от имени и выделения: путь внутри бандла, отсчёт, претензию
    /// и доступность кнопки. Одним методом, потому что оба поля меняются часто и порознь, а
    /// вердикт у них общий.
    /// </summary>
    private void RefreshVerdict()
    {
        if (IsPointMode)
        {
            RefreshPointVerdict();
            return;
        }

        var name = NameBox.Text?.Trim() ?? string.Empty;
        PathText.Text = _request.PathPreview(name);

        var live = _selection is { Width: > 0, Height: > 0 };
        if (live)
        {
            var region = SearchRegion.Around(_selection, _frameWidth, _frameHeight);
            MeasureText.Text = string.Format(
                CultureInfo.CurrentCulture,
                Strings.Dialog_Region_Measure,
                _selection.X, _selection.Y, _selection.Width, _selection.Height,
                region.Width, region.Height, SearchRegion.Margin);
        }
        else
        {
            MeasureText.Text = string.Empty;
        }

        var fits = MarkFitsFrame();

        string? problem = null;
        if (_frame is null)
        {
            problem = null; // пустое состояние уже всё сказало посреди окна
        }
        else if (!live)
        {
            problem = Strings.Dialog_Region_NoSelection;
        }
        else if (!fits)
        {
            // Кадр меньше уже намеченной рамки. Отказ, а не обрезка — довод у ShowFrame.
            problem = string.Format(CultureInfo.CurrentCulture,
                Strings.Dialog_Region_SelectionOutsideFrame, _frameWidth, _frameHeight);
        }
        else if (name.Length == 0)
        {
            problem = Strings.Dialog_Region_NameEmpty;
        }
        else if (!_request.IsNameUsable(name))
        {
            problem = Strings.Dialog_Region_NameBad;
        }
        else if (_taken.Contains(name))
        {
            // Предупреждение, а не запрет: заменить свой же шаблон свежей вырезкой — это ровно то,
            // ради чего сюда чаще всего и приходят второй раз. Список растёт по ходу захода,
            // поэтому вторая «Лучник» подряд предупреждение получит.
            problem = string.Format(CultureInfo.CurrentCulture, Strings.Dialog_Region_NameTaken, name);
        }

        ProblemText.Text = problem ?? string.Empty;
        ProblemText.IsVisible = problem is not null;
        // Занятое имя кнопку НЕ гасит: заменить свой же шаблон свежей вырезкой — обычное дело, и
        // предупреждения об этом достаточно. Негодное имя гасит: файл лёг бы туда, где нода его
        // никогда не найдёт. Не поместившаяся рамка гасит тоже, и по той же причине.
        var usable = !_busy && _frame is not null && live && fits && _request.IsNameUsable(name);
        AcceptButton.IsEnabled = usable;
        SaveAndNextButton.IsEnabled = usable;
    }

    /// <summary>Тот же вердикт для режима точки: отсчёт, претензия, доступность кнопки.</summary>
    private void RefreshPointVerdict()
    {
        PathText.Text = string.Empty;

        var fits = MarkFitsFrame();
        MeasureText.Text = _point is { } point
            ? string.Format(CultureInfo.CurrentCulture, Strings.Dialog_Region_PointMeasure, point.X, point.Y)
            : string.Empty;

        string? problem = null;
        if (_frame is null)
        {
            problem = null;
        }
        else if (_point is null)
        {
            problem = Strings.Dialog_Region_NoPoint;
        }
        else if (!fits)
        {
            problem = string.Format(CultureInfo.CurrentCulture,
                Strings.Dialog_Region_SelectionOutsideFrame, _frameWidth, _frameHeight);
        }

        ProblemText.Text = problem ?? string.Empty;
        ProblemText.IsVisible = problem is not null;
        AcceptButton.IsEnabled = !_busy && _frame is not null && _point is not null && fits;
    }

    private void SetBusy(bool busy, string? note)
    {
        _busy = busy;
        WindowPicker.IsEnabled = !busy;
        RecaptureButton.IsEnabled = !busy && WindowPicker.SelectedItem is WindowChoice;
        if (note is not null)
        {
            FrameText.Text = note;
        }
        else
        {
            // Возвращаем подпись кадра на место: пока шёл снимок, на ней стояло «Снимаю окно…».
            RefreshFrameChrome();
        }

        RefreshVerdict();
    }

    private void ShowProblem(string? problem)
    {
        ProblemText.Text = problem ?? string.Empty;
        ProblemText.IsVisible = problem is not null;
    }

    // ---- исход --------------------------------------------------------------------------

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Вырезает выделение и закрывает окно.
    ///
    /// <b>Вырезка идёт ТОЧНО по выделению</b>, а запас достаётся только области ноды: шаблон с
    /// полями вокруг — это шаблон, который сопоставляется с фоном, а фон в игре меняется.
    /// </summary>
    private void OnAcceptClicked(object? sender, RoutedEventArgs e)
    {
        if (IsPointMode)
        {
            if (_point is not null && MarkFitsFrame())
            {
                _pointResult = _point;
                Close();
            }

            return;
        }

        if (TakeCrop() is not null)
        {
            Close();
        }
    }

    /// <summary>
    /// «Сохранить и дальше»: кладёт вырезку и переходит к следующему окну ТОЙ ЖЕ рамкой.
    ///
    /// <b>Порядок несущий.</b> Сперва сохранить, потом менять кадр: наоборот — и вырезка уехала
    /// бы из уже подменённого окна. Рамка при этом не трогается вообще: она и есть то, ради чего
    /// кнопка существует, и любой её пересчёт под новый кадр вернул бы одиннадцать чуть разных
    /// шаблонов.
    ///
    /// Окно переключается по кругу и только когда их больше одного: у кадра, взятого файлом с
    /// диска, выбор окна пуст, и прыгать на первое попавшееся значило бы подменить картинку без
    /// спроса.
    /// </summary>
    private void OnSaveAndNextClicked(object? sender, RoutedEventArgs e)
    {
        if (TakeCrop() is not { } crop)
        {
            return;
        }

        _saved++;
        _taken.Add(crop.Name);
        NameBox.Text = string.Empty;

        // ⚠️ Отчёт живёт в СВОЕЙ строке, а не в отсчёте выделения. Смена окна ниже запускает
        // снимок, а тот в конце зовёт RefreshVerdict, который отсчёт перепишет: «сохранено»
        // мигнуло бы и исчезло ровно тогда, когда его читают.
        SavedText.Text = string.Format(
            CultureInfo.CurrentCulture, Strings.Dialog_Region_Saved, crop.Name, _saved);

        var windows = WindowPicker.ItemCount;
        if (windows > 1 && WindowPicker.SelectedIndex >= 0)
        {
            // SelectionChanged сам снимет следующий кадр; выделение переживёт пересъёмку.
            WindowPicker.SelectedIndex = (WindowPicker.SelectedIndex + 1) % windows;
        }

        RefreshVerdict();
        NameBox.Focus(NavigationMethod.Tab);
    }

    /// <summary>
    /// Вырезает выделение и отдаёт его редактору. <c>null</c> — не вышло, и окно остаётся
    /// открытым с объяснением внизу.
    ///
    /// <b>Вырезка идёт ТОЧНО по выделению</b>, а запас достаётся только области ноды: шаблон с
    /// полями вокруг — это шаблон, который сопоставляется с фоном, а фон в игре меняется.
    /// </summary>
    private RegionCaptureResult? TakeCrop()
    {
        if (_frame is not { } frame || _selection is not { Width: > 0, Height: > 0 } || !MarkFitsFrame())
        {
            return null;
        }

        var name = NameBox.Text?.Trim() ?? string.Empty;
        if (!_request.IsNameUsable(name))
        {
            return null;
        }

        byte[] png;
        try
        {
            png = Crop(frame, _selection);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Вырезка {Rect} из кадра не удалась", _selection);
            ShowProblem(string.Format(CultureInfo.CurrentCulture,
                Strings.Dialog_Region_CropFailed, ex.Message));
            return null;
        }

        var crop = new RegionCaptureResult(name, png, _selection, _frameWidth, _frameHeight);

        // Кладёт РЕДАКТОР: диалог не знает ни про бандл, ни про библиотеку макросов. Отказ
        // остаётся здесь — окно не закрывается, имя не стирается, чинить придётся на месте.
        if (_commit?.Invoke(crop) is { } refusal)
        {
            ShowProblem(string.Format(
                CultureInfo.CurrentCulture, Strings.Dialog_Region_SaveFailed, name, refusal));
            return null;
        }

        _result = crop;
        return crop;
    }

    /// <summary>
    /// Вырезка средствами Avalonia, без второй библиотеки изображений.
    ///
    /// <c>RenderTargetBitmap</c> отдаёт PNG своим <c>Save</c>, а плотность и у него, и у
    /// декодированного кадра равна 96 точкам на дюйм, поэтому единица <c>Rect</c> здесь — ровно
    /// один пиксель, и переводить ничего не нужно. Кадр приходит из <c>PrintWindow</c>, то есть
    /// плотность у него всегда своя, стандартная.
    /// </summary>
    private static byte[] Crop(Bitmap frame, ScreenRect rect)
    {
        using var target = new RenderTargetBitmap(new PixelSize(rect.Width, rect.Height));
        using (var context = target.CreateDrawingContext())
        {
            context.DrawImage(
                frame,
                new Rect(rect.X, rect.Y, rect.Width, rect.Height),
                new Rect(0, 0, rect.Width, rect.Height));
        }

        using var buffer = new MemoryStream();
        target.Save(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Строка списка окон. <c>ToString</c>, а не <c>DataTemplate</c>: одна строка текста без
    /// единого элемента оформления, и шаблон ради неё был бы разметкой ради разметки.
    /// </summary>
    private sealed record WindowChoice(long Hwnd, string Label)
    {
        public static WindowChoice From(WindowDto window) => new(
            window.Hwnd,
            string.Format(
                CultureInfo.CurrentCulture,
                Strings.Dialog_Region_WindowItem,
                window.Hwnd,
                window.ProcessName,
                window.Tags.Count == 0
                    ? Strings.Dialog_Region_WindowNoTags
                    : string.Join(", ", window.Tags)));

        public override string ToString() => Label;
    }
}
