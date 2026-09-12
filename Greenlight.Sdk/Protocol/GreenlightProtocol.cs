using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Greenlight.Sdk.Protocol;

/// <summary>
/// The wire contract itself: version, pipe naming, and the one serializer both ends use.
/// </summary>
public static class GreenlightProtocol
{
    /// <summary>
    /// Bumped only for a breaking change. A client that meets a different version reports
    /// <c>Incompatible</c> and disconnects rather than guessing at a format it does not know;
    /// additive changes (a new message type, a new enum member, a new field) do not bump it,
    /// because both ends are built to ignore what they do not recognise.
    /// </summary>
    public const int Version = 1;

    private const string PipePrefix = "greenlight.localapi.v1";

    /// <summary>
    /// The pipe this machine's Greenlight serves on, for the account the calling process
    /// runs as.
    /// </summary>
    /// <remarks>
    /// Named pipes are machine-global — the <c>Local\</c> prefix that scopes Greenlight's
    /// single-instance mutex to a session does nothing for a pipe. So the account goes in the
    /// name, or two Windows users each running Greenlight would collide and the second one's
    /// server would simply fail to bind. The name is disambiguation only; what actually keeps
    /// other accounts out is the ACL the server puts on the pipe.
    /// </remarks>
    public static string PipeNameForCurrentUser() => $"{PipePrefix}.{CurrentUserKey()}";

    /// <summary>Serialize one protocol message to its wire line (no trailing newline).</summary>
    public static string Serialize<T>(T message) where T : class =>
        JsonSerializer.Serialize(message, typeof(T), GreenlightJsonContext.Default);

    /// <summary>
    /// Read one protocol message back off the wire. Throws <see cref="JsonException"/> on a
    /// malformed line — callers decide whether that kills the connection or is merely
    /// skipped, and in this protocol it is always skipped.
    /// </summary>
    public static T? Deserialize<T>(string line) where T : class =>
        (T?)JsonSerializer.Deserialize(line, GreenlightJsonContext.Default.GetTypeInfo(typeof(T))
            ?? throw new InvalidOperationException($"{typeof(T)} is not part of the Greenlight protocol."));

    /// <summary>
    /// The <c>t</c> discriminator of a received line, or null if the line is not a JSON
    /// object with one. Callers ignore what they cannot name — never throw on it.
    /// </summary>
    public static string? PeekType(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("t", out var t) &&
                   t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A stable, non-reversible key for the current account. The Windows SID where it can be
    /// had — it survives a rename, which a username does not — and domain\user hashed
    /// otherwise, so the SDK still builds and runs on a non-Windows consumer.
    /// </summary>
    private static string CurrentUserKey()
    {
        var identity = WindowsSidOrNull() ?? $@"{Environment.UserDomainName}\{Environment.UserName}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToUpperInvariant()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static string? WindowsSidOrNull()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var current = System.Security.Principal.WindowsIdentity.GetCurrent();
            return current.User?.Value;
        }
        catch
        {
            // Impersonation, a stripped-down container, a sandboxed host — fall back to the
            // username rather than failing to work out a pipe name at all.
            return null;
        }
    }
}

/// <summary>Source-generated serializer for every protocol message.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HelloMessage))]
[JsonSerializable(typeof(SnapshotMessage))]
[JsonSerializable(typeof(CommandMessage))]
[JsonSerializable(typeof(AckMessage))]
[JsonSerializable(typeof(ByeMessage))]
[JsonSerializable(typeof(GreenlightSnapshot))]
internal sealed partial class GreenlightJsonContext : JsonSerializerContext;
