namespace Functions.Extensions;

using Azure.Security.KeyVault.Secrets;

public static class SecretClientExtensions
{
    extension(SecretClient secretClient)
    {
#pragma warning disable SA1009
        public (
            KeyVaultSecret ResendApiToken,
            KeyVaultSecret SqlServerUserId,
            KeyVaultSecret SqlServerPassword,
            KeyVaultSecret ElasticsearchUsername,
            KeyVaultSecret ElasticsearchPassword
            ) GetFunctionsSecrets()
        {
            var resendApiToken = secretClient.GetSecret("ResendApiToken");
            var sqlServerUserId = secretClient.GetSecret("DirectorySqlServerUserId");
            var sqlServerPassword = secretClient.GetSecret("DirectorySqlServerPassword");
            var elasticsearchUsername = secretClient.GetSecret("ElasticsearchUsername");
            var elasticsearchPassword = secretClient.GetSecret("ElasticsearchPassword");
            return (
                resendApiToken.Value,
                sqlServerUserId.Value,
                sqlServerPassword.Value,
                elasticsearchUsername.Value,
                elasticsearchPassword.Value
            );
        }
#pragma warning restore SA1009
    }
}
