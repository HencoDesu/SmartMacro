using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SmartMacro.App.Views;

/// <summary>«Лог» — chrome and an honest empty state; the daemon's log never crosses the pipe.</summary>
public partial class LogView : UserControl
{
    public LogView() => AvaloniaXamlLoader.Load(this);
}
