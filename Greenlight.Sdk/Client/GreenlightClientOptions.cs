using Greenlight.Sdk.Protocol;

namespace Greenlight.Sdk;

/// <summary>Knobs for <see cref="GreenlightClient"/>. Every one has a sensible default.</summary>
public sealed class GreenlightClientOptions
{
    /// <summary>
    /// Override the pipe to connect to. Defaults to
    /// <see cref="GreenlightProtocol.PipeNameForCurrentUser"/>, which is what a real
    /// Greenlight serves; tests point this at an isolated name.
    /// </summary>
    public string? PipeName { get; set; }

    /// <summary>
    /// Override the transport wholesale. For tests — a consumer has no reason to set this.
    /// </summary>
    public IGreenlightTransport? Transport { get; set; }

    /// <summary>First retry gap after a failed connect. Doubles up to <see cref="MaxReconnectDelay"/>.</summary>
    public TimeSpan MinReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The longest the client will ever wait between attempts. Greenlight restarting under a
    /// Velopack update should be picked up in seconds, not minutes.
    /// </summary>
    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the host has to send its <c>hello</c> before the connection is abandoned.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait for a command's acknowledgement.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
