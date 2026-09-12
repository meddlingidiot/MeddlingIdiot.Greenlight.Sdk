using Greenlight.Sdk.Protocol;

namespace Greenlight.Sdk;

/// <summary>
/// Whether there is a Greenlight to talk to. Every value other than
/// <see cref="Connected"/> is a disabled state: <see cref="IGreenlightStatus.Current"/> is
/// null and commands are refused without throwing.
/// </summary>
public enum GreenlightAvailability
{
    /// <summary>
    /// No Greenlight is serving the local API. Either it is not running, or it is starting
    /// up. The client keeps retrying in the background — this is not a terminal state and
    /// needs no action from the consumer.
    /// </summary>
    Unavailable = 0,

    /// <summary>A connection attempt is in flight.</summary>
    Connecting,

    /// <summary>Attached and receiving snapshots.</summary>
    Connected,

    /// <summary>
    /// Greenlight is running but the user has switched the local API off in its settings.
    /// Best-effort: it is inferred from the host's parting message, so a Greenlight that was
    /// never reachable in the first place reads as <see cref="Unavailable"/> instead.
    /// Worth surfacing differently in a UI — this one the user can fix.
    /// </summary>
    Disabled,

    /// <summary>
    /// The host speaks a protocol version this SDK does not. Retried slowly, in case an
    /// update on either side resolves it, but one of the two needs upgrading.
    /// </summary>
    Incompatible,
}

/// <summary>A new snapshot arrived. Raised on a background thread.</summary>
public sealed class GreenlightSnapshotEventArgs(GreenlightSnapshot snapshot) : EventArgs
{
    /// <summary>The whole current picture. Never null.</summary>
    public GreenlightSnapshot Snapshot { get; } = snapshot;
}

/// <summary>The connection state changed. Raised on a background thread.</summary>
public sealed class GreenlightAvailabilityEventArgs(GreenlightAvailability availability) : EventArgs
{
    /// <summary>The state now in effect.</summary>
    public GreenlightAvailability Availability { get; } = availability;
}
