using Greenlight.Sdk.Protocol;

namespace Greenlight.Sdk;

/// <summary>How a command ended.</summary>
public enum CommandOutcome
{
    /// <summary>Greenlight accepted and performed it.</summary>
    Ok = 0,
    /// <summary>There was no Greenlight attached to send it to, or the connection died mid-flight.</summary>
    Unavailable,
    /// <summary>Greenlight received it and said no. <see cref="CommandResult.Error"/> says why.</summary>
    Refused,
    /// <summary>Sent, but no answer came back in time.</summary>
    TimedOut,
}

/// <summary>
/// The outcome of a command. Commands return one of these rather than throwing when
/// Greenlight is absent — a consumer app runs perfectly well next to a Greenlight that
/// isn't there, and having to wrap every call in a try/catch for the normal case would be
/// the wrong shape entirely.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Error">For <see cref="CommandOutcome.Refused"/>, one of <see cref="CommandError"/>. Match on this.</param>
/// <param name="Message">Human-readable detail, for logs and tooltips. Never match on this.</param>
public sealed record CommandResult(CommandOutcome Outcome, string? Error = null, string? Message = null)
{
    /// <summary>Shorthand for <see cref="CommandOutcome.Ok"/>.</summary>
    public bool IsOk => Outcome == CommandOutcome.Ok;

    internal static CommandResult Ok { get; } = new(CommandOutcome.Ok);
    internal static CommandResult Unavailable { get; } = new(CommandOutcome.Unavailable);
    internal static CommandResult TimedOut { get; } = new(CommandOutcome.TimedOut);
    internal static CommandResult Refused(string? error, string? message) =>
        new(CommandOutcome.Refused, error ?? CommandError.Failed, message);
}
