using System.Text.Json;
using System.Text.Json.Serialization;

namespace Greenlight.Sdk.Protocol;

/// <summary>
/// Writes enums as their member name, and reads an unrecognised name back as the zero
/// member instead of throwing.
/// </summary>
/// <remarks>
/// <para>
/// The tolerance is the point. A newer Greenlight that learns, say, a third provider must
/// not make its snapshots undeserializable by every SDK version already installed on the
/// machine — one unknown string would otherwise throw away the whole payload, including
/// the colour, which is the part every consumer actually needs. Each enum here therefore
/// has an <c>Unknown</c>-shaped zero member, and this converter routes anything it does not
/// recognise to it.
/// </para>
/// <para>
/// Names, not numbers, on the wire: a renumbered enum would otherwise silently change
/// meaning, and a JSON line you can read in a log is worth the handful of bytes.
/// </para>
/// </remarks>
internal sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // A number is accepted as a courtesy to hand-written payloads, but is equally
        // tolerant: an undefined value reads as the zero member rather than as itself,
        // so no consumer ever sees an enum value with no name.
        if (reader.TokenType == JsonTokenType.Number)
        {
            var boxed = Enum.ToObject(typeof(T), reader.GetInt32());
            return Enum.IsDefined(typeof(T), boxed) ? (T)boxed : default;
        }

        var name = reader.GetString();
        return name is not null && Enum.TryParse<T>(name, ignoreCase: true, out var parsed)
            ? parsed
            : default;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
