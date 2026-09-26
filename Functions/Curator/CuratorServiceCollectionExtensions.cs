namespace Functions.Curator;

using System.Data.Common;
using System.Net;
using Azure.Identity;
using Functions;
using Functions.Churches;
using Functions.Churches.Extraction;
using Functions.Churches.Geocoding;
using Functions.Curator.Catalog;
using Functions.Curator.Enrichment;
using Functions.Curator.Jobs;
using Functions.Curator.Library;
using Functions.Curator.OpenCritic;
using Functions.Curator.Psn;
using Functions.Curator.Rawg;
using Functions.Curator.Store;
using Functions.Extensions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenAI.Responses;
using Polly;
using Resend;
using StackExchange.Redis;

public static class CuratorServiceCollectionExtensions
{
    public const string CuratorServiceKey = "Curator";

    internal const string ResendRetryPipelineName = "resend-rate-limit";

    internal const int ResendMaxRetryAttempts = 5;

    internal static readonly TimeSpan ResendRetryDelay = TimeSpan.FromSeconds(1);

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
        var telemetryOptions = configuration.GetRequiredSection(nameof(TelemetryOptions)).Get<TelemetryOptions>()
            ?? throw new InvalidOperationException($"Missing '{nameof(TelemetryOptions)}' section.");

        services.AddMetrics();
        services.AddSingleton(Options.Create(telemetryOptions));
        services.AddSingleton<Telemetry>();

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
        services.AddKeyedSingleton<DbDataSource>(CuratorServiceKey, (_, _) => NpgsqlDataSource.Create(curatorDatabaseConnectionString));
        services.AddKeyedScoped<DbConnection>(CuratorServiceKey, (sp, _) => sp.GetRequiredKeyedService<DbDataSource>(CuratorServiceKey).CreateConnection());
        services.AddSingleton(sp => new OpenCriticCacheRepository(sp.GetRequiredKeyedService<DbDataSource>(CuratorServiceKey)));
        services.AddSingleton(sp => new JobRunsRepository(sp.GetRequiredKeyedService<DbDataSource>(CuratorServiceKey)));
        services.AddSingleton(sp => new CatalogRepository(sp.GetRequiredKeyedService<DbDataSource>(CuratorServiceKey)));
        services.AddSingleton(sp => new EnrichmentRepository(sp.GetRequiredKeyedService<DbDataSource>(CuratorServiceKey)));
        services.AddSingleton(sp => new StoreCatalogCrawlRepository(sp.GetRequiredKeyedService<DbDataSource>(CuratorServiceKey)));
        services.AddSingleton(sp => new LibraryRepository(sp.GetRequiredKeyedService<DbDataSource>(CuratorServiceKey)));
        services.AddSingleton(sp => new EntitlementPullRepository(sp.GetRequiredKeyedService<DbDataSource>(CuratorServiceKey)));
        services.AddSingleton(sp => new PsnLinkRepository(sp.GetRequiredKeyedService<DbDataSource>(CuratorServiceKey)));
        services.AddSingleton(sp => new EnrichmentKeysRepository(sp.GetRequiredKeyedService<DbDataSource>(CuratorServiceKey)));
        services.AddSingleton(new TokenCrypto(configuration.GetRequired<string>(CuratorConfigurationKeys.CuratorTokenKey)));
        services.AddSingleton(sp => new AccountActionLogRepository(
            sp.GetRequiredKeyedService<DbDataSource>(CuratorServiceKey)));
        services.AddSingleton<IConnectionMultiplexer>(
            _ => ConnectionMultiplexer.Connect(redisConfigurationOptions));
        services.AddSingleton(sp => sp.GetRequiredService<IConnectionMultiplexer>().GetDatabase());
        services.AddSingleton<IPsnRateLimiter>(
            sp => new RedisPsnRateLimiter(sp.GetRequiredService<IDatabase>()));
        services.AddSingleton<IRawgRateLimiterFactory, RedisRawgRateLimiterFactory>();
        services.AddSingleton<IOpenAIRateLimiter>(
            sp => new RedisOpenAIRateLimiter(sp.GetRequiredService<IDatabase>()));
        services.AddSingleton(sp => new PsnAccessTokenCache(sp.GetRequiredService<IDatabase>()));
        services.AddSingleton<LibraryRefreshQueuePublisher>();
        var rawgEndpoint = configuration.GetRequired<Uri>(CuratorConfigurationKeys.RawgEndpoint);
        var openCriticEndpoint = configuration.GetRequired<Uri>(CuratorConfigurationKeys.OpenCriticEndpoint);
        services.AddHttpClient<IRawgClient, RawgClient>(
            (httpClient, _) => new RawgClient(httpClient, rawgEndpoint));
        services.AddHttpClient<IOpenCriticClient, OpenCriticClient>(
            (httpClient, _) => new OpenCriticClient(httpClient, openCriticEndpoint));
        services.AddHttpClient<IStoreGatewayClient, StoreGatewayClient>();
        services.AddSingleton<ICatalogClient, PsnCatalogClient>();
        services.AddSingleton<IPsnLibraryClient, PsnLibraryClient>();
        services.AddSingleton<IPsnTrophyClient, PsnTrophyClient>();
        services.AddSingleton(sp => new LeasedJobRunner(
            sp.GetRequiredService<JobRunsRepository>(), sp.GetRequiredService<Telemetry>()));
        services.AddSingleton<IngestionService>();
        services.AddSingleton<ContinuationScheduler>();
        services.AddSingleton<RejectedProviderRecorder>();
        services.AddSingleton<TrophyMatchService>();
        services.AddSingleton<EnrichmentBatchProcessor>();
        services.AddSingleton<LibraryBuildOrchestrator>();
        services.AddSingleton<LibraryRefreshProcessor>();
        services.AddSingleton<LibraryRefreshContinuationProcessor>();
        services.AddSingleton<EnrichmentRunProcessor>();
        services.AddSingleton<UserSessionFactory>();
        services.AddSingleton(new AdminProviderKeys(
            configuration.ConfiguredValues(CuratorConfigurationKeys.RawgApiKey),
            configuration.ConfiguredValues(CuratorConfigurationKeys.OpenCriticRapidApiKey),
            configuration.ConfiguredValues(CuratorConfigurationKeys.PsnNpsso)));
        services.AddTransient<EnrichmentServiceFactory>();
        services.AddTransient<AdminEnrichmentFactory>();
        services.AddSingleton<ChurchQueueSenders>();
        services.AddScoped<ChurchWriter>();
        services.AddTransient(sp => new CensusGeocoder(
            sp.GetRequiredService<IHttpClientFactory>(),
            configuration.GetRequired<Uri>(ChurchSettingKeys.CensusGeocoderUrl),
            sp.GetRequiredService<Telemetry>()));
        var resendApiToken = configuration.GetRequired<string>(CuratorConfigurationKeys.ResendApiToken);
        services.Configure<ResendClientOptions>(options => options.ApiToken = resendApiToken);
        services
            .AddHttpClient<IResend, ResendClient>()
            .AddResilienceHandler(ResendRetryPipelineName, builder => builder
                .AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = ResendMaxRetryAttempts,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = ResendRetryDelay,
                    UseJitter = true,
                    ShouldHandle = args => ValueTask.FromResult(
                        args.Outcome.Result?.StatusCode == HttpStatusCode.TooManyRequests)
                }));
        services.AddHttpClient();
        services
            .AddHttpClient(PsnSession.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(PsnSession.CreateDefaultHandler)
            .ConfigureHttpClient(PsnSession.ConfigureDefaults);

        return services;
    }
#pragma warning restore OPENAI001
}
