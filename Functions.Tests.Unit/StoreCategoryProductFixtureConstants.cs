namespace Functions.Tests.Unit;

using Functions.Curator.Store;

internal static class StoreCategoryProductFixtureConstants
{
    internal const string TypeNameKey = "__typename";

    internal const string SkusKey = "skus";

    internal const string PriceKey = StoreCategoryProduct.PriceJsonName;

    internal const string UpsellTextKey = "upsellText";

    internal const string PreviewRole = "PREVIEW";

    internal const string VideoMediaType = "VIDEO";

    internal const string ProductTypeName = "Product";

    internal const string PriceTypeName = "SkuPrice";

    internal const string MediaTypeName = "Media";

    internal const string SkuTypeName = "Sku";

    internal const string FreeDisplayPrice = "Free";

    internal const string IncludedDisplayPrice = "Included";

    internal const string CentsSuffix = ".99";

    internal const int CentsPart = 99;

    internal const int ThousandsPlaceValue = 1000;

    internal const string CurrencyPrefix = "$";

    internal const string ThousandsSeparator = ",";
}
