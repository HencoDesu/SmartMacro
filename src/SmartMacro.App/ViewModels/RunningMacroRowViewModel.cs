using System.Globalization;
using SmartMacro.App.Mvvm;
using SmartMacro.Contracts.Dto;

namespace SmartMacro.App.ViewModels;

/// <summary>
/// Одна запись панели «работающие макросы»: что работает, сколько уже, и где сейчас обход.
///
/// <see cref="Elapsed"/> обновляется внешним тиком (секундный таймер крутит главное окно), а не
/// собственным <c>DispatcherTimer</c> внутри VM: так все VM этой сборки остаются без типов
/// Avalonia, а значит, пригодными для юнит-тестов.
/// </summary>
public sealed class RunningMacroRowViewModel : ObservableObject
{
    private string? _currentNodeName;
    private string _elapsed = "0:00";

    public RunningMacroRowViewModel(RunningMacroDto run)
    {
        ArgumentNullException.ThrowIfNull(run);
        RunId = run.RunId;
        MacroName = run.MacroName;
        StartedUtc = run.StartedUtc;
        _currentNodeName = run.CurrentNodeName;
        Refresh(DateTimeOffset.UtcNow);
    }

    /// <summary>Идентификатор прогона на стороне демона — то, что принимает запрос <c>StopMacro</c>.</summary>
    public Guid RunId { get; }

    /// <summary>Имя работающего макроса.</summary>
    public string MacroName { get; }

    /// <summary>
    /// Время начала прогона. <see cref="DateTimeOffset"/>, а не <see cref="DateTime"/>, потому
    /// что значение пересекло границу процессов: проводной DTO несёт смещение явно, чтобы ни
    /// одной из сторон не пришлось гадать про <c>DateTimeKind</c>.
    /// </summary>
    public DateTimeOffset StartedUtc { get; }

    /// <summary>Подпись ноды, в которую обход вошёл последней; <c>null</c> до первой ноды.</summary>
    public string? CurrentNodeName
    {
        get => _currentNodeName;
        private set
        {
            if (SetField(ref _currentNodeName, value))
            {
                OnPropertyChanged(nameof(CurrentNodeText));
            }
        }
    }

    /// <summary>Отображаемая форма <see cref="CurrentNodeName"/>.</summary>
    public string CurrentNodeText => string.IsNullOrEmpty(_currentNodeName) ? "—" : _currentNodeName;

    /// <summary>Время по часам с начала прогона, как <c>m:ss</c> (или <c>h:mm:ss</c>).</summary>
    public string Elapsed
    {
        get => _elapsed;
        private set => SetField(ref _elapsed, value);
    }

    /// <summary>
    /// Перерисовывает <see cref="Elapsed"/> и подхватывает последнюю ноду обхода. Вызывается по
    /// тику таймера и всякий раз, когда демон присылает новый снимок прогонов.
    /// </summary>
    public void Refresh(DateTimeOffset nowUtc, string? currentNodeName = null)
    {
        if (currentNodeName is not null)
        {
            CurrentNodeName = currentNodeName;
        }

        var elapsed = nowUtc - StartedUtc;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        Elapsed = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
