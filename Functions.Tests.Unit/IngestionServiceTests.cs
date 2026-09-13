namespace Functions.Tests.Unit;

using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text;
using Curator.Library;
using Curator.Psn;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class IngestionServiceTests
{
    private const int OneEntitlement = 1;
    private const int NoEntitlements = 0;

    private static readonly Guid IdentitySub = Guid.NewGuid();
    private static readonly Guid PullId = Guid.NewGuid();

    private static readonly JsonSerializerOptions PsnWireFormat =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public async Task IngestAsync_ReturnsTheNewPullIdAndTheSnapshotsItJustRecorded()
    {
        // Arrange
        var firstEntitlementId = TestValues.NewEntitlementId();
        var secondEntitlementId = TestValues.NewEntitlementId();
        var entitlements = new[]
        {
            new PsnEntitlementPayload
            {
                Id = firstEntitlementId,
                TitleMeta = new PsnTitleMeta { Name = TestValues.NewGameName() },
            },
            new PsnEntitlementPayload
            {
                Id = secondEntitlementId,
                TitleMeta = new PsnTitleMeta { Name = TestValues.NewGameName() },
            },
        };
        var body = Body(entitlements.Length, entitlements);
        var dataSource = SeededDataSource(snapshotCount: entitlements.Length);
        var (service, session) = await ServiceAsync(dataSource, body);

        // Act
        var (pullId, snapshots) = await service.IngestAsync(
            IdentitySub.ToString(),
            session,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(PullId.ToString(), pullId);
        Assert.Equal(
            [firstEntitlementId, secondEntitlementId],
            snapshots.Select(snapshot => snapshot.EntitlementId));
    }

    [Fact]
    public async Task IngestAsync_MapsEveryEntitlementFieldCanonicalizationNeedsOntoItsSnapshot()
    {
        // Arrange
        var entitlementId = TestValues.NewEntitlementId();
        var productId = TestValues.NewProductId();
        var skuId = TestValues.NewSkuId();
        var activeDate = TestValues.NewUtcTimestamp();
        var platformId = TestValues.NewPlatformId();
        var titleId = TestValues.NewTitleId();
        var titleMetaName = TestValues.NewGameName();
        var titleImageUrl = TestValues.NewCoverImageUri();
        var gameMetaName = TestValues.NewGameName();
        var packageType = TestValues.NewPackageType();
        var gameIconUrl = TestValues.NewCoverImageUri();
        var conceptId = TestValues.NewConceptId();
        var conceptMetaName = TestValues.NewGameName();
        var conceptIconUrl = TestValues.NewCoverImageUri();
        var body = Body(
            OneEntitlement,
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
                    ImageUrl = titleImageUrl.OriginalString,
                },
                GameMeta = new PsnGameMeta
                {
                    Name = gameMetaName,
                    PackageType = packageType,
                    IconUrl = gameIconUrl.OriginalString,
                },
                ConceptMeta = new PsnConceptMeta
                {
                    ConceptId = conceptId,
                    Name = conceptMetaName,
                    IconUrl = conceptIconUrl.OriginalString,
                },
            });
        var dataSource = SeededDataSource(snapshotCount: OneEntitlement);
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
        var neverMappedName = TestValues.NewJsonPropertyName();
        var neverMappedValue = Random.Shared.Next(1, 1_000);
        var dataSource = SeededDataSource(snapshotCount: OneEntitlement);
        var (service, session) = await ServiceAsync(
            dataSource,
            BodyCarrying(new JsonObject
            {
                [PsnEntitlementPayload.IdPropertyName] = TestValues.NewEntitlementId(),
                [neverMappedName] = neverMappedValue,
            }));

        // Act
        await service.IngestAsync(IdentitySub.ToString(), session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var batch = Assert.IsType<string>(
            ParamValue(dataSource.ExecutedCommands[1], EntitlementPullRepository.BatchParameter));
        using var persisted = JsonDocument.Parse(batch);
        var raw = persisted.RootElement[0].GetProperty(EntitlementSnapshotColumns.Raw);
        Assert.Equal(neverMappedValue, raw.GetProperty(neverMappedName).GetInt32());
    }

    [Fact]
    public async Task IngestAsync_RecordsThePullAsCuratorLive()
    {
        // Arrange
        var dataSource = SeededDataSource(snapshotCount: OneEntitlement);
        var (service, session) = await ServiceAsync(
            dataSource,
            Body(OneEntitlement, new PsnEntitlementPayload { Id = TestValues.NewEntitlementId() }));

        // Act
        await service.IngestAsync(IdentitySub.ToString(), session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(
            IngestionService.LiveSource,
            ParamValue(dataSource.ExecutedCommands[0], EntitlementPullRepository.SourceParameter));
    }

    [Fact]
    public async Task IngestAsync_RecordsAnEmptyPull_WhenPsnReportsNoEntitlements()
    {
        // Arrange
        var dataSource = SeededDataSource(snapshotCount: NoEntitlements);
        var (service, session) = await ServiceAsync(dataSource, Body(NoEntitlements));

        // Act
        var (pullId, snapshots) = await service.IngestAsync(
            IdentitySub.ToString(),
            session,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(PullId.ToString(), pullId);
        Assert.Empty(snapshots);
        Assert.Equal(
            NoEntitlements,
            ParamValue(Assert.Single(dataSource.ExecutedCommands), EntitlementPullRepository.EntryCountParameter));
    }

    [Fact]
    public async Task IngestAsync_SkipsEntitlementsWithNoId_BecauseTheyCannotBeTrackedAcrossRefreshes()
    {
        // Arrange
        var firstEntitlementId = TestValues.NewEntitlementId();
        var secondEntitlementId = TestValues.NewEntitlementId();
        var identifiedEntitlementIds = new[] { firstEntitlementId, secondEntitlementId };
        var entitlements = new[]
        {
            new PsnEntitlementPayload
            {
                Id = firstEntitlementId,
                TitleMeta = new PsnTitleMeta { Name = TestValues.NewGameName() },
            },
            new PsnEntitlementPayload { TitleMeta = new PsnTitleMeta { Name = TestValues.NewGameName() } },
            new PsnEntitlementPayload
            {
                Id = secondEntitlementId,
                TitleMeta = new PsnTitleMeta { Name = TestValues.NewGameName() },
            },
        };
        var body = Body(entitlements.Length, entitlements);
        var dataSource = SeededDataSource(snapshotCount: identifiedEntitlementIds.Length);
        var (service, session) = await ServiceAsync(dataSource, body);

        // Act
        var (_, snapshots) = await service.IngestAsync(
            IdentitySub.ToString(),
            session,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(identifiedEntitlementIds, snapshots.Select(snapshot => snapshot.EntitlementId));
    }

    [Fact]
    public async Task IngestAsync_CountsSkippedEntitlementsInThePull_SoEntryCountReportsWhatPsnReturned()
    {
        // Arrange
        var entitlements = new[]
        {
            new PsnEntitlementPayload { Id = TestValues.NewEntitlementId() },
            new PsnEntitlementPayload { TitleMeta = new PsnTitleMeta { Name = TestValues.NewGameName() } },
            new PsnEntitlementPayload { Id = TestValues.NewEntitlementId() },
        };
        var identifiedEntitlementCount = entitlements.Count(entitlement => entitlement.Id is not null);
        var body = Body(entitlements.Length, entitlements);
        var dataSource = SeededDataSource(snapshotCount: identifiedEntitlementCount);
        var (service, session) = await ServiceAsync(dataSource, body);

        // Act
        await service.IngestAsync(
            IdentitySub.ToString(),
            session,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(
            entitlements.Length,
            ParamValue(dataSource.ExecutedCommands[0], EntitlementPullRepository.EntryCountParameter));
    }

    [Fact]
    public async Task IngestAsync_PassesTheRequestedLimitThroughToPsn()
    {
        // Arrange
        var requestedLimit = Random.Shared.Next(1, PsnLibraryClient.PageSize);
        var libraryLargerThanTheLimit = requestedLimit + Random.Shared.Next(1, 1_000);
        var dataSource = SeededDataSource(snapshotCount: requestedLimit);
        PsnEntitlementPayload[] firstPage = [.. Enumerable
            .Range(0, requestedLimit)
            .Select(_ => new PsnEntitlementPayload { Id = TestValues.NewEntitlementId() })];
        var handler = StubHttpMessageHandler.Returns(Json(Body(libraryLargerThanTheLimit, firstPage)));
        var session = await ReadySessionAsync(handler);
        var service = new IngestionService(
            new PsnLibraryClient(),
            new EntitlementPullRepository(dataSource));

        // Act
        await service.IngestAsync(
            IdentitySub.ToString(), session, requestedLimit, TestContext.Current.CancellationToken);

        // Assert
        var request = Assert.Single(handler.Requests);
        Assert.Contains(
            $"{PsnLibraryClient.LimitQueryKey}={requestedLimit.ToString(CultureInfo.InvariantCulture)}",
            request.RequestUri?.Query,
            StringComparison.Ordinal);
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
                AccessToken = TestValues.NewAccessToken(),
                ExpiresIn = Random.Shared.Next(600, 90_000),
                AccessTokenExpiresAt = DateTimeOffset.UtcNow
                    .AddSeconds(Random.Shared.Next(600, 90_000))
                    .ToUnixTimeSeconds(),
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

    private static string BodyCarrying(JsonObject entitlement)
    {
        var entitlements = new JsonArray(entitlement);
        return new JsonObject
        {
            [PsnEntitlementsResponse.TotalResultsPropertyName] = entitlements.Count,
            [PsnEntitlementsResponse.EntitlementsPropertyName] = entitlements,
        }.ToJsonString();
    }

    private static JsonElement AsElement(PsnEntitlementPayload entitlement) =>
        JsonSerializer.SerializeToElement(entitlement, PsnWireFormat);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json),
        };

    private static object? ParamValue(DbCommand command, string name) =>
        command.Parameters[command.Parameters.IndexOf(name)].Value;
}
