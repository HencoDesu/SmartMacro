using SmartMacro.Native;

namespace SmartMacro.Macros.Model;

/// <summary>Press <see cref="Key"/> on the target window(s).</summary>
public sealed record KeyPressNode : MacroNode
{
    /// <summary>Virtual key to press.</summary>
    public required VirtualKey Key { get; init; }

    /// <summary>Fan-out selector; <c>null</c> = the run's context window.</summary>
    public TargetSelector? Target { get; init; }

    /// <summary>Next node id; <c>null</c> = end of run.</summary>
    public string? Next { get; init; }
}

/// <summary>
/// Left-click at a client-space point on the target window(s). EXACTLY one of
/// <see cref="Point"/> (literal) / <see cref="PointVar"/> (run variable holding a point,
/// e.g. <c>"cursor"</c>) must be set — the validator flags graphs that violate this and
/// the executor aborts the run if one slips through.
/// </summary>
public sealed record ClickNode : MacroNode
{
    /// <summary>Literal click point. Mutually exclusive with <see cref="PointVar"/>.</summary>
    public ScreenPoint? Point { get; init; }

    /// <summary>Name of a run variable holding the click point. Mutually exclusive with <see cref="Point"/>.</summary>
    public string? PointVar { get; init; }

    /// <summary>Double-click instead of a single click.</summary>
    public bool DoubleClick { get; init; }

    /// <summary>Fan-out selector; <c>null</c> = the run's context window.</summary>
    public TargetSelector? Target { get; init; }

    /// <summary>Next node id; <c>null</c> = end of run.</summary>
    public string? Next { get; init; }
}

/// <summary>Pause the scenario for <see cref="Ms"/> milliseconds. No target — the pause is global to the run.</summary>
public sealed record DelayNode : MacroNode
{
    /// <summary>Delay in milliseconds. Zero or negative = no-op.</summary>
    public required int Ms { get; init; }

    /// <summary>Next node id; <c>null</c> = end of run.</summary>
    public string? Next { get; init; }
}

/// <summary>Add <see cref="Tag"/> to the target window(s) via the window registry.</summary>
public sealed record AddTagNode : MacroNode
{
    /// <summary>Tag to add. Supports <c>{var}</c> interpolation from run variables.</summary>
    public required string Tag { get; init; }

    /// <summary>Fan-out selector; <c>null</c> = the run's context window.</summary>
    public TargetSelector? Target { get; init; }

    /// <summary>Next node id; <c>null</c> = end of run.</summary>
    public string? Next { get; init; }
}

/// <summary>Remove <see cref="Tag"/> from the target window(s) via the window registry.</summary>
public sealed record RemoveTagNode : MacroNode
{
    /// <summary>Tag to remove. Supports <c>{var}</c> interpolation from run variables.</summary>
    public required string Tag { get; init; }

    /// <summary>Fan-out selector; <c>null</c> = the run's context window.</summary>
    public TargetSelector? Target { get; init; }

    /// <summary>Next node id; <c>null</c> = end of run.</summary>
    public string? Next { get; init; }
}

/// <summary>Set the taskbar/window icon of the target window(s) to <see cref="IconPath"/>.</summary>
public sealed record SetIconNode : MacroNode
{
    /// <summary>Icon file path, e.g. <c>"icons/{tag}.png"</c>. Supports <c>{var}</c> interpolation.</summary>
    public required string IconPath { get; init; }

    /// <summary>Fan-out selector; <c>null</c> = the run's context window.</summary>
    public TargetSelector? Target { get; init; }

    /// <summary>Next node id; <c>null</c> = end of run.</summary>
    public string? Next { get; init; }
}

/// <summary>
/// Run another macro as a sub-run. With <see cref="Target"/>: one parallel sub-run per
/// matched window (that window becomes the sub-run's context). Without: a single sub-run
/// on this run's context window. Sub-runs get a COPY of the parent's variables — reads
/// inherit, writes never leak back. Depth is limited and name cycles abort the run.
/// </summary>
public sealed record RunMacroNode : MacroNode
{
    /// <summary>Name of the macro to run. Supports <c>{var}</c> interpolation.</summary>
    public required string MacroName { get; init; }

    /// <summary>Fan-out selector; <c>null</c> = the run's context window.</summary>
    public TargetSelector? Target { get; init; }

    /// <summary><c>true</c> (default) = wait for the sub-run(s) before following <see cref="Next"/>; <c>false</c> = fire-and-forget.</summary>
    public bool Await { get; init; } = true;

    /// <summary>Next node id; <c>null</c> = end of run.</summary>
    public string? Next { get; init; }
}
