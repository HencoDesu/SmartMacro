namespace SmartMacro.Contracts.Dto;

/// <summary>
/// Wire form of one running macro. Mirror of Core's <c>MacroRunSnapshot</c>;
/// <see cref="StartedUtc"/> is a <see cref="DateTimeOffset"/> so the offset survives JSON
/// instead of relying on the reader to guess <c>DateTimeKind</c>.
/// </summary>
/// <param name="RunId">Unique id of the run. Passed back in <c>StopMacro</c>.</param>
/// <param name="MacroName">Name of the macro being run.</param>
/// <param name="StartedUtc">When the run began (UTC).</param>
/// <param name="CurrentNodeId">Id of the node the walker last entered; <c>null</c> before the first node.</param>
public sealed record RunningMacroDto(Guid RunId, string MacroName, DateTimeOffset StartedUtc, string? CurrentNodeId);
