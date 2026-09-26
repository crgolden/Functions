namespace Functions.Tests.Unit;

internal static class PsnSessionFixtureConstants
{
    internal const int OneAttempt = 1;
    internal const int OneAttemptThenOneRetry = 2;
    internal const int AuthorizeThenTokenThenResource = 3;
    internal const string OAuth2InvalidGrantError = "invalid_grant";
    internal const string LowercasePercentEncodedDotDot = "%2e%2e";
    internal const string UppercasePercentEncodedDotDot = "%2E%2E";
    internal const string DotThenPercentEncodedDot = ".%2e";
    internal const string PercentEncodedDotThenDot = "%2e.";
    internal const string BackslashSeparatedDotDot = @"a\..";
    internal const string PercentEncodedBackslashThenDotDot = "%5c..";
    internal const string DoublePercentEncodedDotDot = "%252e%252e";
    internal const string ThreeDots = "...";
}
