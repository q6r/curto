using System.Text.Json;
using System.Text.Json.Serialization;

namespace Curto.Api.Infrastructure;

/// <summary>
/// ISO 8601 timestamps that always serialise as UTC with a <c>Z</c> suffix. Input must be ISO 8601 and
/// carry an explicit offset, so a value never depends on the server's time zone.
/// </summary>
internal sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    /// <summary>Message used whenever a timestamp is rejected.</summary>
    public const string ExpectedFormat = "Expected an ISO 8601 timestamp with an explicit UTC offset, e.g. 2030-01-31T12:00:00Z.";

    /// <summary>Reads a timestamp from an already parsed JSON value, applying the same rules as deserialisation.</summary>
    /// <param name="element">JSON value.</param>
    /// <param name="value">The instant, converted to UTC.</param>
    public static bool TryRead(JsonElement element, out DateTimeOffset value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.String
            || element.GetString() is not { } text
            || !HasExplicitOffset(text)
            || !element.TryGetDateTimeOffset(out var parsed))
        {
            return false;
        }

        value = parsed.ToUniversalTime();
        return true;
    }

    /// <inheritdoc />
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String
            || reader.GetString() is not { } text
            || !HasExplicitOffset(text)
            || !reader.TryGetDateTimeOffset(out var value))
        {
            throw new JsonException(ExpectedFormat);
        }

        return value.ToUniversalTime();
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // DateTime values of kind Utc are written with a "Z" suffix; DateTimeOffset would print "+00:00".
        writer.WriteStringValue(value.UtcDateTime);
    }

    private static bool HasExplicitOffset(ReadOnlySpan<char> text)
    {
        var timeStart = text.IndexOf('T');
        if (timeStart < 0)
        {
            return false;
        }

        var time = text[timeStart..];
        return time.Contains('Z') || time.IndexOfAny('+', '-') >= 0;
    }
}
