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

    // ---------------------------------------------------------------------------------------
    // Про [AllowNull] на строковых свойствах наследников.
    //
    // Сеттеры, привязанные к TextBox и ComboBox, устроены как
    //     [AllowNull] public string Foo { get => _foo; set => SetField(ref _foo, value ?? ""); }
    // и защита `?? ""` там не лишняя: Avalonia проталкивает в сеттер null — TextBox при очистке,
    // ComboBox пока перестраивается его ItemsSource, — вопреки тому, что свойство размечено
    // ненулевым. Пустая строка для этих полей и означает «не задано», а null в них означал бы
    // NullReferenceException при первом же обращении.
    //
    // Раньше здесь стояла ненулевая аннотация и голая защита, и анализатор справедливо ругался,
    // что левый операнд `??` никогда не null. Ответ — не глушить его, а сказать правду:
    // [AllowNull] разрешает null НА ВХОДЕ, оставляя выход ненулевым. Тогда и защита обоснована
    // типом, и подсказки анализатора снова чего-то стоят.
    //
    // Не путать с nullable-полями DTO протокола (см. SetBreakpointsRequest.NodeIds): там null
    // приезжает из чужого JSON и разрешён по-настоящему, с обеих сторон.
    // ---------------------------------------------------------------------------------------

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
