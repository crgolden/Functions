namespace Functions.Tests.Unit;

using System.Text.Json;
using Functions.Curator.Psn;

[Trait("Category", "Unit")]
public sealed class PsnDurableTokenTests
{
    [Fact]
    public void Deserialize_ABlobCarryingKeysBeyondTheTwoItBinds_BindsTheTwoFieldsAndIgnoresTheRest()
    {
        // Arrange
        var storedRefreshToken = Generated.NewRefreshToken();
        var storedRefreshExpiresAt = Generated.NewStoredRefreshTokenExpiry();
        var pythonWrittenBlob = JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [PsnDurableToken.RefreshTokenPropertyName] = storedRefreshToken,
            [PsnDurableToken.RefreshTokenExpiresAtPropertyName] = storedRefreshExpiresAt,
            [Generated.NewJsonPropertyName()] = Generated.NewExpiresInSeconds(),
            [Generated.NewJsonPropertyName()] = Generated.NewFieldValue(),
            [Generated.NewJsonPropertyName()] = Generated.NewAccessToken(),
        });

        // Act
        var durable = JsonSerializer.Deserialize<PsnDurableToken>(pythonWrittenBlob);

        // Assert
        Assert.NotNull(durable);
        Assert.Equal(storedRefreshToken, durable.RefreshToken);
        Assert.Equal(storedRefreshExpiresAt, durable.RefreshTokenExpiresAt);
    }

    [Fact]
    public void Deserialize_ABlobWithNoRefreshToken_LeavesBothFieldsNull()
    {
        // Arrange
        var pythonWrittenBlob = JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [Generated.NewJsonPropertyName()] = Generated.NewFieldValue(),
            [Generated.NewJsonPropertyName()] = Generated.NewFieldValue(),
        });

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
        var storedRefreshToken = Generated.NewRefreshToken();
        var storedRefreshExpiresAt = (double)Generated.NewStoredRefreshTokenExpiry();
        var durable = new PsnDurableToken
        {
            RefreshToken = storedRefreshToken,
            RefreshTokenExpiresAt = storedRefreshExpiresAt,
        };

        // Act
        using var written = JsonDocument.Parse(JsonSerializer.Serialize(durable));

        // Assert
        var keys = written.RootElement.EnumerateObject().Select(property => property.Name).ToList();
        Assert.Equal([PsnDurableToken.RefreshTokenPropertyName, PsnDurableToken.RefreshTokenExpiresAtPropertyName], keys);
        Assert.Equal(storedRefreshToken, written.RootElement.GetProperty(PsnDurableToken.RefreshTokenPropertyName).GetString());
        Assert.Equal(storedRefreshExpiresAt, written.RootElement.GetProperty(PsnDurableToken.RefreshTokenExpiresAtPropertyName).GetDouble());
    }

    [Fact]
    public void PropertyNames_AreTheSnakeCaseKeysCuratorsPythonWritesAndReads()
    {
        // Act
        string[] propertyNames = [PsnDurableToken.RefreshTokenPropertyName, PsnDurableToken.RefreshTokenExpiresAtPropertyName];

        // Assert
        Assert.Equal(["refresh_token", "refresh_token_expires_at"], propertyNames);
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
