using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using SmartMacro.App.Mvvm;
using SmartMacro.Contracts.Settings;
using SmartMacro.Resources;

namespace SmartMacro.App.ViewModels;

/// <summary>
/// Одна строка таблицы профилей процессов: за кем следить и как будить его окна перед вводом.
///
/// <b>Сигнала побудки (<c>ActivationLParam</c>) на экране нет вовсе — есть только факт его
/// наличия.</b> Само число добыто реверсом клиента, это костыль под одну игру, а не настройка:
/// стереть его значит получить фоновые окна, которые молча перестают принимать ввод, без единой
/// строки в журнале. Поэтому строка проносит значение через себя нетронутым
/// (<see cref="_activationLParam"/>), проставляется оно один раз при СОЗДАНИИ профиля по таблице
/// известных имён, а тому, кому попадётся сборка с другим значением, остаётся правка файла.
///
/// Отсутствие сигнала — тоже рабочая семантика, а не «поле забыли»: это «обычный процесс,
/// пробуждение через <c>WM_ACTIVATEAPP</c> пропускается», и на ней держатся профили не-игровых
/// процессов. Отсюда две разные подписи в колонке, а не пустая ячейка.
/// </summary>
public sealed class ProcessProfileRowViewModel : ObservableObject
{
    private readonly uint? _activationLParam;

    private string _processName;
    private string _settleDelay;
    private string _deactivationDelay;
    private InputMethodChoice _input;
    private bool _isSelected;

    internal ProcessProfileRowViewModel(ProcessProfileSettings profile, IReadOnlyList<InputMethodChoice> choices)
    {
        _activationLParam = profile.ActivationLParam;
        _processName = profile.ProcessName;
        _settleDelay = profile.SettleDelayMs.ToString(CultureInfo.InvariantCulture);
        _deactivationDelay = profile.DeactivationDelayMs.ToString(CultureInfo.InvariantCulture);
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

    /// <summary>Пауза после сигнала побудки, мс. Ниже 150 PW начинает терять ввод — подсказка стоит у поля.</summary>
    [AllowNull]
    public string SettleDelayMs
    {
        get => _settleDelay;
        set => Set(ref _settleDelay, value ?? string.Empty);
    }

    /// <summary>Пауза перед сигналом деактивации, мс — время на разбор очереди ввода.</summary>
    [AllowNull]
    public string DeactivationDelayMs
    {
        get => _deactivationDelay;
        set => Set(ref _deactivationDelay, value ?? string.Empty);
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

    /// <summary><c>true</c>, когда у профиля есть сигнал побудки, то есть это замораживаемый клиент.</summary>
    public bool WakesWindows => _activationLParam is not null;

    /// <summary>Что стоит в колонке «сигнал побудки». Числа здесь нет намеренно — см. заметку у класса.</summary>
    public string WakeText => WakesWindows ? Strings_App.Settings_Profiles_WakeYes : Strings_App.Settings_Profiles_WakeNo;

    /// <summary>Подсказка к той же колонке: что этот сигнал делает и почему его не показывают числом.</summary>
    public string WakeTooltip => WakesWindows
        ? Strings_App.Settings_Profiles_WakeTooltipYes
        : Strings_App.Settings_Profiles_WakeTooltipNo;

    /// <summary>Собирает профиль обратно.</summary>
    /// <param name="profile">Собранный профиль.</param>
    /// <param name="error">Причина, по которой не вышло.</param>
    /// <returns><c>false</c> — числовое поле не разбирается.</returns>
    public bool TryBuild([NotNullWhen(true)] out ProcessProfileSettings? profile, [NotNullWhen(false)] out string? error)
    {
        profile = null;
        var name = _processName.Trim();

        if (!TryParse(_settleDelay, Strings_App.Settings_Profiles_SettleName, name, out var settle, out error)
            || !TryParse(_deactivationDelay, Strings_App.Settings_Profiles_DeactivateName, name, out var deactivation, out error))
        {
            return false;
        }

        profile = new ProcessProfileSettings
        {
            ProcessName = name,
            // Проносится нетронутым: экран его не показывает и потому не имеет права им
            // распоряжаться.
            ActivationLParam = _activationLParam,
            SettleDelayMs = settle,
            DeactivationDelayMs = deactivation,
            InputMethod = _input.Method,
        };
        error = null;
        return true;
    }

    /// <summary>Отличается ли строка от того профиля, что сейчас у демона.</summary>
    internal bool DiffersFrom(ProcessProfileSettings baseline) =>
        !string.Equals(_processName.Trim(), baseline.ProcessName, StringComparison.Ordinal)
        || _settleDelay != baseline.SettleDelayMs.ToString(CultureInfo.InvariantCulture)
        || _deactivationDelay != baseline.DeactivationDelayMs.ToString(CultureInfo.InvariantCulture)
        || _input.Method != baseline.InputMethod;

    private static bool TryParse(string text, string title, string process, out int value,
        [NotNullWhen(false)] out string? error)
    {
        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error = null;
            return true;
        }

        error = string.Format(CultureInfo.CurrentCulture,
            Strings_App.Settings_Profiles_BadNumber, title, process, text);
        return false;
    }

    private void Set(ref string field, string value)
    {
        if (SetField(ref field, value))
        {
            Changed?.Invoke();
        }
    }
}
