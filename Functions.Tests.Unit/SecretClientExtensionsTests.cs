namespace Functions.Tests.Unit;

using Azure;
using Azure.Security.KeyVault.Secrets;
using Functions.Extensions;
using Moq;

[Trait("Category", "Unit")]
public sealed class SecretClientExtensionsTests
{
    [Fact]
    public void GetFunctionsSecrets_ReturnsTupleWithAllSecretValues()
    {
        var values = new Dictionary<string, string>
        {
            ["ResendApiToken"] = "resend-token",
            ["DirectorySqlServerUserId"] = "sql-user",
            ["DirectorySqlServerPassword"] = "sql-pass",
            ["ElasticsearchUsername"] = "elastic-user",
            ["ElasticsearchPassword"] = "elastic-pass",
        };
        var mock = new Mock<SecretClient>();
        mock.Setup(c => c.GetSecret(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<SecretContentType?>(), It.IsAny<CancellationToken>()))
            .Returns<string, string?, SecretContentType?, CancellationToken>((name, _, _, _) => SecretResponse(name, values[name]));

        var (resendApiToken, sqlServerUserId, sqlServerPassword, elasticsearchUsername, elasticsearchPassword) = mock.Object.GetFunctionsSecrets();

        Assert.Equal("resend-token", resendApiToken.Value);
        Assert.Equal("sql-user", sqlServerUserId.Value);
        Assert.Equal("sql-pass", sqlServerPassword.Value);
        Assert.Equal("elastic-user", elasticsearchUsername.Value);
        Assert.Equal("elastic-pass", elasticsearchPassword.Value);
    }

    private static Response<KeyVaultSecret> SecretResponse(string name, string value) =>
        Response.FromValue(new KeyVaultSecret(name, value), Mock.Of<Response>());
}
