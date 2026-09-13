namespace Functions.Tests.Unit;

using System.ClientModel;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Curator;
using Curator.OpenCritic;
using Curator.Psn;
using Curator.Rawg;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Azure.Storage.Blobs;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI.Responses;
using Resend;
using StackExchange.Redis;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class CuratorServiceCollectionExtensionsTests
{
    private const string CaptiveScopedDependencyFragment = "scoped service";

    private static readonly Type[] RedisBackedSingletons =
    [
        typeof(IConnectionMultiplexer),
        typeof(IDatabase),
        typeof(IPsnRateLimiter),
        typeof(IRawgRateLimiterFactory),
        typeof(PsnAccessTokenCache),
    ];

    private static readonly Type[] NamedAzureClients =
    [
        typeof(BlobServiceClient),
        typeof(ServiceBusClient),
        typeof(ServiceBusAdministrationClient),
    ];

    public static TheoryData<Type> RedisBackedSingletonTypes() => [.. RedisBackedSingletons];

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
        Assert.Empty(resolution.CapturedScopedDependencies);
        Assert.Empty(resolution.UnexpectedFailures);
    }

    [Fact]
    public void TheCaptiveDependencyDetector_StillMatchesWhatTheRuntimeSays_SoAMessageChangeCannotSilenceIt()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<SqlConnection>(_ => new SqlConnection());
        services.AddSingleton<CaptorOfAScopedService>();
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });

        // Act
        var error = Record.Exception(() => provider.GetRequiredService<CaptorOfAScopedService>());

        // Assert
        Assert.Contains(
            CaptiveScopedDependencyFragment,
            Assert.IsType<InvalidOperationException>(error).Message,
            StringComparison.Ordinal);
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

        // Act
        var exception = Record.Exception(() => scope.ServiceProvider.GetRequiredService<IResend>());

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
            From = TestValues.NewEmailAddress(),
            Subject = TestValues.NewEmailSubject(),
            HtmlBody = TestValues.NewHtmlBody(),
        };
        message.To.Add(TestValues.NewEmailAddress());
        return message;
    }

    private static (int Examined, IReadOnlyList<string> CapturedScopedDependencies, IReadOnlyList<string> UnexpectedFailures)
        ResolveEveryReachableSingleton(IServiceCollection services)
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

        var captured = new List<string>();
        var unexpected = new List<string>();
        foreach (var serviceType in serviceTypes)
        {
            try
            {
                provider.GetRequiredService(serviceType);
            }
            catch (InvalidOperationException error)
                when (error.Message.Contains(CaptiveScopedDependencyFragment, StringComparison.Ordinal))
            {
                captured.Add($"{serviceType.FullName}: {error.Message}");
            }
            catch (Exception error)
            {
                unexpected.Add($"{serviceType.FullName}: {error.GetType().Name}: {error.Message}");
            }
        }

        return (serviceTypes.Count, captured, unexpected);
    }

    private static void AssertNotRegisteredAsSingleton<TService>(IServiceCollection services)
    {
        var descriptor = services.Single(candidate => candidate.ServiceType == typeof(TService));
        Assert.NotEqual(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    private static ResponsesClient NewResponsesClient() => new(
        new ApiKeyCredential(Guid.NewGuid().ToString()),
        new ResponsesClientOptions { Endpoint = new Uri($"https://{NewHostLabel()}/openai/v1/") });

    private static string NewHostLabel() => TestValues.NewHostLabel();

    private static int NewPortNumber() => TestValues.NewPortNumber();

    private static string NewTokenCryptoKey()
    {
        var raw = new byte[TokenCrypto.KeySizeBytes];
        RandomNumberGenerator.Fill(raw);
        return Convert.ToBase64String(raw).Replace('+', '-').Replace('/', '_');
    }

    private static IConfiguration NewConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{nameof(SqlConnectionStringBuilder)}:DataSource"] = NewHostLabel(),
                [CuratorConfigurationKeys.CuratorDatabaseConnection] =
                    $"Host={NewHostLabel()};Database=db{Guid.NewGuid():N};Username=u;Password=p",
                [CuratorConfigurationKeys.StorageUri] = $"https://{NewHostLabel()}/storage/",
                [CuratorConfigurationKeys.ServiceBusFullyQualifiedNamespace] = NewHostLabel(),
                [CuratorConfigurationKeys.RedisHost] = NewHostLabel(),
                [CuratorConfigurationKeys.RedisPort] = NewPortNumber().ToString(CultureInfo.InvariantCulture),
                [CuratorConfigurationKeys.RedisSsl] = true.ToString(CultureInfo.InvariantCulture),
                [CuratorConfigurationKeys.RedisPassword] = Guid.NewGuid().ToString(),
                [CuratorConfigurationKeys.CuratorTokenKey] = NewTokenCryptoKey(),
                [CuratorConfigurationKeys.RawgEndpoint] = $"https://{NewHostLabel()}/api/",
                [CuratorConfigurationKeys.OpenCriticEndpoint] = $"https://{NewHostLabel()}/",
                [CuratorConfigurationKeys.ResendApiToken] = Guid.NewGuid().ToString(),
            })
            .Build();

    private sealed class CaptorOfAScopedService
    {
        public CaptorOfAScopedService(SqlConnection captured) => Captured = captured;

        public SqlConnection Captured { get; }
    }
}
