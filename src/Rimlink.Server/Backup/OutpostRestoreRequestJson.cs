using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;

namespace Rimlink.Server.Backup;

public static class OutpostRestoreRequestJson
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32, AllowDuplicateProperties = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, Converters = { new AnchorBytesConverter() }
    };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static async Task<T> ReadAsync<T>(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        if (stream.Length is <= 0 or > 32 * 1024 * 1024) throw new InvalidDataException("restore-request-size-limit");
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct) ?? throw new InvalidDataException("restore-request-missing");
    }
    // Preserve the normal CLI's existing numeric-array representation of anchor ByteString fields.
    private sealed class AnchorBytesConverter : JsonConverter<ByteString>
    {
        public override ByteString Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("anchor bytes must be an array");
            var bytes = new List<byte>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (bytes.Count >= 4096 || reader.TokenType != JsonTokenType.Number || !reader.TryGetByte(out byte value))
                    throw new JsonException("anchor byte-array limit or value");
                bytes.Add(value);
            }
            if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("incomplete anchor bytes");
            return ByteString.CopyFrom(bytes.ToArray());
        }
        public override void Write(Utf8JsonWriter writer, ByteString value, JsonSerializerOptions options)
        {
            if (value.Length > 4096) throw new JsonException("anchor byte-array limit");
            writer.WriteStartArray();
            foreach (byte item in value) writer.WriteNumberValue(item);
            writer.WriteEndArray();
        }
    }
}
