using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aria2Gui.Services.Aria2;

/// <summary>aria2 returns every numeric value as a JSON string ("12345"); convert transparently.</summary>
public sealed class Aria2LongConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => long.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0,
            JsonTokenType.Number => reader.GetInt64(),
            _ => 0,
        };

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}

/// <summary>aria2 returns booleans as the strings "true"/"false".</summary>
public sealed class Aria2BoolConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => string.Equals(reader.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            JsonTokenType.True => true,
            _ => false,
        };

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value ? "true" : "false");
}

/// <summary>
/// Source-generated JSON context for aria2 RPC payloads — no reflection, trim/AOT safe.
/// </summary>
[JsonSourceGenerationOptions(Converters = new[] { typeof(Aria2LongConverter), typeof(Aria2BoolConverter) })]
[JsonSerializable(typeof(Aria2Download))]
[JsonSerializable(typeof(List<Aria2Download>))]
[JsonSerializable(typeof(Aria2GlobalStat))]
[JsonSerializable(typeof(Aria2VersionInfo))]
[JsonSerializable(typeof(List<Aria2Peer>))]
public sealed partial class Aria2JsonContext : JsonSerializerContext;
