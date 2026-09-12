using Greenlight.Sdk.Protocol;

namespace Greenlight.Sdk;

/// <summary>
/// A live view of the Greenlight running on this machine. Implemented by
/// <see cref="GreenlightClient"/>; the interface exists so consumer code can be tested
/// against a stub.
/// </summary>
/// <remarks>
/// <para>
/// <b>Events are raised on a background thread.</b> Marshal to your UI thread before
/// touching anything the UI owns — this is the single most likely way to get this wrong.
/// </para>
/// <para>
/// Nothing here throws because Greenlight is absent. That is a normal operating mode:
/// <see cref="Current"/> goes null, <see cref="Availability"/> says why, and commands come
/// back <see cref="CommandOutcome.Unavailable"/>.
/// </para>
/// </remarks>
public interface IGreenlightStatus
{
    /// <summary>Whether there is a Greenlight attached, and if not, what kind of not.</summary>
    GreenlightAvailability Availability { get; }

    /// <summary>
    /// The latest snapshot, or null when nothing is attached. Snapshots are immutable, so a
    /// reference you keep stays consistent even as newer ones arrive.
    /// </summary>
    GreenlightSnapshot? Current { get; }

    /// <summary>A new snapshot arrived. Background thread.</summary>
    event EventHandler<GreenlightSnapshotEventArgs>? Changed;

    /// <summary>The connection state changed. Background thread.</summary>
    event EventHandler<GreenlightAvailabilityEventArgs>? AvailabilityChanged;

    /// <summary>
    /// Snapshots as a stream, for consumers that would rather loop than subscribe. Yields
    /// <see cref="Current"/> first if there is one, then every subsequent snapshot, and ends
    /// when <paramref name="cancellationToken"/> fires. It does not end when Greenlight goes
    /// away — it simply goes quiet until Greenlight comes back.
    /// </summary>
    IAsyncEnumerable<GreenlightSnapshot> WatchAsync(CancellationToken cancellationToken = default);

    /// <summary>Dismiss specific build runs in Greenlight, as clicking them away in its UI would.</summary>
    Task<CommandResult> AcknowledgeBuildsAsync(IEnumerable<long> buildIds, CancellationToken cancellationToken = default);

    /// <summary>Dismiss every retained run of one pipeline.</summary>
    Task<CommandResult> AcknowledgePipelineAsync(string project, string pipeline, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ask Greenlight to poll every connection now. Rate-limited by the host across all
    /// clients — a refused call comes back with <see cref="CommandError.RateLimited"/>, which
    /// is not an error worth showing a user.
    /// </summary>
    Task<CommandResult> RefreshNowAsync(CancellationToken cancellationToken = default);

    /// <summary>Bring Greenlight's own dashboard window to the foreground.</summary>
    Task<CommandResult> ShowDashboardAsync(CancellationToken cancellationToken = default);
}
