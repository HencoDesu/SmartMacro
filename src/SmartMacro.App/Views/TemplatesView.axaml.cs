using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Serilog;
using SmartMacro.App.ViewModels;

namespace SmartMacro.App.Views;

/// <summary>
/// «Шаблоны» — браузер дерева <c>Assets/templates</c> у демона.
///
/// Code-behind здесь делает ровно две вещи: переносит клик по строке в выделение view-model и
/// ДЕКОДИРУЕТ превью.
///
/// <b>Почему декодирование живёт тут, а не в конвертере привязки.</b> View-model отдаёт байты и
/// про Avalonia не знает — это условие того, что её гоняют headless. Значит, кто-то должен
/// превратить их в <see cref="Bitmap"/>, а <see cref="Bitmap"/> держит неуправляемую память
/// Skia и требует освобождения. Конвертер, вызываемый на каждое обновление привязки, оставлял бы
/// каждую предыдущую картинку финализатору. Здесь же ровно одна живая картинка: следующая
/// вытесняет и освобождает предыдущую.
/// </summary>
public partial class TemplatesView : UserControl
{
    private TemplatesViewModel? _bound;
    private Bitmap? _preview;

    public TemplatesView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Release();
    }

    private TemplatesViewModel? Vm => DataContext as TemplatesViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_bound is not null)
        {
            _bound.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _bound = Vm;
        if (_bound is not null)
        {
            _bound.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdatePreview();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(TemplatesViewModel.PreviewPng), StringComparison.Ordinal))
        {
            UpdatePreview();
        }
    }

    private void UpdatePreview()
    {
        Release();

        if (Vm?.PreviewPng is not { Length: > 0 } bytes)
        {
            return;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            _preview = new Bitmap(stream);
        }
        catch (Exception ex)
        {
            // Файл лежит в папке шаблонов, но картинкой не является. Демон отдал его честно —
            // ругаться должен вид, а не превращать это в отказ запроса.
            Log.Warning(ex, "Не удалось декодировать превью шаблона");
            return;
        }

        PreviewImage.Source = _preview;
    }

    private void Release()
    {
        PreviewImage.Source = null;
        _preview?.Dispose();
        _preview = null;
    }

    private void OnTemplateRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is { } vm && sender is Control { DataContext: TemplateRowViewModel row })
        {
            vm.Selected = row;
        }
    }

    private void OnRefreshClicked(object? sender, RoutedEventArgs e) => _ = Vm?.RefreshAsync();
}
