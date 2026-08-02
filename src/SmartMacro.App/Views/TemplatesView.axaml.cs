using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SmartMacro.App.Views;

/// <summary>«Шаблоны» — chrome and an honest empty state; there is no IPC behind it yet.</summary>
public partial class TemplatesView : UserControl
{
    public TemplatesView() => AvaloniaXamlLoader.Load(this);
}
