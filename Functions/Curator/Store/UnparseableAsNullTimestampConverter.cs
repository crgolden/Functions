namespace Functions.Curator.Store;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class UnparseableAsNullTimestampConverter : JsonConverter<DateTimeOffset?>
{
    public override bool HandleNull => true;

    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return null;
        }

        return DateTimeOffset.TryParse(reader.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is { } timestamp)
        {
            writer.WriteStringValue(timestamp);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
