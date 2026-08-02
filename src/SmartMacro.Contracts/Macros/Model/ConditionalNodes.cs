using SmartMacro.Native;

namespace SmartMacro.Macros.Model;

/// <summary>
/// Single-shot template search on the context window. Outcome picks the edge:
/// <see cref="Found"/> when the template matches, <see cref="NotFound"/> otherwise.
/// </summary>
public sealed record FindElementNode : MacroNode
{
    /// <summary>Template name (resolved to <c>templates/{name}.png</c> by the primitives layer).</summary>
    public required string Template { get; init; }

    /// <summary>Client-space crop to search in; <c>null</c> = the whole window.</summary>
    public ScreenRect? Region { get; init; }

    /// <summary>When set and the template is found, the match center is written into this run variable (as a point).</summary>
    public string? FoundPointVar { get; init; }

    /// <summary>Edge taken when the template is found; <c>null</c> = end of run.</summary>
    public string? Found { get; init; }

    /// <summary>Edge taken when the template is not found; <c>null</c> = end of run.</summary>
    public string? NotFound { get; init; }
}

/// <summary>
/// Poll for a template on the context window until it appears or <see cref="TimeoutMs"/>
/// elapses. Outcome picks the edge: <see cref="Found"/> / <see cref="Timeout"/>.
/// </summary>
public sealed record WaitForElementNode : MacroNode
{
    /// <summary>Template name (resolved to <c>templates/{name}.png</c> by the primitives layer).</summary>
    public required string Template { get; init; }

    /// <summary>Client-space crop to search in; <c>null</c> = the whole window.</summary>
    public ScreenRect? Region { get; init; }

    /// <summary>How long to keep polling before giving up, in milliseconds.</summary>
    public required int TimeoutMs { get; init; }

    /// <summary>When set and the template appears, the match center is written into this run variable (as a point).</summary>
    public string? FoundPointVar { get; init; }

    /// <summary>Edge taken when the template appears in time; <c>null</c> = end of run.</summary>
    public string? Found { get; init; }

    /// <summary>Edge taken when the wait times out; <c>null</c> = end of run.</summary>
    public string? Timeout { get; init; }
}

/// <summary>
/// Match a template SET against a region of the context window; the best match above
/// threshold wins. The winning template's name is written into <see cref="ResultVar"/>
/// and (with <see cref="ApplyTag"/>) applied to the window as a tag. Outcome picks the
/// edge: <see cref="Matched"/> / <see cref="NotMatched"/>.
/// </summary>
public sealed record RecognizeTagNode : MacroNode
{
    /// <summary>Template set name (resolved to <c>templates/{set}/*.png</c>; file name = tag).</summary>
    public required string TemplateSet { get; init; }

    /// <summary>Client-space crop the set is matched against.</summary>
    public required ScreenRect Region { get; init; }

    /// <summary><c>true</c> (default) = tag the context window with the winning template name.</summary>
    public bool ApplyTag { get; init; } = true;

    /// <summary>Run variable the winning template name is written to. Defaults to <c>"tag"</c>.</summary>
    public string ResultVar { get; init; } = "tag";

    /// <summary>Edge taken when a template matched; <c>null</c> = end of run.</summary>
    public string? Matched { get; init; }

    /// <summary>Edge taken when nothing matched above threshold; <c>null</c> = end of run.</summary>
    public string? NotMatched { get; init; }
}
