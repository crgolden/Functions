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
            KeyVaultSecret SqlServerPassword
            ) GetFunctionsSecrets()
        {
            var resendApiToken = secretClient.GetSecret("ResendApiToken");
            var sqlServerUserId = secretClient.GetSecret("DirectorySqlServerUserId");
            var sqlServerPassword = secretClient.GetSecret("DirectorySqlServerPassword");
            return (
                resendApiToken.Value,
                sqlServerUserId.Value,
                sqlServerPassword.Value
            );
        }
#pragma warning restore SA1009
    }
}
