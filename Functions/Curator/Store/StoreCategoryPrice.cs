namespace Functions.Curator.Store;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record StoreCategoryPrice
{
    internal const int CentsPerUnit = 100;

    internal const string IsFreeJsonName = "isFree";

    internal const string IsTiedToSubscriptionJsonName = "isTiedToSubscription";

    internal const string BasePriceJsonName = "basePrice";

    internal const string DiscountedPriceJsonName = "discountedPrice";

    internal const string DiscountTextJsonName = "discountText";

    [JsonPropertyName(IsFreeJsonName)]
    public bool? IsFree { get; init; }

    [JsonPropertyName(IsTiedToSubscriptionJsonName)]
    public bool? IsTiedToSubscription { get; init; }

    [JsonPropertyName(BasePriceJsonName)]
    public string? BasePrice { get; init; }

    [JsonPropertyName(DiscountedPriceJsonName)]
    public string? DiscountedPrice { get; init; }

    [JsonPropertyName(DiscountTextJsonName)]
    public string? DiscountText { get; init; }

    [JsonExtensionData]
    [AllowNull]
    public IDictionary<string, JsonElement> Unmapped { get => field; init => field = value ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal); } = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    public int? BaseCents => Cents(BasePrice);

    public int? DiscountedCents => Cents(DiscountedPrice);

    public static int? Cents(string? displayPrice)
    {
        if (string.IsNullOrWhiteSpace(displayPrice))
        {
            return null;
        }

        var digits = displayPrice.Trim().TrimStart('$').Replace(",", string.Empty, StringComparison.Ordinal);
        if (digits.Length == 0 || !char.IsDigit(digits[0]))
        {
            return null;
        }

        return decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
            ? (int)decimal.Round(amount * CentsPerUnit, MidpointRounding.AwayFromZero)
            : null;
    }
}
