namespace Functions.Tests.Unit;

using System.Data.Common;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using Curator.Library;
using Curator.Psn;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class IngestionServiceTests
{
    private static readonly Guid IdentitySub = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
    private static readonly Guid PullId = Guid.Parse("2b1c4a3e-0000-4000-8000-00000000cafe");

    private static readonly JsonSerializerOptions PsnWireFormat =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public async Task IngestAsync_ReturnsTheNewPullIdAndTheSnapshotsItJustRecorded()
    {
        // Arrange
        var body = Body(
            2,
            new PsnEntitlementPayload { Id = "ent-1", TitleMeta = new PsnTitleMeta { Name = "First" } },
            new PsnEntitlementPayload { Id = "ent-2", TitleMeta = new PsnTitleMeta { Name = "Second" } });
        var dataSource = SeededDataSource(snapshotCount: 2);
        var (service, session) = await ServiceAsync(dataSource, body);

        // Act
        var (pullId, snapshots) = await service.IngestAsync(
            IdentitySub.ToString(),
            session,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(PullId.ToString(), pullId);
        Assert.Equal(["ent-1", "ent-2"], snapshots.Select(snapshot => snapshot.EntitlementId));
    }

    [Fact]
    public async Task IngestAsync_MapsEveryEntitlementFieldCanonicalizationNeedsOntoItsSnapshot()
    {
        // Arrange
        var entitlementId = NewEntitlementId();
        var productId = NewOpaqueId();
        var skuId = NewOpaqueId();
        var activeDate = NewActiveDate();
        var platformId = NewPlatformId();
        var titleId = NewTitleId();
        var titleMetaName = NewGameName();
        var titleImageUrl = NewImageUrl();
        var gameMetaName = NewGameName();
        var packageType = NewPackageType();
        var gameIconUrl = NewImageUrl();
        var conceptId = NewConceptId();
        var conceptMetaName = NewGameName();
        var conceptIconUrl = NewImageUrl();
        var body = Body(
            1,
            new PsnEntitlementPayload
            {
                Id = entitlementId,
                ProductId = productId,
                SkuId = skuId,
                ActiveFlag = true,
                ActiveDate = activeDate,
                IsGame = true,
                EntitlementAttributes = [new PsnEntitlementAttribute { PlatformId = platformId }],
                TitleMeta = new PsnTitleMeta
                {
                    TitleId = titleId,
                    Name = titleMetaName,
                    ImageUrl = titleImageUrl,
                },
                GameMeta = new PsnGameMeta
                {
                    Name = gameMetaName,
                    PackageType = packageType,
                    IconUrl = gameIconUrl,
                },
                ConceptMeta = new PsnConceptMeta
                {
                    ConceptId = conceptId,
                    Name = conceptMetaName,
                    IconUrl = conceptIconUrl,
                },
            });
        var dataSource = SeededDataSource(snapshotCount: 1);
        var (service, session) = await ServiceAsync(dataSource, body);

        // Act
        var (_, snapshots) = await service.IngestAsync(
            IdentitySub.ToString(),
            session,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var snapshot = Assert.Single(snapshots);
        Assert.Equal(entitlementId, snapshot.EntitlementId);
        Assert.Equal(conceptId, snapshot.ConceptId);
        Assert.Equal(productId, snapshot.ProductId);
        Assert.Equal(titleId, snapshot.TitleId);
        Assert.Equal(gameMetaName, snapshot.GameMetaName);
        Assert.Equal(conceptMetaName, snapshot.ConceptMetaName);
        Assert.Equal(titleMetaName, snapshot.TitleMetaName);
        Assert.Equal(packageType, snapshot.PackageType);
        Assert.Equal(skuId, snapshot.SkuId);
        Assert.Equal(activeDate, snapshot.ActiveDate);
        Assert.Equal(titleImageUrl, snapshot.TitleImageUrl);
        Assert.Equal(gameIconUrl, snapshot.GameIconUrl);
        Assert.Equal(conceptIconUrl, snapshot.ConceptIconUrl);
        Assert.Equal([platformId], snapshot.PlatformIds);
        Assert.True(snapshot.Active);
        Assert.True(snapshot.IsGame);
    }

    [Fact]
    public async Task IngestAsync_CarriesPsnsVerbatimEntryThroughToThePersistedRaw()
    {
        // Arrange
        var dataSource = SeededDataSource(snapshotCount: 1);
        var (service, session) = await ServiceAsync(
            dataSource,
            """{"totalResults": 1, "entitlements": [{"id": "ent-1", "neverMapped": 42}]}""");

        // Act
        await service.IngestAsync(IdentitySub.ToString(), session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var batch = Assert.IsType<string>(ParamValue(dataSource.ExecutedCommands[1], "@batch"));
        var raw = JsonDocument.Parse(batch).RootElement[0].GetProperty("raw");
        Assert.Equal(42, raw.GetProperty("neverMapped").GetInt32());
    }

    [Fact]
    public async Task IngestAsync_RecordsThePullAsCuratorLive()
    {
        // Arrange
        var dataSource = SeededDataSource(snapshotCount: 1);
        var (service, session) = await ServiceAsync(
            dataSource,
            Body(1, new PsnEntitlementPayload { Id = "ent-1" }));

        // Act
        await service.IngestAsync(IdentitySub.ToString(), session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IngestionService.LiveSource, ParamValue(dataSource.ExecutedCommands[0], "@source"));
    }

    [Fact]
    public async Task IngestAsync_RecordsAnEmptyPull_WhenPsnReportsNoEntitlements()
    {
        // Arrange
        var dataSource = SeededDataSource(snapshotCount: 0);
        var (service, session) = await ServiceAsync(dataSource, Body(0));

        // Act
        var (pullId, snapshots) = await service.IngestAsync(
            IdentitySub.ToString(),
            session,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(PullId.ToString(), pullId);
        Assert.Empty(snapshots);
        Assert.Equal(0, ParamValue(Assert.Single(dataSource.ExecutedCommands), "@entry_count"));
    }

    [Fact]
    public async Task IngestAsync_SkipsEntitlementsWithNoId_BecauseTheyCannotBeTrackedAcrossRefreshes()
    {
        // Arrange
        var body = Body(
            3,
            new PsnEntitlementPayload { Id = "ent-1", TitleMeta = new PsnTitleMeta { Name = "First" } },
            new PsnEntitlementPayload { TitleMeta = new PsnTitleMeta { Name = "No Id" } },
            new PsnEntitlementPayload { Id = "ent-2", TitleMeta = new PsnTitleMeta { Name = "Second" } });
        var dataSource = SeededDataSource(snapshotCount: 2);
        var (service, session) = await ServiceAsync(dataSource, body);

        // Act
        var (_, snapshots) = await service.IngestAsync(
            IdentitySub.ToString(),
            session,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["ent-1", "ent-2"], snapshots.Select(snapshot => snapshot.EntitlementId));
    }

    [Fact]
    public async Task IngestAsync_CountsSkippedEntitlementsInThePull_SoEntryCountReportsWhatPsnReturned()
    {
        // Arrange
        var body = Body(
            3,
            new PsnEntitlementPayload { Id = "ent-1" },
            new PsnEntitlementPayload { TitleMeta = new PsnTitleMeta { Name = "No Id" } },
            new PsnEntitlementPayload { Id = "ent-2" });
        var dataSource = SeededDataSource(snapshotCount: 2);
        var (service, session) = await ServiceAsync(dataSource, body);

        // Act
        await service.IngestAsync(
            IdentitySub.ToString(),
            session,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3, ParamValue(dataSource.ExecutedCommands[0], "@entry_count"));
    }

    [Fact]
    public async Task IngestAsync_PassesTheRequestedLimitThroughToPsn()
    {
        // Arrange
        var dataSource = SeededDataSource(snapshotCount: 1);
        var handler = StubHttpMessageHandler.Returns(
            Json(Body(50, new PsnEntitlementPayload { Id = "ent-1" })));
        var session = await ReadySessionAsync(handler);
        var service = new IngestionService(
            new PsnLibraryClient(),
            new EntitlementPullRepository(dataSource));

        // Act
        await service.IngestAsync(IdentitySub.ToString(), session, 1, TestContext.Current.CancellationToken);

        // Assert
        var request = Assert.Single(handler.Requests);
        Assert.Contains("limit=1", request.RequestUri?.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IngestAsync_SurfacesPsnAuthException_WhenPsnRejectsTheAccessToken()
    {
        // Arrange
        var dataSource = new FakeDbDataSource();
        var handler = StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var session = await ReadySessionAsync(handler);
        var service = new IngestionService(
            new PsnLibraryClient(),
            new EntitlementPullRepository(dataSource));

        // Act
        var exception = await Record.ExceptionAsync(() => service.IngestAsync(
            IdentitySub.ToString(),
            session,
            cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<PsnAuthException>(exception);
        Assert.Equal(0, dataSource.ConnectionsCreated);
    }

    private static async Task<(IngestionService Service, PsnSession Session)> ServiceAsync(
        FakeDbDataSource dataSource,
        string body)
    {
        var session = await ReadySessionAsync(StubHttpMessageHandler.Returns(Json(body)));
        return (new IngestionService(new PsnLibraryClient(), new EntitlementPullRepository(dataSource)), session);
    }

    private static FakeDbDataSource SeededDataSource(int snapshotCount)
    {
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(FakeDbCommand.WithScalarResult(PullId));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(snapshotCount));

        return dataSource;
    }

    private static async Task<PsnSession> ReadySessionAsync(StubHttpMessageHandler handler)
    {
        var store = new InMemoryPsnTokenStore();
        await store.SaveAsync(
            new PsnTokenResponse
            {
                AccessToken = "cached-access",
                ExpiresIn = 3600,
                AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            },
            TestContext.Current.CancellationToken);
        return await PsnSession.RestoreAsync(
            null,
            store,
            rateLimiter: NullPsnRateLimiter.Unthrottled,
            httpClient: new HttpClient(handler),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static string Body(int totalResults, params PsnEntitlementPayload[] entitlements) =>
        JsonSerializer.Serialize(
            new PsnEntitlementsResponse
            {
                TotalResults = totalResults,
                Entitlements = [.. entitlements.Select(AsElement)],
            },
            PsnWireFormat);

    private static JsonElement AsElement(PsnEntitlementPayload entitlement) =>
        JsonSerializer.SerializeToElement(entitlement, PsnWireFormat);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static object? ParamValue(DbCommand command, string name) =>
        command.Parameters[command.Parameters.IndexOf(name)].Value;

    private static string NewEntitlementId() => $"ent-{Guid.NewGuid():N}";

    private static string NewOpaqueId() => Guid.NewGuid().ToString("N");

    private static string NewConceptId() => $"{Random.Shared.Next(10_000_000, 99_999_999)}";

    private static string NewTitleId() =>
        $"{TrophyMatchService.Ps4TitleIdPrefix}{Random.Shared.Next(10_000, 99_999)}_00";

    private static string NewPlatformId() => $"ps{Random.Shared.Next(3, 6)}";

    private static string NewPackageType() => $"PKG{Random.Shared.Next(100, 999)}";

    private static string NewGameName() => $"Game {Guid.NewGuid():N}";

    private static string NewImageUrl() => $"https://example.com/{Guid.NewGuid():N}.png";

    private static DateTimeOffset NewActiveDate() =>
        new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(Random.Shared.Next(1, 1_000_000));
}
