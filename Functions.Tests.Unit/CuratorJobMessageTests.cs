namespace Functions.Tests.Unit;

using System.Text.Json;
using Functions.Curator.Library;
using static Shared.Testing.Generated;

[Trait("Category", "Unit")]
public sealed class CuratorJobMessageTests
{
    private static readonly JsonSerializerOptions CuratorWireFormat =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    [Fact]
    public void LibraryRefreshMessage_ReadsTheIdentitySubCuratorWritesAsAHyphenatedLowercaseString()
    {
        // Arrange
        var runId = NewRunId();
        var identitySub = NewIdentitySub();
        var body = JsonSerializer.Serialize(
            new { RunId = runId, IdentitySub = identitySub.ToString("D"), Seq = NewJobRunSeq() },
            CuratorWireFormat);

        // Act
        var payload = JsonSerializer.Deserialize<LibraryRefreshMessage>(body);

        // Assert
        Assert.Equal(identitySub, payload?.IdentitySub);
    }

    [Fact]
    public void LibraryRefreshContinuationMessage_WritesTheIdentitySubAndGameIdsAsHyphenatedLowercaseStrings()
    {
        // Arrange
        var identitySub = NewIdentitySub();
        var remainingGameId = NewGameId();
        var message = new LibraryRefreshContinuationMessage
        {
            RunId = NewRunId(),
            IdentitySub = identitySub,
            RemainingGameIds = [remainingGameId],
            RetryAfterSeconds = NewRetryAfterSeconds(),
        };

        // Act
        using var written = JsonDocument.Parse(JsonSerializer.Serialize(message));

        // Assert
        var root = written.RootElement;
        Assert.Equal(identitySub.ToString("D"), root.GetProperty(WireName(nameof(message.IdentitySub))).GetString());
        Assert.Equal(
            [remainingGameId.ToString("D")],
            root.GetProperty(WireName(nameof(message.RemainingGameIds))).EnumerateArray().Select(element => element.GetString()).OfType<string>());
    }

    private static string WireName(string memberName) => JsonNamingPolicy.SnakeCaseLower.ConvertName(memberName);
}
