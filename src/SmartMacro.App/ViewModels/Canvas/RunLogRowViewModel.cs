using System.Globalization;
using SmartMacro.App.Mvvm;
using SmartMacro.Contracts.Dto;

namespace SmartMacro.App.ViewModels.Canvas;

/// <summary>
/// One line of the run-log strip under the canvas:
/// <c>0:01.2 · click-server-select · ок · PostMessage 1192,1805 · 40 мс</c>.
///
/// <b>A row is written twice.</b> It appears the moment the walker ENTERS a node, with no
/// outcome and no duration, and is completed in place when the node exits. That is what the
/// mockup's live row is — the bottom line reading <c>▸ ждёт … 2.4 с / 60 с</c> while
/// everything above it is settled — and it is why this is a mutable observable rather than
/// the record D3a reserved: replacing the item would rebuild its container and make the
/// strip flicker on every node of every run.
///
/// The Russian lives here, not on the wire. The daemon sends
/// <see cref="RunOutcomes"/> symbols and a free-form detail; the wording and the colour of
/// an outcome are the panel's to choose.
/// </summary>
public sealed class RunLogRowViewModel : ObservableObject
{
    private string _outcome;
    private string _detail;
    private bool _isCurrent;
    private bool _outcomeIsAccent;

    internal RunLogRowViewModel(int elapsedMs, string nodeId)
    {
        Elapsed = FormatElapsed(elapsedMs);
        NodeId = nodeId;
        _outcome = PendingOutcome;
        _detail = string.Empty;
        _isCurrent = true;
        _outcomeIsAccent = true;
    }

    /// <summary>The marker an unfinished row carries in the outcome column.</summary>
    public const string PendingOutcome = "▸ идёт";

    /// <summary>Time since the walk started, <c>m:ss.f</c>.</summary>
    public string Elapsed { get; }

    /// <summary>Node the row belongs to.</summary>
    public string NodeId { get; }

    /// <summary>Which way the node went, in Russian: <c>ок</c>, <c>нашёл</c>, <c>таймаут</c>, …</summary>
    public string Outcome
    {
        get => _outcome;
        private set => SetField(ref _outcome, value);
    }

    /// <summary>Specifics plus the duration — the daemon's detail line with <c>· 40 мс</c> appended.</summary>
    public string Detail
    {
        get => _detail;
        private set => SetField(ref _detail, value);
    }

    /// <summary>The node the walker is standing on right now. Exactly one row per walk has it.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        private set => SetField(ref _isCurrent, value);
    }

    /// <summary>Outcome worth the accent colour: a conditional that matched, or a row still running.</summary>
    public bool OutcomeIsAccent
    {
        get => _outcomeIsAccent;
        private set => SetField(ref _outcomeIsAccent, value);
    }

    /// <summary>Fills in what the node did. Called once, when its <c>NodeExited</c> arrives.</summary>
    internal void Complete(string? outcome, string? detail, int durationMs)
    {
        Outcome = DescribeOutcome(outcome);
        OutcomeIsAccent = IsAccentOutcome(outcome);
        Detail = Join(detail, FormatDuration(durationMs));
        IsCurrent = false;
    }

    /// <summary>
    /// Turns the row off as "current" without completing it. Used when a walk ends while a
    /// node is still open — a cancelled run leaves its last row unfinished, and pretending
    /// otherwise would invent an outcome the daemon never reported.
    /// </summary>
    internal void Settle() => IsCurrent = false;

    /// <summary>Russian wording for a <see cref="RunOutcomes"/> symbol.</summary>
    public static string DescribeOutcome(string? outcome) => outcome switch
    {
        RunOutcomes.Ok => "ок",
        RunOutcomes.Found => "нашёл",
        RunOutcomes.NotFound => "не нашёл",
        RunOutcomes.Timeout => "таймаут",
        RunOutcomes.Matched => "распознал",
        RunOutcomes.NotMatched => "не распознал",
        RunOutcomes.Error => "ошибка",
        RunOutcomes.Completed => "готово",
        RunOutcomes.Aborted => "прервано",
        RunOutcomes.Cancelled => "отменено",
        // A daemon newer than this panel (wave D5 adds kinds and outcomes). Showing the raw
        // symbol beats showing nothing — and beats crashing on an unexpected value.
        null or "" => PendingOutcome,
        var other => other,
    };

    /// <summary>Which outcomes are worth the accent colour — the branches that went somewhere.</summary>
    public static bool IsAccentOutcome(string? outcome) =>
        outcome is RunOutcomes.Found or RunOutcomes.Matched or RunOutcomes.Completed;

    /// <summary><c>m:ss.f</c>, the log's left column.</summary>
    public static string FormatElapsed(int elapsedMs)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(elapsedMs, 0));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds / 100}");
    }

    /// <summary>Milliseconds under a second, seconds with one decimal above it.</summary>
    public static string FormatDuration(int durationMs) => durationMs < 1000
        ? string.Create(CultureInfo.InvariantCulture, $"{durationMs} мс")
        : string.Create(CultureInfo.InvariantCulture, $"{durationMs / 1000.0:0.0} с");

    private static string Join(string? detail, string duration) =>
        string.IsNullOrEmpty(detail) ? duration : $"{detail} · {duration}";
}
