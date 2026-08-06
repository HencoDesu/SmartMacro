using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Serilog;
using SmartMacro.App.ViewModels;
using SmartMacro.App.ViewModels.Canvas;
using SmartMacro.App.ViewModels.Nodes;
using SmartMacro.Resources;

namespace SmartMacro.App.Views;

/// <summary>
/// «Макросы» — слой взаимодействия у редактора на canvas.
///
/// Всё, что пользователь может СДЕЛАТЬ с графом напрямую, живёт здесь: панорама, масштаб,
/// перетаскивание коробки, вытягивание связи из порта. Всё, что меняет граф, уходит прямиком
/// обратно в <see cref="MacroEditorViewModel"/> — этот класс не владеет никаким состоянием
/// модели, только мимолётным «что сейчас под указателем» у жеста в полёте.
///
/// Координаты: поверхность — это <see cref="Canvas"/> под render transform из масштаба и
/// сдвига. Экран → canvas считается как <c>(экран - панорама) / масштаб</c>, и делается это в
/// одном месте (<see cref="ToCanvas"/>), чтобы указатель и коробки не могли разъехаться;
/// обратный ход нужен только <see cref="ApplyTransform"/>, который и есть сам transform.
/// </summary>
public partial class MacrosView : UserControl
{
    /// <summary>Щелчок колеса → множитель масштаба.</summary>
    private const double ZoomStep = 1.12;

    /// <summary>Клик по кнопке → множитель масштаба. Грубее колеса, и это намеренно.</summary>
    private const double ZoomButtonStep = 1.25;

    /// <summary>Размер поверхности, а значит, и то, как далеко можно утащить коробку.</summary>
    private const double SurfaceExtent = 4000;

    private enum Gesture
    {
        None,
        Pan,
        DragNode,
        DragLink,
    }

    private Gesture _gesture;
    private Point _gestureOrigin;
    private double _panOriginX;
    private double _panOriginY;
    private NodeRowViewModel? _draggedNode;
    private Point _dragOffset;
    private NodeEdgeViewModel? _draggedEdge;
    private MacroEditorViewModel? _watched;
    private TemplatesViewModel? _watchedTemplates;
    private Bitmap? _templatePreview;

