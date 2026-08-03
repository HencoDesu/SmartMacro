namespace SmartMacro.Macros.Execution;

/// <summary>Чем закончился прогон макроса.</summary>
public enum MacroRunStatus
{
    /// <summary>Обход дошёл до ребра со значением <c>null</c> — чистое завершение.</summary>
    Completed,

    /// <summary>Прогон напоролся на ошибку (нет переменной, нет контекст-окна, цикл, предел глубины, …). См. <see cref="MacroRunResult.Error"/>.</summary>
    Aborted,

    /// <summary>Прогон отменили (кнопка «Стоп» в UI, выключение). Молча — это не ошибка.</summary>
    Cancelled,
}

/// <summary>Исход одного вызова <see cref="MacroExecutor.RunAsync"/>.</summary>
/// <param name="Status">Чем закончился прогон.</param>
/// <param name="Error">Человекочитаемое описание сбоя; не null только при <paramref name="Status"/>, равном <see cref="MacroRunStatus.Aborted"/>.</param>
public sealed record MacroRunResult(MacroRunStatus Status, string? Error = null)
{
    /// <summary>Общий результат «чисто завершились».</summary>
    public static MacroRunResult Completed { get; } = new(MacroRunStatus.Completed);

    /// <summary>Общий результат «отменено».</summary>
    public static MacroRunResult Cancelled { get; } = new(MacroRunStatus.Cancelled);

    /// <summary>Собирает результат обрыва вместе с описанием сбоя.</summary>
    public static MacroRunResult Aborted(string error) => new(MacroRunStatus.Aborted, error);
}
