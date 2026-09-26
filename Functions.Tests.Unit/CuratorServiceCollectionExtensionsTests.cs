namespace Functions.Tests.Unit;

using System.ClientModel;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Azure.Storage.Blobs;
using Functions.Churches.Extraction;
using Functions.Curator;
using Functions.Curator.Enrichment;
using Functions.Curator.Library;
using Functions.Curator.OpenCritic;
using Functions.Curator.Psn;
using Functions.Curator.Rawg;
using Functions.Tests.Unit.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using OpenAI.Responses;
using Resend;
using StackExchange.Redis;

[Trait("Category", "Unit")]
public sealed class CuratorServiceCollectionExtensionsTests
{
    private static readonly Type[] RedisBackedSingletons =
    [
        typeof(IConnectionMultiplexer),
        typeof(IDatabase),
        typeof(IPsnRateLimiter),
        typeof(IRawgRateLimiterFactory),
        typeof(IOpenAIRateLimiter),
        typeof(PsnAccessTokenCache),
    ];

    private static readonly Type[] NamedAzureClients =
    [
        typeof(BlobServiceClient),
        typeof(ServiceBusClient),
        typeof(ServiceBusAdministrationClient),
    ];

    public static TheoryData<Type> RedisBackedSingletonTypes() => [.. RedisBackedSingletons];

    public static TheoryData<Type> CuratorJobWorkerTypes() =>
    [
        typeof(LibraryRefreshWorker),
        typeof(LibraryRefreshContinuationWorker),
        typeof(EnrichmentRunWorker),
    ];

    [Theory]
    [MemberData(nameof(CuratorJobWorkerTypes))]
    public void TheCuratorJobWorker_IsActivatedFromTheContainer_SoEveryDependencyItTakesIsRegistered(Type workerType)
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddCuratorServices(NewConfiguration(), NewResponsesClient());
        services.AddSingleton(Mock.Of<IDatabase>());
        services.AddSingleton(Mock.Of<IConnectionMultiplexer>());
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        // Act
        var worker = ActivatorUtilities.CreateInstance(scope.ServiceProvider, workerType);

