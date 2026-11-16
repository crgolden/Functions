namespace Functions.Curator;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class AbsoluteUriConverter : JsonConverter<Uri?>
{
    public override bool HandleNull => true;

    public override Uri? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return null;
        }

        return Uri.TryCreate(reader.GetString(), UriKind.Absolute, out var absolute) ? absolute : null;
    }

    public override void Write(Utf8JsonWriter writer, Uri? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value.OriginalString);
    }
}
