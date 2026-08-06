using System.Globalization;
using SmartMacro.App.Mvvm;
using SmartMacro.Contracts.Dto;
using SmartMacro.Resources;

namespace SmartMacro.App.ViewModels.Canvas;

/// <summary>
/// Одна строка ленты лога прогона под canvas:
/// <c>0:01.2 · click-server-select · ок · PostMessage 1192,1805 · 40 мс</c>.
///
/// <b>Строка пишется дважды.</b> Она появляется в тот момент, когда обход ВХОДИТ в ноду, — без
/// исхода и без длительности, — и дописывается на месте, когда нода завершается. Именно это и
/// есть живая строка с макета: нижняя строчка вида <c>▸ ждёт … 2.4 с / 60 с</c>, пока всё, что
/// над ней, уже устоялось. И именно поэтому здесь изменяемый observable, а не запись, которую
/// приберегала волна D3a: подмена элемента пересобрала бы его контейнер, и лента моргала бы на
/// каждой ноде каждого прогона.
///
/// Русский язык живёт здесь, а не на проводе. Демон присылает символы
/// <see cref="RunOutcomes"/> и деталь в свободной форме; формулировка и цвет исхода — это выбор
/// панели.
/// </summary>
public sealed class RunLogRowViewModel : ObservableObject
{
    private string _outcome;
    private string _detail;
    private bool _isCurrent;
    private bool _outcomeIsAccent;

    internal RunLogRowViewModel(int elapsedMs, Guid? nodeId, string nodeName)
    {
        Elapsed = FormatElapsed(elapsedMs);
        NodeId = nodeId;
        NodeName = nodeName;
        _outcome = PendingOutcome;
        _detail = string.Empty;
        _isCurrent = true;
        _outcomeIsAccent = true;
    }

    /// <summary>Метка, которую незавершённая строка несёт в колонке исхода.</summary>
    public static string PendingOutcome => Strings.Editor_RunLog_Pending;

    /// <summary>Время с начала обхода, <c>m:ss.f</c>.</summary>
    public string Elapsed { get; }

    /// <summary>Нода, которой принадлежит строка, — по ней <c>NodeExited</c> находит свою строку входа.</summary>
    public Guid? NodeId { get; }

    /// <summary>Её подпись — то, что печатается во второй колонке. Приезжает с событием, а не резолвится по графу.</summary>
    public string NodeName { get; }

    /// <summary>Куда нода в итоге пошла, по-русски: <c>ок</c>, <c>нашёл</c>, <c>таймаут</c>, …</summary>
    public string Outcome
    {
        get => _outcome;
        private set => SetField(ref _outcome, value);
    }

    /// <summary>Подробности плюс длительность — строка детали от демона с приписанным <c>· 40 мс</c>.</summary>
    public string Detail
    {
        get => _detail;
        private set => SetField(ref _detail, value);
    }

    /// <summary>Нода, на которой обход стоит прямо сейчас. Такая строка на обход ровно одна.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        private set => SetField(ref _isCurrent, value);
    }

    /// <summary>Исход, заслуживающий акцентного цвета: сработавшее условие либо ещё идущая строка.</summary>
    public bool OutcomeIsAccent
    {
        get => _outcomeIsAccent;
        private set => SetField(ref _outcomeIsAccent, value);
    }

    /// <summary>Дописывает, что нода сделала. Вызывается один раз, когда приходит её <c>NodeExited</c>.</summary>
    internal void Complete(string? outcome, string? detail, int durationMs)
    {
        Outcome = DescribeOutcome(outcome);
        OutcomeIsAccent = IsAccentOutcome(outcome);
        Detail = Join(detail, FormatDuration(durationMs));
        IsCurrent = false;
    }

    /// <summary>
    /// Снимает со строки признак «текущая», не дописывая её. Нужно, когда обход закончился, а
    /// нода осталась открытой: отменённый прогон оставляет последнюю строку незавершённой, и
    /// притвориться, что это не так, значило бы выдумать исход, о котором демон не сообщал.
    /// </summary>
    internal void Settle() => IsCurrent = false;

    /// <summary>Русская формулировка для символа <see cref="RunOutcomes"/>.</summary>
    public static string DescribeOutcome(string? outcome) => outcome switch
    {
        RunOutcomes.Ok => Strings.Editor_RunLog_OutcomeOk,
        RunOutcomes.Found => Strings.Editor_RunLog_OutcomeFound,
        RunOutcomes.NotFound => Strings.Editor_RunLog_OutcomeNotFound,
        RunOutcomes.Timeout => Strings.Editor_RunLog_OutcomeTimeout,
        RunOutcomes.Matched => Strings.Editor_RunLog_OutcomeMatched,
        RunOutcomes.NotMatched => Strings.Editor_RunLog_OutcomeNotMatched,
        RunOutcomes.Error => Strings.Editor_RunLog_OutcomeError,
        RunOutcomes.Completed => Strings.Editor_RunLog_OutcomeCompleted,
        RunOutcomes.Aborted => Strings.Editor_RunLog_OutcomeAborted,
        RunOutcomes.Cancelled => Strings.Editor_RunLog_OutcomeCancelled,
        // Исхода нет вообще — значит, нода ещё не завершилась, и строка остаётся с меткой «идёт».
        null or "" => PendingOutcome,
        // Демон новее этой панели (волна D5 добавляет и виды, и исходы). Показать сырой символ
        // лучше, чем не показать ничего, — и лучше, чем упасть на неожиданном значении.
        var other => other,
    };

    /// <summary>Какие исходы заслуживают акцентного цвета — те ветки, что куда-то привели.</summary>
    public static bool IsAccentOutcome(string? outcome) =>
        outcome is RunOutcomes.Found or RunOutcomes.Matched or RunOutcomes.Completed;

    /// <summary><c>m:ss.f</c> — левая колонка лога.</summary>
    public static string FormatElapsed(int elapsedMs)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(elapsedMs, 0));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds / 100}");
    }

    /// <summary>До секунды — миллисекунды, свыше — секунды с одним знаком после запятой.</summary>
    public static string FormatDuration(int durationMs) => durationMs < 1000
        ? string.Format(CultureInfo.InvariantCulture, Strings.Editor_RunLog_DurationMs, durationMs)
        : string.Format(
            CultureInfo.InvariantCulture,
            Strings.Editor_RunLog_DurationSec,
            (durationMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture));

    private static string Join(string? detail, string duration) =>
        string.IsNullOrEmpty(detail) ? duration : $"{detail} · {duration}";
}