        // Assert
        Assert.IsType(workerType, worker);
    }

    [Fact]
    public void AddCuratorServices_BuildsAServiceProvider_WithEveryRegistrationResolvableAndNoCaptiveScopedDependency()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddCuratorServices(NewConfiguration(), NewResponsesClient());

        // Act
        var exception = Record.Exception(() => services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }).Dispose());

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void AddCuratorServices_ResolvesEverySingleton_SoAFactoryLambdaCannotHideACaptiveScopedDependency()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddCuratorServices(NewConfiguration(), NewResponsesClient());

        // Act
        var resolution = ResolveEveryReachableSingleton(services);

        // Assert
        Assert.NotEqual(0, resolution.Examined);
        Assert.Empty(resolution.Failures);
    }

    [Fact]
    public void TheSingletonResolutionSweep_ReportsAFactoryLambdaThatCapturesAScopedService_SoItsEmptyResultIsNotVacuous()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<SqlConnection>(_ => new SqlConnection());
        services.AddSingleton<IDisposable>(provider => provider.GetRequiredService<SqlConnection>());

        // Act
        var resolution = ResolveEveryReachableSingleton(services);

        // Assert
        Assert.Equal(1, resolution.Examined);
        Assert.Single(resolution.Failures);
    }

    [Theory]
    [MemberData(nameof(RedisBackedSingletonTypes))]
    public void TheRedisBackedSingleton_IsStillRegistered_SoTheResolutionExclusionCannotQuietlyCoverNothing(
        Type serviceType)
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddCuratorServices(NewConfiguration(), NewResponsesClient());

        // Assert
        Assert.Contains(services, descriptor => descriptor.ServiceType == serviceType);
    }

    [Fact]
    public void TheNamedAzureClients_ResolveThroughTheirFactoryByName_SoTheResolutionExclusionCannotQuietlyCoverNothing()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddCuratorServices(NewConfiguration(), NewResponsesClient());
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        // Act
        var clients = new object[]
        {
            provider.GetRequiredService<IAzureClientFactory<BlobServiceClient>>()
                .CreateClient(AzureClientNames.Crgolden),
            provider.GetRequiredService<IAzureClientFactory<ServiceBusClient>>()
                .CreateClient(AzureClientNames.Crgolden),
            provider.GetRequiredService<IAzureClientFactory<ServiceBusAdministrationClient>>()
                .CreateClient(AzureClientNames.Crgolden),
        };

        // Assert
        Assert.Equal(NamedAzureClients.Length, clients.Length);
        Assert.All(clients, Assert.NotNull);
    }

    [Fact]
    public void AddCuratorServices_RegistersTheRawgAndOpenCriticTypedClientsAsNonSingletons_SoNoSingletonCanCaptureTheirHttpMessageHandler()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddCuratorServices(NewConfiguration(), NewResponsesClient());

        // Assert
        AssertNotRegisteredAsSingleton<IRawgClient>(services);
        AssertNotRegisteredAsSingleton<IOpenCriticClient>(services);
    }

    [Fact]
    public void AddCuratorServices_ResolvesIResendInsideAScope_SoTheTypedClientsScopedOptionsSnapshotCannotThrowOnTheFirstEmail()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddCuratorServices(NewConfiguration(), NewResponsesClient());
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();
        var scopedServices = scope.ServiceProvider;

        // Act
        var exception = Record.Exception(() => scopedServices.GetRequiredService<IResend>());

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public async Task TheResendClient_RetriesARateLimitedSend_SoAnAlertBurstOverResendsPerSecondLimitIsNotAnUnhandledException()
    {
        // Arrange
        var stub = StubHttpMessageHandler.Always(NewRateLimitedResponse);
        var services = new ServiceCollection();
        services.AddCuratorServices(NewConfiguration(), NewResponsesClient());
        services.AddHttpClient<IResend, ResendClient>().ConfigurePrimaryHttpMessageHandler(() => stub);
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();
        var resend = scope.ServiceProvider.GetRequiredService<IResend>();

        // Act
        var error = await Record.ExceptionAsync(() => resend.EmailSendAsync(
            NewEmailMessage(),
            TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<ResendException>(error);
        Assert.Equal(
            CuratorServiceCollectionExtensions.ResendMaxRetryAttempts + 1,
            stub.Requests.Count);
    }

    private static HttpResponseMessage NewRateLimitedResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        return response;
    }

    private static EmailMessage NewEmailMessage()
    {
        var message = new EmailMessage
        {
            From = Generated.NewEmailAddress(),
            Subject = Generated.NewEmailSubject(),
            HtmlBody = Generated.NewHtmlBody(),
        };
        message.To.Add(Generated.NewEmailAddress());
        return message;
    }

    private static (int Examined, IReadOnlyList<string> Failures) ResolveEveryReachableSingleton(
        IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var serviceTypes = services
            .Where(descriptor => descriptor.Lifetime == ServiceLifetime.Singleton && !descriptor.IsKeyedService)
            .Select(descriptor => descriptor.ServiceType)
            .Where(serviceType => !serviceType.ContainsGenericParameters)
            .Where(serviceType => !RedisBackedSingletons.Contains(serviceType))
            .Where(serviceType => !NamedAzureClients.Contains(serviceType))
            .Distinct(EqualityComparer<Type>.Default)
            .ToList();

        var failures = new List<string>();
        foreach (var serviceType in serviceTypes)
        {
            try
            {
                provider.GetRequiredService(serviceType);
            }
            catch (Exception error)
            {
                failures.Add($"{serviceType.FullName}: {error.GetType().Name}: {error.Message}");
            }
        }

        return (serviceTypes.Count, failures);
    }

    private static void AssertNotRegisteredAsSingleton<TService>(IServiceCollection services)
    {
        var descriptor = services.Single(candidate => candidate.ServiceType == typeof(TService));
        Assert.NotEqual(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    private static ResponsesClient NewResponsesClient() => new(
        new ApiKeyCredential(Generated.NewOpenAIApiKey()),
        new ResponsesClientOptions { Endpoint = Generated.NewProviderBaseAddressUnderAPathPrefix() });

    private static IConfiguration NewConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{nameof(SqlConnectionStringBuilder)}:{nameof(SqlConnectionStringBuilder.DataSource)}"] =
                    Generated.NewHostLabel(),
                [CuratorConfigurationKeys.CuratorDatabaseConnection] = Generated.NewPostgresConnectionString(),
                [CuratorConfigurationKeys.StorageUri] = Generated.NewProviderBaseAddress().ToString(),
                [CuratorConfigurationKeys.ServiceBusFullyQualifiedNamespace] = Generated.NewHostLabel(),
                [CuratorConfigurationKeys.RedisHost] = Generated.NewHostLabel(),
                [CuratorConfigurationKeys.RedisPort] = Generated.NewPortNumber().ToString(CultureInfo.InvariantCulture),
                [CuratorConfigurationKeys.RedisSsl] = true.ToString(CultureInfo.InvariantCulture),
                [CuratorConfigurationKeys.RedisPassword] = Generated.NewRedisPassword(),
                [CuratorConfigurationKeys.CuratorTokenKey] = Generated.NewWebSafeBase64Key(TokenCrypto.KeySizeBytes),
                [CuratorConfigurationKeys.RawgEndpoint] =
                    Generated.NewProviderBaseAddressUnderAPathPrefix().ToString(),
                [CuratorConfigurationKeys.OpenCriticEndpoint] = Generated.NewProviderBaseAddress().ToString(),
                [CuratorConfigurationKeys.ResendApiToken] = Generated.NewResendApiToken(),
                [$"{nameof(TelemetryOptions)}:{nameof(TelemetryOptions.ExceptionsDescription)}"] = Generated.NewDescription(),
                [$"{nameof(TelemetryOptions)}:{nameof(TelemetryOptions.GeocoderFallbacksDescription)}"] = Generated.NewDescription(),
                [$"{nameof(TelemetryOptions)}:{nameof(TelemetryOptions.ZipBackfillDescription)}"] = Generated.NewDescription(),
                [$"{nameof(TelemetryOptions)}:{nameof(TelemetryOptions.BulkImportRowsDescription)}"] = Generated.NewDescription(),
                [$"{nameof(TelemetryOptions)}:{nameof(TelemetryOptions.ReGeocodedChurchesDescription)}"] = Generated.NewDescription(),
                [$"{nameof(TelemetryOptions)}:{nameof(TelemetryOptions.ReGeocodedCampusesDescription)}"] = Generated.NewDescription(),
                [$"{nameof(TelemetryOptions)}:{nameof(TelemetryOptions.EnrichmentGateUnavailableDescription)}"] = Generated.NewDescription(),
                [$"{nameof(TelemetryOptions)}:{nameof(TelemetryOptions.EnrichmentGamesDescription)}"] = Generated.NewDescription(),
                [$"{nameof(TelemetryOptions)}:{nameof(TelemetryOptions.ProviderDisabledDescription)}"] = Generated.NewDescription(),
                [$"{nameof(TelemetryOptions)}:{nameof(TelemetryOptions.StaleRedeliveriesDescription)}"] = Generated.NewDescription(),
                [$"{nameof(TelemetryOptions)}:{nameof(TelemetryOptions.TransientRetriesDescription)}"] = Generated.NewDescription(),
                [$"{nameof(TelemetryOptions)}:{nameof(TelemetryOptions.ReapedLeasesDescription)}"] = Generated.NewDescription(),
                [$"{nameof(TelemetryOptions)}:{nameof(TelemetryOptions.OpenCriticSweepGamesDescription)}"] = Generated.NewDescription(),
                [$"{nameof(TelemetryOptions)}:{nameof(TelemetryOptions.PsnSessionRotationsDescription)}"] = Generated.NewDescription(),
                [$"{nameof(TelemetryOptions)}:{nameof(TelemetryOptions.StoreProductsDescription)}"] = Generated.NewDescription(),
                [$"{nameof(TelemetryOptions)}:{nameof(TelemetryOptions.QueueActiveDescription)}"] = Generated.NewDescription(),
                [$"{nameof(TelemetryOptions)}:{nameof(TelemetryOptions.QueueDeadLetterDescription)}"] = Generated.NewDescription(),
            })
            .Build();
}
