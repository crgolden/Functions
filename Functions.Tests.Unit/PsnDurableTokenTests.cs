namespace Functions.Tests.Unit;

using System.Text.Json;
using Curator.Psn;

[Trait("Category", "Unit")]
public sealed class PsnDurableTokenTests
{
    [Fact]
    public void Deserialize_APythonWrittenBlobCarryingEveryNonEphemeralKey_BindsTheTwoFieldsAndIgnoresTheRest()
    {
        // Arrange
        var storedRefreshToken = $"refresh-{Guid.NewGuid():N}";
        var storedRefreshExpiresAt = DateTimeOffset.UtcNow.AddDays(Random.Shared.Next(1, 60)).ToUnixTimeSeconds();
        var pythonWrittenBlob = $$"""
            {
              "refresh_token": "{{storedRefreshToken}}",
              "refresh_token_expires_at": {{storedRefreshExpiresAt}},
              "refresh_token_expires_in": 5184000,
              "token_type": "bearer",
              "scope": "psn:mobile.v2.core psn:clientapp",
              "id_token": "eyJhbGciOiJSUzI1NiJ9.e30.",
              "cid": "00000000-0000-0000-0000-000000000000"
            }
            """;

        // Act
        var durable = JsonSerializer.Deserialize<PsnDurableToken>(pythonWrittenBlob);

        // Assert
        Assert.NotNull(durable);
        Assert.Equal(storedRefreshToken, durable.RefreshToken);
        Assert.Equal(storedRefreshExpiresAt, durable.RefreshTokenExpiresAt);
    }

    [Fact]
    public void Deserialize_APythonWrittenBlobWithNoRefreshToken_LeavesBothFieldsNull()
    {
        // Arrange
        var pythonWrittenBlob = """
            {
              "token_type": "bearer",
              "scope": "psn:mobile.v2.core psn:clientapp"
            }
            """;

        // Act
        var durable = JsonSerializer.Deserialize<PsnDurableToken>(pythonWrittenBlob);

        // Assert
        Assert.NotNull(durable);
        Assert.Null(durable.RefreshToken);
        Assert.Null(durable.RefreshTokenExpiresAt);
    }

    [Fact]
    public void Serialize_WithBothFieldsSet_WritesExactlyTheTwoSnakeCaseKeysPythonReads()
    {
        // Arrange
        var storedRefreshToken = $"refresh-{Guid.NewGuid():N}";
        var storedRefreshExpiresAt = (double)DateTimeOffset.UtcNow.AddDays(Random.Shared.Next(1, 60)).ToUnixTimeSeconds();
        var durable = new PsnDurableToken
        {
            RefreshToken = storedRefreshToken,
            RefreshTokenExpiresAt = storedRefreshExpiresAt,
        };

        // Act
        using var written = JsonDocument.Parse(JsonSerializer.Serialize(durable));

        // Assert
        var keys = written.RootElement.EnumerateObject().Select(property => property.Name).ToList();
        Assert.Equal(["refresh_token", "refresh_token_expires_at"], keys);
        Assert.Equal(storedRefreshToken, written.RootElement.GetProperty("refresh_token").GetString());
        Assert.Equal(storedRefreshExpiresAt, written.RootElement.GetProperty("refresh_token_expires_at").GetDouble());
    }

    [Fact]
    public void Serialize_WithNoRefreshToken_OmitsBothKeysRatherThanWritingNulls()
    {
        // Arrange
        var durable = new PsnDurableToken();

        // Act
        using var written = JsonDocument.Parse(JsonSerializer.Serialize(durable));

        // Assert
        Assert.Empty(written.RootElement.EnumerateObject());
    }
}
