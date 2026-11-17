namespace Functions.Curator.Store;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class StoreCategoryProductsConverter : JsonConverter<IReadOnlyList<StoreCategoryProduct>>
{
    public override IReadOnlyList<StoreCategoryProduct> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return [];
        }

        var products = new List<StoreCategoryProduct>();
        using var nodes = JsonDocument.ParseValue(ref reader);
        foreach (var node in nodes.RootElement.EnumerateArray())
        {
            var product = node.Deserialize<StoreCategoryProduct>(options);
            if (product is not null)
            {
                products.Add(product with { Raw = node.GetRawText() });
            }
        }

        return products;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<StoreCategoryProduct> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartArray();
        foreach (var product in value)
        {
            JsonSerializer.Serialize(writer, product, options);
        }

        writer.WriteEndArray();
    }
}
