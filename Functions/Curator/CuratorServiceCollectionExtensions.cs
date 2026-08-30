namespace Functions.Curator;

using System.Data.Common;
using System.Net;
using Azure.Identity;
using Functions;
using Functions.Churches;
using Functions.Curator.Catalog;
using Functions.Curator.Enrichment;
using Functions.Curator.Jobs;
using Functions.Curator.Library;
using Functions.Curator.OpenCritic;
using Functions.Curator.Psn;
using Functions.Curator.Rawg;
using Functions.Extensions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenAI.Responses;
using Resend;
using StackExchange.Redis;

public static class CuratorServiceCollectionExtensions
{
#pragma warning disable OPENAI001
    public static IServiceCollection AddCuratorServices(
        this IServiceCollection services,
        IConfiguration configuration,
        ResponsesClient responsesClient)
    {
        var sqlConnectionStringBuilderSection = configuration.GetRequiredSection(nameof(SqlConnectionStringBuilder));
        var sqlConnectionStringBuilder = sqlConnectionStringBuilderSection.Get<SqlConnectionStringBuilder>()
            ?? throw new InvalidOperationException($"Missing '{nameof(SqlConnectionStringBuilder)}' section.");
        var curatorDatabaseConnectionString = PostgresConnectionString.Normalize(
            configuration.GetRequired<string>(CuratorConfigurationKeys.CuratorDatabaseConnection));
        var storageUri = configuration.GetRequired<Uri>(CuratorConfigurationKeys.StorageUri);
        var serviceBusNamespace = configuration.GetRequired<string>(CuratorConfigurationKeys.ServiceBusFullyQualifiedNamespace);
        string redisHost = configuration.GetRequired<string>(CuratorConfigurationKeys.RedisHost);
        var redisPort = configuration.GetRequired<int>(CuratorConfigurationKeys.RedisPort);
        var redisSsl = configuration.GetRequired<bool>(CuratorConfigurationKeys.RedisSsl);
        var redisEndpoint = new DnsEndPoint(redisHost, redisPort);
        var redisConfigurationOptions = new ConfigurationOptions
        {
            Ssl = redisSsl,
            EndPoints = [redisEndpoint],
        };
        redisConfigurationOptions.Password = configuration.GetRequired<string>(CuratorConfigurationKeys.RedisPassword);
        redisConfigurationOptions.AbortOnConnectFail = false;
        var tokenCredential = new DefaultAzureCredential();

        services.AddAzureClients(azureClientFactoryBuilder =>
        {
            azureClientFactoryBuilder.UseCredential(tokenCredential);
            azureClientFactoryBuilder.AddBlobServiceClient(storageUri).WithName(AzureClientNames.Crgolden);
            azureClientFactoryBuilder.AddServiceBusClientWithNamespace(serviceBusNamespace).WithName(AzureClientNames.Crgolden);
            azureClientFactoryBuilder.AddServiceBusAdministrationClientWithNamespace(serviceBusNamespace).WithName(AzureClientNames.Crgolden);
        });

        services.AddSingleton(responsesClient);
        services.AddScoped<DbConnection>(_ =>
        {
            var dbConnection = SqlClientFactory.Instance.CreateConnection()
                ?? throw new InvalidOperationException($"{nameof(SqlClientFactory)} failed to create a {nameof(DbConnection)}.");
            dbConnection.ConnectionString = sqlConnectionStringBuilder.ConnectionString;
            return dbConnection;
        });
        services.AddKeyedSingleton<DbDataSource>("Curator", (_, _) => NpgsqlDataSource.Create(curatorDatabaseConnectionString));
        services.AddKeyedScoped<DbConnection>("Curator", (sp, _) => sp.GetRequiredKeyedService<DbDataSource>("Curator").CreateConnection());
        services.AddSingleton(sp => new OpenCriticCacheRepository(sp.GetRequiredKeyedService<DbDataSource>("Curator")));
        services.AddSingleton(sp => new JobRunsRepository(sp.GetRequiredKeyedService<DbDataSource>("Curator")));
        services.AddSingleton(sp => new CatalogRepository(sp.GetRequiredKeyedService<DbDataSource>("Curator")));
        services.AddSingleton(sp => new EnrichmentRepository(sp.GetRequiredKeyedService<DbDataSource>("Curator")));
        services.AddSingleton(sp => new LibraryRepository(sp.GetRequiredKeyedService<DbDataSource>("Curator")));
        services.AddSingleton(sp => new EntitlementPullRepository(sp.GetRequiredKeyedService<DbDataSource>("Curator")));
        services.AddSingleton(sp => new PsnLinkRepository(sp.GetRequiredKeyedService<DbDataSource>("Curator")));
        services.AddSingleton(sp => new EnrichmentKeysRepository(sp.GetRequiredKeyedService<DbDataSource>("Curator")));
        services.AddSingleton(new TokenCrypto(configuration.GetRequired<string>(CuratorConfigurationKeys.CuratorTokenKey)));
        services.AddSingleton(sp => new AccountActionLogRepository(
            sp.GetRequiredKeyedService<DbDataSource>("Curator")));
        services.AddSingleton<IConnectionMultiplexer>(
            _ => ConnectionMultiplexer.Connect(redisConfigurationOptions));
        services.AddSingleton(sp => sp.GetRequiredService<IConnectionMultiplexer>().GetDatabase());
        services.AddSingleton<IPsnRateLimiter>(
            sp => new RedisPsnRateLimiter(sp.GetRequiredService<IDatabase>()));
        services.AddSingleton(sp => new PsnAccessTokenCache(sp.GetRequiredService<IDatabase>()));
        services.AddSingleton<LibraryRefreshQueuePublisher>();
        var rawgEndpoint = configuration.GetRequired<Uri>(CuratorConfigurationKeys.RawgEndpoint);
        var openCriticEndpoint = configuration.GetRequired<Uri>(CuratorConfigurationKeys.OpenCriticEndpoint);
        services.AddHttpClient<IRawgClient, RawgClient>(
            (httpClient, _) => new RawgClient(httpClient, rawgEndpoint));
        services.AddHttpClient<IOpenCriticClient, OpenCriticClient>(
            (httpClient, _) => new OpenCriticClient(httpClient, openCriticEndpoint));
        services.AddSingleton<ICatalogClient, PsnCatalogClient>();
        services.AddSingleton<IPsnLibraryClient, PsnLibraryClient>();
        services.AddSingleton<IPsnTrophyClient, PsnTrophyClient>();
        services.AddScoped<ChurchWriter>();
        var resendApiToken = configuration.GetRequired<string>(CuratorConfigurationKeys.ResendApiToken);
        services.Configure<ResendClientOptions>(options => options.ApiToken = resendApiToken);
        services.AddHttpClient<ResendClient>();
        services.AddHttpClient();
        services
            .AddHttpClient(PsnSession.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(PsnSession.CreateDefaultHandler)
            .ConfigureHttpClient(PsnSession.ConfigureDefaults);
        services.AddTransient<IResend, ResendClient>();

        return services;
    }
#pragma warning restore OPENAI001
}
