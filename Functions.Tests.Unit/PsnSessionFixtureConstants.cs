namespace Functions.Tests.Unit;

internal static class PsnSessionFixtureConstants
{
    internal const int OneAttempt = 1;
    internal const int OneAttemptThenOneRetry = 2;
    internal const int AuthorizeThenTokenThenResource = 3;
    internal const string OAuth2InvalidGrantError = "invalid_grant";
}
