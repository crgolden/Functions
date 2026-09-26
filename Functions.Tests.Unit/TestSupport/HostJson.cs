namespace Functions.Tests.Unit.TestSupport;

using System.Globalization;
using System.Text.Json;

internal static class HostJson
{
    internal const string FileName = AzureFunctionsHostFixtureConstants.HostFileName;
    internal const string FunctionTimeoutPropertyName = AzureFunctionsHostFixtureConstants.FunctionTimeoutPropertyName;

    internal static TimeSpan FunctionTimeout { get; } = ReadFunctionTimeout();

    private static TimeSpan ReadFunctionTimeout()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FileName));
        return TimeSpan.Parse(
            document.RootElement.GetProperty(FunctionTimeoutPropertyName).GetString()
                ?? throw new InvalidOperationException($"{FileName} carries no {FunctionTimeoutPropertyName}."),
            CultureInfo.InvariantCulture);
    }
}