    /// <summary>
    /// Подгоняет «0:12.4» у отладчика.
    ///
    /// Часы живут ЗДЕСЬ, а не во view-model, потому что <c>DispatcherTimer</c> — тип Avalonia, а
    /// каждую view-model этой сборки гоняют headless. View-model выставляет наружу
    /// <c>TickElapsed()</c>, который тест может вызвать напрямую; частота тика — решение об
    /// отрисовке и лежит по эту сторону границы.
    ///
    /// 100 мс — потому что на экране один знак после запятой в секундах. Пока не выбран живой
    /// обход, он не поднимает вообще ничего, так что простаивающая панель платит один пустой
    /// вызов на тик.
    /// </summary>
    private readonly DispatcherTimer _elapsedTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    /// <summary>
    /// Часы автосохранения — по тому же правилу, что и часы выше: <c>DispatcherTimer</c> живёт в
    /// виде, а view-model выставляет наружу <c>TickAutoSave()</c>, который тест дёргает напрямую.
    /// Ни одна проверка автосохранения поэтому не ждёт настоящих секунд.
    ///
    /// Период и число тихих тиков — оба у view-model
    /// (<see cref="MacroEditorViewModel.AutoSaveTick"/>,
    /// <see cref="MacroEditorViewModel.AutoSaveQuietTicks"/>): «через сколько записываем»
    /// обязано читаться в одном месте, а не по половине на файл.
    ///
    /// Часы НЕ останавливаются при уходе из режима «Макросы» — виды переключаются видимостью, и
    /// правка, сделанная за секунду до перехода в «Настройки», обязана дописаться так же, как
    /// любая другая.
    /// </summary>
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = MacroEditorViewModel.AutoSaveTick };

    /// <summary>
    /// <b>InitializeComponent, а НЕ AvaloniaXamlLoader.Load.</b> Выглядят они равнозначно, но
    /// равнозначны не являются: генератор имён Avalonia кладёт присваивания полей <c>x:Name</c>
    /// ВНУТРЬ сгенерированного <c>InitializeComponent</c>, так что вызов одного лишь загрузчика
    /// поднимает XAML и оставляет каждое именованное поле нулевым. Этот вид первым тронул
    /// именованный контрол, и отказ тут злой: первое разыменование null попадает в
    /// <c>OnDataContextChanged</c>, а исключение оттуда обрывает распространение DataContext по
    /// детям, — и вся панель рисуется с молча пустыми привязками и каждым <c>IsVisible</c>,
    /// вернувшимся к умолчанию. Соседние виды перевели тоже: именованных контролов у них сегодня
    /// нет, поэтому голый загрузчик работал, — но это была ловушка, взведённая на того, кто
    /// добавит первый <c>x:Name</c>.
    /// </summary>
    public MacrosView()
    {
        InitializeComponent();
        _elapsedTimer.Tick += (_, _) => Vm?.TickElapsed();
        _autoSaveTimer.Tick += (_, _) => Vm?.TickAutoSave();
    }

    private MacroEditorViewModel? Vm => DataContext as MacroEditorViewModel;

    /// <summary>Браузер шаблонов открытого макроса — раздел инспектора, живёт внутри редактора.</summary>
    private TemplatesViewModel? TemplatesVm => Vm?.Templates;

    // ---- библиотека ---------------------------------------------------------------------

    private void OnNewMacroClicked(object? sender, RoutedEventArgs e) => Vm?.NewMacro();

    /// <summary>
    /// Строки библиотеки — не <c>ListBoxItem</c> (список представляет собой дерево групп),
    /// поэтому открытие макроса — это обычное нажатие по строке. Кнопки внутри строки помечают
    /// своё нажатие обработанным, так что «Запустить» заодно не открывает макрос.
    /// </summary>
    private void OnMacroRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Control { DataContext: MacroListItemViewModel item })
        {
            return;
        }

        // ⚠️ Найдено глазами: строка макроса, УЖЕ выбранного, ничего не делала — а с открытым
        // под-макросом это значит «клик по родителю в дереве не возвращает к родителю». Дерево и
        // есть навигация, и строка обязана вести туда, что на ней написано; сеттер SelectedMacro
        // на неизменившемся значении выходит сразу и до канвы не доходит.
        if (vm.IsSubmacroOpen && vm.SelectedMacro?.Name == item.Name)
        {
            vm.OpenParentGraph();
            return;
        }

        vm.SelectedMacro = item;
    }

    private void OnRunMacroClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: MacroListItemViewModel item })
        {
            vm.Run(item);
        }
    }

    private async void OnStopMacroClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: MacroListItemViewModel item })
        {
            await vm.StopAsync(item);
        }
    }

    private void OnDeleteMacroClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: MacroListItemViewModel item })
        {
            vm.DeleteMacro(item);
        }
    }

    // ---- под-макросы (F4) -----------------------------------------------------------------

    /// <summary>
    /// Клик по вложенной строке дерева открывает функцию на канве. Если открыт другой макрос,
    /// сперва открываем её МАКРОС: функция без своего бандла не существует, а перескочить в неё
    /// мимо родителя означало бы показать граф без того, что его вызывает.
    /// </summary>
    private void OnSubmacroRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Control { DataContext: SubmacroListItemViewModel item })
        {
            return;
        }

        if (vm.SelectedMacro?.Name != item.MacroName)
        {
            vm.SelectedMacro = vm.Macros.FirstOrDefault(macro =>
                string.Equals(macro.Name, item.MacroName, StringComparison.Ordinal));
        }

        vm.OpenSubmacro(item.Id);
    }

    private void OnDeleteSubmacroClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: SubmacroListItemViewModel item })
        {
            vm.DeleteSubmacro(item.Id);
        }
    }

    private void OnOpenParentGraphClicked(object? sender, RoutedEventArgs e) => Vm?.OpenParentGraph();

    private void OnAddSubmacroClicked(object? sender, RoutedEventArgs e) => Vm?.AddSubmacro();

    private void OnExtractSubmacroClicked(object? sender, RoutedEventArgs e) => Vm?.ExtractSubmacro();

    /// <summary>
    /// «Импорт» — системный диалог выбора <c>.hsm</c>, дальше файл просто копируется в
    /// <c>macros/</c>. Это весь импорт целиком: с волны F3 библиотека — это папка, а панель —
    /// её автор.
    /// </summary>
    private async void OnImportMacroClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        IReadOnlyList<IStorageFile> picked;
        try
        {
            picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Strings.Dialog_ImportMacro_Title,
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType(Strings.Dialog_MacroFileType) { Patterns = ["*.hsm"] }],
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Диалог импорта макроса не открылся");
            return;
        }

        foreach (var file in picked)
        {
            // Локальный путь, а не поток: импорт — это File.Copy, и бандл обязан доехать байт в
            // байт. Файл из места без пути (облачный провайдер) импортировать нечем, и сказать
            // об этом честнее, чем пересобрать бандл сегодняшним писателем и потерять то, чего
            // сегодняшняя версия формата не знает.
            if (file.TryGetLocalPath() is { Length: > 0 } path)
            {
                // Последовательно, а не пачкой: на занятом имени импорт спрашивает, и два вопроса
                // одновременно — это два модальных окна поверх друг друга.
                await vm.ImportMacroAsync(path);
            }
            else
            {
                vm.ErrorMessage = string.Format(CultureInfo.CurrentCulture,
                    Strings.Editor_Status_ImportNotOnDisk, file.Name);
            }
        }
    }

    /// <summary>«Экспорт» — системный диалог сохранения, дальше файл отдаётся как есть.</summary>
    private async void OnExportMacroClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm
            || sender is not Button { DataContext: MacroListItemViewModel item }
            || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        if (vm.ExportPath(item) is not { } source)
        {
            vm.ErrorMessage = string.Format(CultureInfo.CurrentCulture,
                Strings.Editor_Status_ExportFileMissing, item.Name);
            return;
        }

        try
        {
            var target = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Strings.Dialog_ExportMacro_Title,
                SuggestedFileName = item.Name + ".hsm",
                DefaultExtension = "hsm",
                FileTypeChoices = [new FilePickerFileType(Strings.Dialog_MacroFileType) { Patterns = ["*.hsm"] }],
            });

            if (target?.TryGetLocalPath() is { Length: > 0 } destination)
            {
                File.Copy(source, destination, overwrite: true);
                vm.StatusMessage = string.Format(CultureInfo.CurrentCulture,
                    Strings.Editor_Status_Exported, item.Name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            vm.ErrorMessage = string.Format(CultureInfo.CurrentCulture,
                Strings.Editor_Status_ExportFailed, ex.Message);
            Log.Warning(ex, "Экспорт макроса '{Macro}' не выполнен", item.Name);
        }
    }

    // ---- шаблоны макроса (F2) -------------------------------------------------------------

    /// <summary>
    /// Клик по строке шаблона — выделение, ровно как у строк библиотеки: это тоже не
    /// <c>ListBoxItem</c>, а строки внутри дерева групп.
    /// </summary>
    private void OnTemplateRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TemplatesVm is { } templates && sender is Control { DataContext: TemplateRowViewModel row })
        {
            templates.Selected = row;
        }
    }

    private void OnDeleteTemplateClicked(object? sender, RoutedEventArgs e)
    {
        if (TemplatesVm is { } templates && sender is Button { DataContext: TemplateRowViewModel row })
        {
            templates.Delete(row);
        }
    }

    /// <summary>
    /// «+ файл…» — системный диалог выбора PNG, дальше байты уезжают в бандл.
    ///
    /// Множественный выбор разрешён: одиннадцать имён классов кладут в набор одной пачкой, а не
    /// одиннадцатью походами в диалог. Каждый файл кладётся отдельно — бандл переписывается на
    /// каждый, но это десятки килобайт, а взамен один битый файл не отменяет остальных.
    /// </summary>
    private async void OnAddTemplateClicked(object? sender, RoutedEventArgs e)
    {
        if (TemplatesVm is not { } templates || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        IReadOnlyList<IStorageFile> picked;
        try
        {
            picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Strings.Dialog_ImportTemplate_Title,
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("PNG") { Patterns = ["*.png"] }],
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Диалог выбора шаблона не открылся");
            return;
        }

        foreach (var file in picked)
        {
            try
            {
                await using var stream = await file.OpenReadAsync();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                templates.Import(file.Name, buffer.ToArray());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "Файл шаблона {File} не прочитался", file.Name);
            }
        }
    }

    /// <summary>
    /// «Выделить на снимке…» — вся содержательная часть у view-model, здесь только доставка
    /// строки ноды из <c>DataContext</c> кнопки.
    ///
    /// <c>async void</c>, как и соседние обработчики диалогов: обработчик события Avalonia другой
    /// формы не имеет, а всё, что могло бы упасть, view-model ловит у себя и превращает в красную
    /// строку.
    /// </summary>
    private async void OnCaptureRegionClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: NodeRowViewModel row })
        {
            await vm.CaptureRegionAsync(row);
        }
    }

    /// <summary>
    /// «Указать на снимке…» у ноды клика — то же самое окно, но за одной точкой. Содержательная
    /// часть у view-model; здесь только доставка строки ноды из <c>DataContext</c> кнопки.
    /// </summary>
    private async void OnPickPointClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: NodeRowViewModel row })
        {
            await vm.PickClickPointAsync(row);
        }
    }

    /// <summary>
    /// Декодирование превью шаблона.
    ///
    /// <b>Здесь, а не в конвертере привязки</b>, и это то же решение, что стояло в удалённом
    /// <c>TemplatesView</c>. View-model отдаёт БАЙТЫ и про Avalonia не знает — это условие того,
    /// что её гоняют headless. Значит, кто-то должен превратить их в <see cref="Bitmap"/>, а тот
    /// держит неуправляемую память Skia и требует освобождения. Конвертер, вызываемый на каждое
    /// обновление привязки, оставлял бы каждую предыдущую картинку финализатору; здесь живая
    /// картинка ровно одна, и следующая вытесняет предыдущую.
    /// </summary>
    private void UpdateTemplatePreview()
    {
        _templatePreview?.Dispose();
        _templatePreview = null;

        if (TemplatesVm?.PreviewPng is { Length: > 0 } bytes)
        {
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                _templatePreview = new Bitmap(stream);
            }
            catch (Exception ex)
            {
                // Запись лежит в бандле, но картинкой не является. Демон отдал её честно —
                // ругаться должен вид, а не превращать это в отказ запроса.
                Log.Warning(ex, "Не удалось декодировать превью шаблона");
            }
        }

        TemplatePreviewImage.Source = _templatePreview;
    }

    // ---- триггеры --------------------------------------------------------------------

    private void OnAddHotkeyTriggerClicked(object? sender, RoutedEventArgs e) =>
        Vm?.AddTrigger(MacroTriggerKind.Hotkey);

    private void OnAddProcessTriggerClicked(object? sender, RoutedEventArgs e) =>
        Vm?.AddTrigger(MacroTriggerKind.ProcessAppeared);

    private void OnRemoveTriggerClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: TriggerRowViewModel row })
        {
            vm.RemoveTrigger(row);
        }
    }

    // ---- бейдж целей (1g) ----------------------------------------------------------------

    /// <summary>
    /// «контекст-окно» во всплывающем окне бейджа. ВЫКЛЮЧИТЬ селектор — не то же самое, что
    /// очистить поля тегов (см. <see cref="TargetSelectorViewModel.UseSelector"/>), поэтому теги
    /// оставляют в покое, и они возвращаются, если пользователь переключит обратно.
    /// </summary>
    private void OnTargetContextClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TargetSelectorViewModel target })
        {
            target.UseSelector = false;
        }
    }

    private void OnTargetTagsClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TargetSelectorViewModel target })
        {
            target.UseSelector = true;
        }
    }

    private void OnRemoveSelectorTagClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SelectorTagChipViewModel chip })
        {
            chip.Remove();
        }
    }

    /// <summary>
    /// Enter фиксирует поле «+ тег». Моделью под ним остаётся тот же текст через запятую, так
    /// что добавленный здесь тег неотличим от набранного в поле инспектора.
    /// </summary>
    private void OnRequireTagKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is Control { DataContext: TargetSelectorViewModel target })
        {
            target.CommitRequireTag();
            e.Handled = true;
        }
    }

    private void OnExcludeTagKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is Control { DataContext: TargetSelectorViewModel target })
        {
            target.CommitExcludeTag();
            e.Handled = true;
        }
    }

    // ---- ноды -----------------------------------------------------------------------

    private void OnAddNodeClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: MacroNodeKindOption option })
        {
            vm.AddNode(option.Kind);
            AddNodeButton.Flyout?.Hide();
        }
    }

    private void OnDeleteNodeClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { SelectedNode: { } row } vm)
        {
            vm.DeleteNode(row);
        }
    }

    private void OnIssueClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Button { DataContext: ValidationIssueViewModel issue })
        {
            vm.SelectIssue(issue);
        }
    }

    // ---- действия редактора --------------------------------------------------------------

    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            await vm.SaveAsync();
        }
    }

    // ---- переименование файла макроса -----------------------------------------------------
    //
    // Автосохранение пишет в ЗАГРУЖЕННОЕ имя и файлов не плодит, поэтому момент «имя набрано
    // целиком» называет человек. Способов три и они равнозначны: Enter, уход фокуса и кнопка
    // «Сохранить». Esc возвращает то, как файл называется на самом деле.

    private async void OnMacroNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                // force: Enter — прямое действие, и спросить про занятое имя надо даже если на
                // него уже отвечали отказом.
                await vm.CommitRenameAsync(force: true);
                break;

            case Key.Escape:
                e.Handled = true;
                vm.CancelRename();
                break;
        }
    }

    private async void OnMacroNameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            await vm.CommitRenameAsync();
        }
    }

    private void OnReloadClicked(object? sender, RoutedEventArgs e) => Vm?.ReloadFromDisk();

    private void OnAutoLayoutClicked(object? sender, RoutedEventArgs e) => Vm?.AutoLayout();

    // ---- переключатель прогонов -------------------------------------------------------------

    private void OnPreviousRunClicked(object? sender, RoutedEventArgs e) => Vm?.SelectPreviousRun();

    private void OnNextRunClicked(object? sender, RoutedEventArgs e) => Vm?.SelectNextRun();

    private void OnClearRunLogClicked(object? sender, RoutedEventArgs e) => Vm?.ClearRunLog();

    // ---- отладчик (D5) --------------------------------------------------------------------

    private async void OnDebugPauseClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            await vm.PauseAsync();
        }
    }

    private async void OnDebugResumeClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            await vm.ResumeAsync();
        }
    }

    private async void OnDebugStepClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            await vm.StepAsync();
        }
    }

    private async void OnDebugRunToCursorClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            await vm.RunToCursorAsync();
        }
    }

    private async void OnDebugStopClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            await vm.StopSelectedRunAsync();
        }
    }

    // ---- панель переменных -----------------------------------------------------------------

    private void OnVariableHovered(object? sender, PointerEventArgs e)
    {
        if (Vm is { } vm && sender is Control { DataContext: MacroVariableRowViewModel row })
        {
            vm.HighlightVariable(row);
        }
    }

    private void OnVariableUnhovered(object? sender, PointerEventArgs e) => Vm?.HighlightVariable(null);

    private void OnOpenFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = vm.FolderPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            vm.ErrorMessage = string.Format(CultureInfo.CurrentCulture,
                Strings.Editor_Status_OpenFolderFailed, ex.Message);
        }
    }

    // ---- масштаб --------------------------------------------------------------------------

    private void OnZoomInClicked(object? sender, RoutedEventArgs e) => ZoomAboutCentre(ZoomButtonStep);

    private void OnZoomOutClicked(object? sender, RoutedEventArgs e) => ZoomAboutCentre(1 / ZoomButtonStep);

    private void OnZoomResetClicked(object? sender, RoutedEventArgs e)
    {
        Vm?.ResetView();
        ApplyTransform();
    }

    private void OnViewportWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        var factor = e.Delta.Y > 0 ? ZoomStep : 1 / ZoomStep;
        ZoomAbout(e.GetPosition(Viewport), factor);
        e.Handled = true;
    }

    private void ZoomAboutCentre(double factor) =>
        ZoomAbout(new Point(Viewport.Bounds.Width / 2, Viewport.Bounds.Height / 2), factor);

    /// <summary>Масштабирует так, чтобы точка canvas под <paramref name="pivot"/> осталась на месте.</summary>
    private void ZoomAbout(Point pivot, double factor)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        var anchor = ToCanvas(pivot);
        vm.Zoom *= factor;
        vm.PanX = pivot.X - (anchor.X * vm.Zoom);
        vm.PanY = pivot.Y - (anchor.Y * vm.Zoom);
        ApplyTransform();
    }

    // ---- жесты на canvas -------------------------------------------------------------------

    /// <summary>
    /// Нажатие, дошедшее до самой области просмотра: указатель над пустым canvas (коробка или
    /// контрол внутри неё обработали бы его раньше). И левая, и средняя двигают панораму; левая
    /// вдобавок снимает выделение — именно так возвращаются к инспектору макроса.
    /// </summary>
    private void OnViewportPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        Viewport.Focus();

        var point = e.GetCurrentPoint(Viewport);
        if (point.Properties.IsLeftButtonPressed)
        {
            vm.SelectedNode = null;
            vm.CollapseNodes();
            // Набор для извлечения сбрасывается ЗДЕСЬ и только здесь: клик по пустому месту —
            // единственный жест, который у канвы означает «ничего не выбрано».
            vm.ClearMarks();
        }
        else if (!point.Properties.IsMiddleButtonPressed)
        {
            return;
        }

        BeginPan(e.GetPosition(Viewport), vm);
        e.Pointer.Capture(Viewport);
        e.Handled = true;
    }

    /// <summary>
    /// Нажатие по коробке. Выделяет её и начинает перемещение; нажатия по полям РАЗВЁРНУТОЙ
    /// коробки сюда не доходят никогда, потому что каждый контрол ввода помечает своё нажатие
    /// обработанным.
    ///
    /// <b>Ctrl+клик набирает НАБОР</b> для выделения в под-макрос (F4) и перемещения не начинает:
    /// набирают его по несколько коробок подряд, и сдвинуть одну из них случайным дрожанием руки
    /// посреди набора — не то, чего ждёшь. Обычный клик набор не трогает: он сбрасывается кликом
    /// по пустому месту канвы, и это отдельный жест ровно затем, чтобы разглядывание графа его не
    /// разрушало.
    /// </summary>
    private void OnNodePressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Control { DataContext: NodeRowViewModel row })
        {
            return;
        }

        Viewport.Focus();

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.ToggleMark(row);
            e.Handled = true;
            return;
        }

        vm.SelectedNode = row;

        var point = e.GetCurrentPoint(Viewport);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var canvasPoint = ToCanvas(e.GetPosition(Viewport));
        _gesture = Gesture.DragNode;
        _draggedNode = row;
        _dragOffset = new Point(canvasPoint.X - row.X, canvasPoint.Y - row.Y);
        e.Pointer.Capture(Viewport);
        e.Handled = true;
    }

    private void OnNodeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Control { DataContext: NodeRowViewModel row })
        {
            return;
        }

        // Второй двойной клик складывает её обратно, так что переключателем служит тот же жест.
        vm.ExpandNode(row.IsExpanded ? null : row);
        e.Handled = true;
    }

    /// <summary>Начинает вытягивать связь из порта исхода.</summary>
    private void OnPortPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Control { DataContext: NodeEdgeViewModel edge })
        {
            return;
        }

        if (!e.GetCurrentPoint(Viewport).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var owner = vm.Nodes.FirstOrDefault(node => node.Edges.Contains(edge));
        if (owner is null)
        {
            return;
        }

        var index = 0;
        for (var i = 0; i < owner.Edges.Count; i++)
        {
            if (ReferenceEquals(owner.Edges[i], edge))
            {
                index = i;
                break;
            }
        }

        var port = CanvasEdgeRouter.OutcomePort(owner, index);
        _gesture = Gesture.DragLink;
        _draggedEdge = edge;
        Edges.PendingStart = new Point(port.X, port.Y);
        Edges.PendingEnd = ToCanvas(e.GetPosition(Viewport));
        e.Pointer.Capture(Viewport);
        e.Handled = true;
    }

    private void OnViewportMoved(object? sender, PointerEventArgs e)
    {
        if (Vm is not { } vm || _gesture == Gesture.None)
        {
            return;
        }

        var screen = e.GetPosition(Viewport);
        switch (_gesture)
        {
            case Gesture.Pan:
                vm.PanX = _panOriginX + (screen.X - _gestureOrigin.X);
                vm.PanY = _panOriginY + (screen.Y - _gestureOrigin.Y);
                ApplyTransform();
                break;

            case Gesture.DragNode when _draggedNode is not null:
                var canvasPoint = ToCanvas(screen);
                vm.MoveNode(
                    _draggedNode,
                    Clamp(canvasPoint.X - _dragOffset.X, CanvasMetrics.NodeWidth),
                    Clamp(canvasPoint.Y - _dragOffset.Y, _draggedNode.LayoutHeight));
                break;

            case Gesture.DragLink:
                Edges.PendingEnd = ToCanvas(screen);
                break;

            default:
                break;
        }
    }

    private void OnViewportReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (Vm is { } vm && _gesture == Gesture.DragLink && _draggedEdge is not null)
        {
            // Бросили на коробку → подключаем к ней. Бросили в любое другое место → «конец
            // прогона», и это полноправный ответ, а не отменённый жест: именно так исход
            // ОТключают, и макет настаивает, что терминальной ноды при этом появляться не должно.
            var target = NodeAt(ToCanvas(e.GetPosition(Viewport)));
            vm.RewireEdge(_draggedEdge, target?.Id);
        }

        _gesture = Gesture.None;
        _draggedNode = null;
        _draggedEdge = null;
        Edges.PendingStart = null;
        Edges.PendingEnd = null;
        e.Pointer.Capture(null);
    }

    private void OnViewportKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                vm.CollapseNodes();
                e.Handled = true;
                break;

            case Key.Delete when vm.SelectedNode is { } selected:
                vm.DeleteNode(selected);
                e.Handled = true;
                break;

            default:
                break;
        }
    }

    private void BeginPan(Point screen, MacroEditorViewModel vm)
    {
        _gesture = Gesture.Pan;
        _gestureOrigin = screen;
        _panOriginX = vm.PanX;
        _panOriginY = vm.PanY;
    }

    /// <summary>Самая верхняя коробка, накрывающая точку canvas, либо <c>null</c> для пустого места.</summary>
    private NodeRowViewModel? NodeAt(Point canvasPoint)
    {
        if (Vm is not { } vm)
        {
            return null;
        }

        // В обратном порядке: более поздние ноды рисуются поверх, поэтому попадание за ними.
        for (var i = vm.Nodes.Count - 1; i >= 0; i--)
        {
            var node = vm.Nodes[i];
            if (canvasPoint.X >= node.X
                && canvasPoint.X <= node.X + CanvasMetrics.NodeWidth
                && canvasPoint.Y >= node.Y
                && canvasPoint.Y <= node.Y + node.LayoutHeight)
            {
                return node;
            }
        }

        return null;
    }

    private Point ToCanvas(Point screen)
    {
        var vm = Vm;
        var zoom = vm?.Zoom ?? 1;
        var panX = vm?.PanX ?? 0;
        var panY = vm?.PanY ?? 0;
        return new Point((screen.X - panX) / zoom, (screen.Y - panY) / zoom);
    }

    /// <summary>
    /// Проталкивает масштаб и панораму на поверхность.
    ///
    /// Делается кодом, а не привязкой <c>TransformGroup</c> в XAML, потому что порядок здесь
    /// важен (сначала масштаб, ПОТОМ сдвиг, чтобы панорама оставалась в экранных пикселях), а
    /// привязанную группу от тихой перестановки местами отделяет один рефакторинг.
    /// </summary>
    private void ApplyTransform()
    {
        // Проверка Surface на null — не паранойя: этот метод вызывается из
        // OnDataContextChanged, а исключение оттуда не даёт DataContext дойти до детей, и отказ
        // выглядит как «не работает ни одна привязка», а вовсе не как падение.
        if (Vm is not { } vm || Surface is null)
        {
            return;
        }

        Surface.RenderTransform = new TransformGroup
        {
            Children =
            {
                new ScaleTransform(vm.Zoom, vm.Zoom),
                new TranslateTransform(vm.PanX, vm.PanY),
            },
        };
    }

    /// <summary>
    /// Transform следует за view-model, а не только за жестами, потому что двигает его и сама
    /// view-model: открытие макроса сбрасывает вид, и без этого canvas сохранил бы панораму
    /// предыдущего графа.
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_watched is not null)
        {
            _watched.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _watched = Vm;
        if (_watched is not null)
        {
            _watched.PropertyChanged += OnViewModelPropertyChanged;
        }

        if (_watchedTemplates is not null)
        {
            _watchedTemplates.PropertyChanged -= OnTemplatesPropertyChanged;
        }

        _watchedTemplates = TemplatesVm;
        if (_watchedTemplates is not null)
        {
            _watchedTemplates.PropertyChanged += OnTemplatesPropertyChanged;
        }

        UpdateTemplatePreview();
        ApplyTransform();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyTransform();
        _elapsedTimer.Start();
        if (RunsInALivePanel)
        {
            _autoSaveTimer.Start();
        }
    }

    /// <summary>
    /// Этот вид поднят настоящей панелью, а не дизайнером и не headless-обходом раскладки.
    ///
    /// Признак тот же, по которому <c>App</c> решает, поднимать ли главное окно, — классический
    /// оконный цикл. Часы времени прогона такой оговорки не требуют (они лишь поднимают
    /// <c>PropertyChanged</c>), а часы автосохранения ПИШУТ ФАЙЛЫ, и делать это от одного лишь
    /// появления контрола в дереве нельзя: у обхода раскладки тик пришёлся бы посреди замера и
    /// сделал бы его плавающим — ровно та беда, из-за которой часы вообще вынесены в вид и
    /// дёргаются тестом вручную.
    /// </summary>
    private static bool RunsInALivePanel =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime;

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _elapsedTimer.Stop();
        _autoSaveTimer.Stop();
        // Неуправляемая память Skia у превью шаблона: панель закрыли — отпускаем сразу, а не
        // финализатором.
        _templatePreview?.Dispose();
        _templatePreview = null;
    }

    private void OnTemplatesPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(TemplatesViewModel.PreviewPng), StringComparison.Ordinal))
        {
            UpdateTemplatePreview();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MacroEditorViewModel.Zoom)
            or nameof(MacroEditorViewModel.PanX)
            or nameof(MacroEditorViewModel.PanY))
        {
            ApplyTransform();
        }
    }

    private static double Clamp(double value, double extent) =>
        Math.Clamp(value, 0, SurfaceExtent - extent);
}
