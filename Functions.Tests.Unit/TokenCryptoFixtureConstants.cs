namespace Functions.Tests.Unit;

internal static class TokenCryptoFixtureConstants
{
    internal const int AesGcmNonceSizeBytes = 12;
    internal const int AesGcmTagSizeBytes = 16;
    internal const int AesGcmKeySizeBytes = 32;
    internal const int SchemeSizeBytes = 1;
    internal const byte NonCollidingFirstNonceByte = 0x00;
    internal const string PythonGeneratedKey = "KOZEl3PXkk9i2iVoJw-cepA5qIsK1ZK56K2ykqXZ17U=";
    internal const string PythonGeneratedTokenBase64 =
        "M6cqDHc7e8NuKxBHyAJZo1A4EIqL30HmNVAbbYNG0QWCuauJqFq9kKer7ezpzyv80HHYxkEkabsxZvRry7kobYXqC/fHwErXk2FkZwDaEraR9WO+RvSZTV3fAzgmyniKmDXu4YnXt/33EA==";

    internal const string PythonGeneratedPlaintext =
        "{\"refresh_token\": \"sample-refresh-token-value\", \"scope\": \"psn:mobile.v2.core\"}";
}
