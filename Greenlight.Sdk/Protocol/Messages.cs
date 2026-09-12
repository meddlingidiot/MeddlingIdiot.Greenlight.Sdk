using System.Text.Json.Serialization;

namespace Greenlight.Sdk.Protocol;

/// <summary>
/// The <c>t</c> discriminator on every line of the protocol. A receiver that meets a type
/// it does not know must ignore that line rather than fail — that is what lets a newer
/// host add a message without breaking older clients.
/// </summary>
public static class MessageType
{
    /// <summary>Server → client, first line on every connection.</summary>
    public const string Hello = "hello";
    /// <summary>Server → client, on connect and on every state change.</summary>
    public const string Snapshot = "snapshot";
    /// <summary>Client → server.</summary>
    public const string Command = "command";
    /// <summary>Server → client, one per command.</summary>
    public const string Ack = "ack";
    /// <summary>Server → client, sent before a deliberate close.</summary>
    public const string Bye = "bye";
}

/// <summary>The commands a client may send. The host advertises the subset it will accept in <see cref="HelloMessage.Commands"/>.</summary>
public static class CommandName
{
    /// <summary>Dismiss specific build runs. Args: <see cref="CommandArgs.BuildIds"/>.</summary>
    public const string AcknowledgeBuilds = "acknowledge_builds";
    /// <summary>Dismiss every retained run of one pipeline. Args: <see cref="CommandArgs.Project"/>, <see cref="CommandArgs.Pipeline"/>.</summary>
    public const string AcknowledgePipeline = "acknowledge_pipeline";
    /// <summary>Poll every connection now, instead of waiting for the next interval. Rate-limited by the host.</summary>
    public const string RefreshNow = "refresh_now";
    /// <summary>Bring Greenlight's dashboard window to the foreground.</summary>
    public const string ShowDashboard = "show_dashboard";
}

/// <summary>Why the host closed a connection, as carried by <see cref="ByeMessage"/>.</summary>
public static class ByeReason
{
    /// <summary>The user switched the local API off. The client reports <c>Disabled</c>, not merely unavailable.</summary>
    public const string Disabled = "disabled";
    /// <summary>Greenlight is shutting down.</summary>
    public const string Shutdown = "shutdown";
}

/// <summary>Standard <see cref="AckMessage.Error"/> codes. The accompanying message is for humans; match on these.</summary>
public static class CommandError
{
    /// <summary>Commands are switched off in Greenlight's settings.</summary>
    public const string CommandsDisabled = "commands_disabled";
    /// <summary>Too soon after the last accepted call. Back off and try later.</summary>
    public const string RateLimited = "rate_limited";
    /// <summary>This host does not know the command. Check <see cref="HelloMessage.Commands"/>.</summary>
    public const string UnknownCommand = "unknown_command";
    /// <summary>The arguments were missing or malformed.</summary>
    public const string BadRequest = "bad_request";
    /// <summary>The command was understood and went wrong anyway.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// Server → client. Always the first line. A client compares <see cref="V"/> against
/// <see cref="GreenlightProtocol.Version"/> and refuses to guess across a mismatch.
/// </summary>
/// <param name="V">Protocol version spoken by the host.</param>
/// <param name="App">The Greenlight version, for display and diagnostics.</param>
/// <param name="Commands">Exactly the commands this host will accept right now.</param>
public sealed record HelloMessage(int V, string App, IReadOnlyList<string> Commands)
{
    /// <summary>The discriminator. Always <see cref="MessageType.Hello"/>.</summary>
    [JsonPropertyName("t")] public string T => MessageType.Hello;
}

/// <summary>Server → client. The whole picture; there are no deltas.</summary>
/// <param name="Seq">Monotonic per connection, so a consumer can spot a gap in its own logging.</param>
public sealed record SnapshotMessage(long Seq, GreenlightSnapshot Data)
{
    /// <summary>The discriminator. Always <see cref="MessageType.Snapshot"/>.</summary>
    [JsonPropertyName("t")] public string T => MessageType.Snapshot;
}

/// <summary>Client → server.</summary>
/// <param name="Id">Correlates the <see cref="AckMessage"/>. Unique per connection.</param>
/// <param name="Name">One of <see cref="CommandName"/>.</param>
public sealed record CommandMessage(string Id, string Name, CommandArgs? Args = null)
{
    /// <summary>The discriminator. Always <see cref="MessageType.Command"/>.</summary>
    [JsonPropertyName("t")] public string T => MessageType.Command;
}

/// <summary>
/// Every argument any command takes, in one shape. A flat optional bag beats a polymorphic
/// hierarchy here: the set is tiny, it keeps the source-generated serializer free of
/// type-discriminator handling, and an unknown field costs a reader nothing.
/// </summary>
public sealed record CommandArgs(
    IReadOnlyList<long>? BuildIds = null,
    string? Project = null,
    string? Pipeline = null);

/// <summary>Server → client. One per command, always, whether it worked or not.</summary>
/// <param name="Id">The <see cref="CommandMessage.Id"/> being answered.</param>
/// <param name="Error">One of <see cref="CommandError"/> when <paramref name="Ok"/> is false; otherwise null.</param>
/// <param name="Message">Human-readable detail. Never match on this.</param>
public sealed record AckMessage(string Id, bool Ok, string? Error = null, string? Message = null)
{
    /// <summary>The discriminator. Always <see cref="MessageType.Ack"/>.</summary>
    [JsonPropertyName("t")] public string T => MessageType.Ack;
}

/// <summary>
/// Server → client, immediately before a deliberate close. Distinguishes "the user turned
/// this off" from "the process vanished", which are the same silence on the wire but very
/// different things to show a user.
/// </summary>
/// <param name="Reason">One of <see cref="ByeReason"/>.</param>
public sealed record ByeMessage(string Reason)
{
    /// <summary>The discriminator. Always <see cref="MessageType.Bye"/>.</summary>
    [JsonPropertyName("t")] public string T => MessageType.Bye;
}
