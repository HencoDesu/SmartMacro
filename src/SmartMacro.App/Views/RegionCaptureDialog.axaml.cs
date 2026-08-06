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
    }

    private readonly RegionCaptureRequest _request;
    private readonly IWindowCaptureService? _capture;

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
    private bool _busy;

    /// <summary>
    /// Ответ. ПОЛЕ С УМОЛЧАНИЕМ, а не результат <c>ShowDialog&lt;T&gt;</c>, — по той же причине,
    /// что и в вопросе о занятом имени: закрыть окно можно крестиком, Esc, Alt+F4 и системой, и
    /// все эти пути обязаны означать «ничего не вырезали».
    /// </summary>
    private RegionCaptureResult? _result;

    // Дизайнеру нужен конструктор без параметров; приложение пользуется перегрузкой с запросом.
    public RegionCaptureDialog()
        : this(
            new RegionCaptureRequest(RegionCaptureKind.Template, string.Empty, string.Empty, null,
                string.Empty, [], []),
            capture: null)
    {
    }

    public RegionCaptureDialog(RegionCaptureRequest request, IWindowCaptureService? capture)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;
        _capture = capture;

        InitializeComponent();
        DataContext = request;

        NameBox.Text = request.SuggestedName;
        WindowPicker.ItemsSource = request.Windows.Select(WindowChoice.From).ToList();
        WindowPicker.SelectedIndex = request.Windows.Count > 0 ? 0 : -1;

        RefreshFrameChrome();
        RefreshSelectionChrome();
        RefreshVerdict();
    }

    /// <summary>Показывает окно модально над <paramref name="owner"/>. <c>null</c> — отменили.</summary>
    public async Task<RegionCaptureResult?> AskAsync(Window owner)
    {
        await ShowDialog(owner);
        return _result;
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

        // Выделение переживает пересъёмку — «снять заново» затем и нужно, чтобы поймать нужное
        // состояние интерфейса, не потеряв уже намеченную рамку, — но не переезд на кадр меньшего
        // размера: рамка за краем нового кадра указывает на область, которой в окне нет.
        if (_selection.X + _selection.Width > _frameWidth || _selection.Y + _selection.Height > _frameHeight)
        {
            _selection = default;
        }

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
        RefreshSelectionChrome();
        RefreshVerdict();
    }

    // ---- панорама, масштаб, выделение ----------------------------------------------------

    /// <summary>Экран → пиксели кадра. Единственное место перевода; см. заметку на классе.</summary>
    private Point ToFrame(Point screen) => new((screen.X - _panX) / _zoom, (screen.Y - _panY) / _zoom);

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

        _gesture = Gesture.Select;
        _selectionAnchor = ToFrame(point.Position);
        _selection = default;
        RefreshSelectionChrome();
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

        _selection = Between(_selectionAnchor, ToFrame(position));
        RefreshSelectionChrome();
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

        ZoomText.Text = string.Create(CultureInfo.InvariantCulture, $"{Math.Round(_zoom * 100)}%");
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

    private void RefreshSelectionChrome()
    {
        var live = _selection is { Width: > 0, Height: > 0 };
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

        string? problem = null;
        if (_frame is null)
        {
            problem = null; // пустое состояние уже всё сказало посреди окна
        }
        else if (!live)
        {
            problem = Strings.Dialog_Region_NoSelection;
        }
        else if (name.Length == 0)
        {
            problem = Strings.Dialog_Region_NameEmpty;
        }
        else if (!_request.IsNameUsable(name))
        {
            problem = Strings.Dialog_Region_NameBad;
        }
        else if (_request.ExistingNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            // Предупреждение, а не запрет: заменить свой же шаблон свежей вырезкой — это ровно то,
            // ради чего сюда чаще всего и приходят второй раз.
            problem = string.Format(CultureInfo.CurrentCulture, Strings.Dialog_Region_NameTaken, name);
        }

        ProblemText.Text = problem ?? string.Empty;
        ProblemText.IsVisible = problem is not null;
        // Занятое имя кнопку НЕ гасит: заменить свой же шаблон свежей вырезкой — обычное дело, и
        // предупреждения об этом достаточно. Негодное имя гасит: файл лёг бы туда, где нода его
        // никогда не найдёт.
        AcceptButton.IsEnabled = !_busy && _frame is not null && live && _request.IsNameUsable(name);
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
        if (_frame is not { } frame || _selection is not { Width: > 0, Height: > 0 })
        {
            return;
        }

        var name = NameBox.Text?.Trim() ?? string.Empty;
        if (!_request.IsNameUsable(name))
        {
            return;
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
            return;
        }

        _result = new RegionCaptureResult(name, png, _selection, _frameWidth, _frameHeight);
        Close();
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
