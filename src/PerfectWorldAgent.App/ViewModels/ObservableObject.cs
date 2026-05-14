using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PerfectWorldAgent.App.ViewModels;

// Tiny INotifyPropertyChanged base. CommunityToolkit.Mvvm would give us source-generated
// observable properties, but pulling that in for ~3 view-models is overkill; the manual
// pattern is fine and keeps dependencies minimal.
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
