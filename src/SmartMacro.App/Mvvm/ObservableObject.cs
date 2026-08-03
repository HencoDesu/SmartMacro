using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SmartMacro.App.Mvvm;

// Крошечная база под INotifyPropertyChanged. CommunityToolkit.Mvvm дал бы observable-свойства
// с генерацией исходников, но тащить его ради трёх с небольшим view-models — перебор; ручной
// шаблон вполне устраивает и держит список зависимостей коротким.
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // Виртуальный, чтобы наследник мог разослать одно изменение веером по своим вычисляемым
    // свойствам. Canvas без этого не обходится: коробка ноды показывает однострочную сводку,
    // собранную из тех полей, какие у этого типа ноды нашлись, а поднимать событие вручную из
    // каждого сеттера — верный способ однажды забыть.
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
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
