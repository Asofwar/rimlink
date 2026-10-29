using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rimlink.Server.Rest;

/// <summary>
/// REST dates are UTC with the "Z" marker. SQLite hands DateTime back with Kind=Unspecified; written as-is, a browser
/// would read it as local time (the panel showed times off by the zone offset and a fresh mute as expired).
/// </summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTime.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value,
        };
        writer.WriteStringValue(utc.ToString("O", CultureInfo.InvariantCulture));
    }
}
