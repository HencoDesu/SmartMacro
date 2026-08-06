using System.Diagnostics.CodeAnalysis;
using SmartMacro.App.Mvvm;
using SmartMacro.Contracts.Settings;

namespace SmartMacro.App.ViewModels;

/// <summary>
/// Одна строка таблицы профилей процессов: за кем следить и чем вводить.
///
/// <b>Скобки пробуждения на экране нет вовсе — целиком.</b> Раньше строка показывала три колонки
/// (ПОБУДКА / ОСЕДАНИЕ / ДЕАКТИВ.), и это было половинчато: само число сигнала уже тогда не
/// выводилось, потому что оно добыто реверсом клиента и является костылём под одну игру, а не
/// настройкой. Теперь всё три поля собраны в хук процесса (<see cref="ProcessHookSettings"/>) и
/// живут в <c>settings.json</c>, вне интерфейса: набор их значений проверяется только на живой
/// игре, а выставленные наугад они дают фоновые окна, которые молча перестают принимать ввод, без
/// единой строки в журнале.
///
/// Строка ничего из хука не проносит и не может: хуки едут через <c>SettingsViewModel</c> целым
/// словарём, нетронутыми, ровно как непоказываемые поля <c>Vision</c>.
/// </summary>
public sealed class ProcessProfileRowViewModel : ObservableObject
{
    private string _processName;
    private InputMethodChoice _input;
    private bool _isSelected;

    internal ProcessProfileRowViewModel(ProcessProfileSettings profile, IReadOnlyList<InputMethodChoice> choices)
    {
        _processName = profile.ProcessName;
        InputChoices = choices;
        _input = choices.FirstOrDefault(c => c.Method == profile.InputMethod) ?? choices[0];
    }

    /// <summary>Поднимается на любую правку строки — оболочка по нему пересчитывает счётчик правок.</summary>
    public event Action? Changed;

    /// <summary>Способы ввода для выпадающего списка, включая пункт «По умолчанию».</summary>
    public IReadOnlyList<InputMethodChoice> InputChoices { get; }

    /// <summary>Имя процесса без расширения.</summary>
    [AllowNull]
    public string ProcessName
    {
        get => _processName;
        set => Set(ref _processName, value ?? string.Empty);
    }

    /// <summary>Способ ввода для этого процесса либо «По умолчанию».</summary>
    [AllowNull]
    public InputMethodChoice Input
    {
        get => _input;
        set
        {
            // ComboBox проталкивает null, пока перетряхивается его ItemsSource.
            if (value is null || ReferenceEquals(value, _input))
            {
                return;
            }

            _input = value;
            OnPropertyChanged(nameof(Input));
            Changed?.Invoke();
        }
    }

    /// <summary>Выделена ли строка — акцентная линейка слева, как в остальных списках панели.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    /// <summary>Собирает профиль обратно.</summary>
    public ProcessProfileSettings Build() => new()
    {
        ProcessName = _processName.Trim(),
        InputMethod = _input.Method,
    };

    /// <summary>Отличается ли строка от того профиля, что сейчас у демона.</summary>
    internal bool DiffersFrom(ProcessProfileSettings baseline) =>
        !string.Equals(_processName.Trim(), baseline.ProcessName, StringComparison.Ordinal)
        || _input.Method != baseline.InputMethod;

    private void Set(ref string field, string value)
    {
        if (SetField(ref field, value))
        {
            Changed?.Invoke();
        }
    }
}
