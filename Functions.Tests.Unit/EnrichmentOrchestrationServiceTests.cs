namespace Functions.Tests.Unit;

using System.Data;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using Curator.Enrichment;
using Curator.OpenCritic;
using Curator.Psn;
using Curator.Rawg;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class EnrichmentOrchestrationServiceTests
{
    private static readonly PublisherTierRuleSet NoTierRules = PublisherTierRuleSet.Prepare([]);

    private static readonly JsonSerializerOptions RawgWireFormat =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public async Task EnrichGameAsync_WhenPsnAndRawgBothProvidePublisher_PsnPublisherWins()
    {
        // Arrange
        var rawgPublisherName = NewPublisherName();
        var psnPublisherName = NewPublisherName();
        var gameTitle = NewGameTitle();
        var titleId = NewTitleId();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow(RawgDetail(new RawgGameDetail { Publishers = Named(rawgPublisherName) })));
        dataSource.Enqueue(PsnCatalogCacheRow(publisher: psnPublisherName));
        dataSource.Enqueue(EmptyReader());
        var (service, credentials) = NewService(
            dataSource,
            rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())),
            catalogClient: new FakeCatalogClient(NotCalled()));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, titleId, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(psnPublisherName, result.Publisher);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenPsnPublisherIsAnEmptyString_FallsBackToRawgRatherThanStayingBlank()
    {
        // Arrange
        var rawgPublisherName = NewPublisherName();
        var gameTitle = NewGameTitle();
        var titleId = NewTitleId();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow(RawgDetail(new RawgGameDetail { Publishers = Named(rawgPublisherName) })));
        dataSource.Enqueue(PsnCatalogCacheRow(publisher: string.Empty));
        dataSource.Enqueue(EmptyReader());
        var (service, credentials) = NewService(
            dataSource,
            rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())),
            catalogClient: new FakeCatalogClient(NotCalled()));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, titleId, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(rawgPublisherName, result.Publisher);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenPsnEsrbAuthorityMatchesButContentRatingIsAnEmptyString_FallsBackToRawg()
    {
        // Arrange
        var rawgEsrbRating = NewEsrbRatingLabel();
        var gameTitle = NewGameTitle();
        var titleId = NewTitleId();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow(RawgDetail(new RawgGameDetail { EsrbRating = new RawgNamed { Name = rawgEsrbRating } })));
        dataSource.Enqueue(PsnCatalogCacheRow(contentRating: string.Empty, ratingAuthority: EnrichmentOrchestrationService.EsrbAuthority));
        dataSource.Enqueue(EmptyReader());
        var (service, credentials) = NewService(
            dataSource,
            rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())),
            catalogClient: new FakeCatalogClient(NotCalled()));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, titleId, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(rawgEsrbRating, result.Esrb);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenPsnRatingAuthorityIsEsrb_PsnContentRatingWinsOverRawg()
    {
        // Arrange
        var rawgEsrbRating = NewEsrbRatingLabel();
        var psnContentRating = NewEsrbRatingLabel();
        var gameTitle = NewGameTitle();
        var titleId = NewTitleId();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow(RawgDetail(new RawgGameDetail { EsrbRating = new RawgNamed { Name = rawgEsrbRating } })));
        dataSource.Enqueue(PsnCatalogCacheRow(contentRating: psnContentRating, ratingAuthority: EnrichmentOrchestrationService.EsrbAuthority));
        dataSource.Enqueue(EmptyReader());
        var (service, credentials) = NewService(
            dataSource,
            rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())),
            catalogClient: new FakeCatalogClient(NotCalled()));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, titleId, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(psnContentRating, result.Esrb);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenPsnRatingAuthorityIsNotEsrb_EsrbFallsBackToRawg()
    {
        // Arrange
        var rawgEsrbRating = NewEsrbRatingLabel();
        var psnContentRating = NewEsrbRatingLabel();
        var nonEsrbAuthority = NewAuthorityName();
        var gameTitle = NewGameTitle();
        var titleId = NewTitleId();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow(RawgDetail(new RawgGameDetail { EsrbRating = new RawgNamed { Name = rawgEsrbRating } })));
        dataSource.Enqueue(PsnCatalogCacheRow(contentRating: psnContentRating, ratingAuthority: nonEsrbAuthority));
        dataSource.Enqueue(EmptyReader());
        var (service, credentials) = NewService(
            dataSource,
            rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())),
            catalogClient: new FakeCatalogClient(NotCalled()));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, titleId, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(rawgEsrbRating, result.Esrb);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenPsnStatesMultiplayerIsFalse_ItWinsOverARawgMultiplayerTag()
    {
        // Arrange
        var multiplayerKeywordTag = "Multiplayer";
        var gameTitle = NewGameTitle();
        var titleId = NewTitleId();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow(RawgDetail(new RawgGameDetail { Tags = Named(multiplayerKeywordTag) })));
        dataSource.Enqueue(PsnCatalogCacheRow(multiplayer: false));
        dataSource.Enqueue(EmptyReader());
        var (service, credentials) = NewService(
            dataSource,
            rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())),
            catalogClient: new FakeCatalogClient(NotCalled()));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, titleId, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.Multiplayer);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenPsnGenresArePresent_TheyWinOverRawgGenres()
    {
        // Arrange
        var rawgGenreName = NewGenreName();
        var primaryPsnGenre = NewGenreName();
        var secondaryPsnGenre = NewGenreName();
        var gameTitle = NewGameTitle();
        var titleId = NewTitleId();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow(RawgDetail(new RawgGameDetail { Genres = Named(rawgGenreName) })));
        dataSource.Enqueue(PsnCatalogCacheRow([primaryPsnGenre, secondaryPsnGenre]));
        dataSource.Enqueue(EmptyReader());
        var (service, credentials) = NewService(
            dataSource,
            rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())),
            catalogClient: new FakeCatalogClient(NotCalled()));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, titleId, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(primaryPsnGenre, result.Genre);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenPsnReleaseDateIsPresent_ItsYearWinsOverARawgDate()
    {
        // Arrange
        var rawgReleasedText = NewRawgReleasedText();
        var psnReleaseDate = NewReleaseDate();
        var gameTitle = NewGameTitle();
        var titleId = NewTitleId();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow(RawgDetail(new RawgGameDetail { Released = rawgReleasedText })));
        dataSource.Enqueue(PsnCatalogCacheRow(releaseDate: psnReleaseDate));
        dataSource.Enqueue(EmptyReader());
        var (service, credentials) = NewService(
            dataSource,
            rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())),
            catalogClient: new FakeCatalogClient(NotCalled()));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, titleId, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(psnReleaseDate.Year, result.ReleaseYear);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenAFreshPsnLookupReturnsAFullUtcTimestamp_CachesOnlyTheDatePart()
    {
        // Arrange
        var releaseDate = NewReleaseDate();
        var releaseHour = Random.Shared.Next(1, 24);
        var releaseTimestamp = new DateTimeOffset(releaseDate.Year, releaseDate.Month, releaseDate.Day, releaseHour, 0, 0, TimeSpan.Zero);
        var gameTitle = NewGameTitle();
        var titleId = NewTitleId();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(EmptyReader());
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(EmptyReader());
        var concept = new TitleConcept
        {
            ConceptId = NewConceptId(),
            ReleaseDate = releaseTimestamp,
        };
        var (service, credentials) = NewService(dataSource, catalogClient: new FakeCatalogClient(concept));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, titleId, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(releaseDate.Year, result.ReleaseYear);
        var cacheWrite = dataSource.ExecutedCommands.Single(command =>
            command.CapturedCommandText?.Contains("INSERT INTO psn_catalog_cache", StringComparison.Ordinal) == true);
        Assert.Equal(releaseDate, cacheWrite.Parameters["@release_date"].Value);
    }

    [Theory]
    [InlineData(2018, 4, 20, 4)]
    [InlineData(2013, 11, 15, 5)]
    [InlineData(2013, 11, 15, 0)]
    public async Task EnrichGameAsync_TruncatesThePsnReleaseDateInUtc_NotInTheHostTimeZone(
        int year,
        int month,
        int day,
        int utcHour)
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var titleId = NewTitleId();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(EmptyReader());
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(EmptyReader());
        var concept = new TitleConcept
        {
            ConceptId = NewConceptId(),
            ReleaseDate = new DateTimeOffset(year, month, day, utcHour, 0, 0, TimeSpan.Zero),
        };
        var (service, credentials) = NewService(dataSource, catalogClient: new FakeCatalogClient(concept));

        // Act
        await service.EnrichGameAsync(
            gameTitle, titleId, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        var cacheWrite = dataSource.ExecutedCommands.Single(command =>
            command.CapturedCommandText?.Contains("INSERT INTO psn_catalog_cache", StringComparison.Ordinal) == true);
        Assert.Equal(new DateOnly(year, month, day), cacheWrite.Parameters["@release_date"].Value);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenNeitherSourceCarriesAReleaseDate_LeavesTheReleaseYearUnset()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var (service, credentials) = NewService(new FakeDbDataSource());

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result.ReleaseYear);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenCachedPsnEntryHasNullConceptFetchedAt_ResolvesAFreshLookupInsteadOfSkippingIt()
    {
        // Arrange
        var freshStarRating = NewStarRating();
        var freshPublisherName = NewPublisherName();
        var gameTitle = NewGameTitle();
        var titleId = NewTitleId();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(PsnCatalogCacheRow(includeConceptFetchedAt: false));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(EmptyReader());
        var catalogClient = new FakeCatalogClient(
            new TitleConcept { ConceptId = NewConceptId(), StarRating = freshStarRating, Publisher = freshPublisherName });
        var (service, credentials) = NewService(dataSource, catalogClient: catalogClient);

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, titleId, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, catalogClient.CallCount);
        Assert.Equal(freshStarRating, result.PsnRating);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenCachedPsnEntryHasAConceptFetchedAt_UsesTheCacheWithoutCallingTheCatalogClient()
    {
        // Arrange
        var cachedStarRating = NewStarRating();
        var gameTitle = NewGameTitle();
        var titleId = NewTitleId();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(PsnCatalogCacheRow(starRating: cachedStarRating, conceptFetchedAt: DateTimeOffset.UtcNow));
        dataSource.Enqueue(EmptyReader());
        var catalogClient = new FakeCatalogClient(NotCalled());
        var (service, credentials) = NewService(dataSource, catalogClient: catalogClient);

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, titleId, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, catalogClient.CallCount);
        Assert.Equal(cachedStarRating, result.PsnRating);
    }

    [Fact]
    public async Task EnrichGameAsync_DoesNotReportRawgAsAttempted_WhenTheRequestNeverReachedIt()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(EmptyReader());
        var (service, credentials) = NewService(
            dataSource,
            rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(new HttpRequestException("RAWG is unreachable"))));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.RawgEnriched);
        Assert.False(result.RawgAttempted);
    }

    [Fact]
    public async Task EnrichGameAsync_ReportsRawgAsAttempted_WhenItAnsweredAndGenuinelyHasNoSuchTitle()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow(null));
        var (service, credentials) = NewService(
            dataSource,
            rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.RawgEnriched);
        Assert.True(result.RawgAttempted);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenRawgRejectsTheKey_ThrowsEnrichmentAuthException()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(EmptyReader());
        var handler = StubHttpMessageHandler.Returns(
            Json(HttpStatusCode.Unauthorized, NewProviderErrorBody()));
        var (service, credentials) = NewService(dataSource, rawgClient: NewRawgClient(handler));

        // Act
        var exception = await Record.ExceptionAsync(() => service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken));

        // Assert
        var authException = Assert.IsType<EnrichmentAuthException>(exception);
        Assert.Equal(EnrichmentProvider.Rawg, authException.Provider);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenRawgReturns429_ThrowsEnrichmentRateLimitException()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(EmptyReader());
        var handler = StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var (service, credentials) = NewService(dataSource, rawgClient: NewRawgClient(handler));

        // Act
        var exception = await Record.ExceptionAsync(() => service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken));

        // Assert
        var rateLimitException = Assert.IsType<EnrichmentRateLimitException>(exception);
        Assert.Equal(EnrichmentProvider.Rawg, rateLimitException.Provider);
        Assert.Equal(RateLimitBackoff.DefaultRetrySeconds, rateLimitException.RetryAfterSeconds);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenRawgsRetryAfterHeaderExceeds24Hours_ClampsToTheCap()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(EmptyReader());
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.Add("Retry-After", "200000");
        var (service, credentials) = NewService(dataSource, rawgClient: NewRawgClient(StubHttpMessageHandler.Returns(response)));

        // Act
        var exception = await Record.ExceptionAsync(() => service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken));

        // Assert
        var rateLimitException = Assert.IsType<EnrichmentRateLimitException>(exception);
        Assert.Equal(RateLimitBackoff.MaxRetrySeconds, rateLimitException.RetryAfterSeconds);
    }

    [Fact]
    public async Task EnrichGameAsync_AfterThreeConsecutiveRawgTransportFailures_DisablesRawgAndRecordsItUnavailable()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        EnqueueEmptyCacheReads(dataSource, EnrichmentOrchestrationService.TransportFailureLimit);
        var handler = StubHttpMessageHandler.Throws(new HttpRequestException(NewTransportFailureMessage()));
        var (service, credentials) = NewService(dataSource, rawgClient: NewRawgClient(handler));

        // Act
        await EnrichRepeatedlyAsync(
            service, credentials, gameTitle, EnrichmentOrchestrationService.TransportFailureLimit);

        // Assert
        Assert.Contains(EnrichmentProvider.Rawg, service.TransportUnavailableProviders);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenRawgIsDisabledMidBatch_SkipsRawgWithoutClearingTheStillConfiguredCredential()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(EmptyReader());
        var (service, credentials) = NewService(
            dataSource, rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())));
        service.DisableProvider(EnrichmentProvider.Rawg);

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(credentials.Rawg);
        Assert.False(result.RawgAttempted);
        Assert.False(result.RawgEnriched);
    }

    [Fact]
    public void RateLimitBackoffNext_DoublesThePreviousValue()
    {
        // Act
        var next = RateLimitBackoff.Next(3600.0);

        // Assert
        Assert.Equal(7200.0, next);
    }

    [Fact]
    public void RateLimitBackoffNext_WhenDoublingWouldExceedTheCap_ClampsTo24Hours()
    {
        // Act
        var next = RateLimitBackoff.Next(50000.0);

        // Assert
        Assert.Equal(RateLimitBackoff.MaxRetrySeconds, next);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenTheOpenCriticTopupHitsANetworkFailure_MarksTheTopupIncompleteInsteadOfFailingTheGame()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        var handler = StubHttpMessageHandler.Throws(new HttpRequestException(NewTransportFailureMessage()));
        var (service, credentials) = NewService(dataSource, openCriticClient: NewOpenCriticClient(handler));

        // Act
        var exception = await Record.ExceptionAsync(() => service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken));

        // Assert
        Assert.Null(exception);
        Assert.True(service.OpencriticTopupIncomplete);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenTheOpenCriticTopupHitsANetworkFailure_ReportsTheGameAsNotOpenCriticEnriched()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        var handler = StubHttpMessageHandler.Throws(new HttpRequestException(NewTransportFailureMessage()));
        var (service, credentials) = NewService(dataSource, openCriticClient: NewOpenCriticClient(handler));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.OpencriticEnriched);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenTheOpenCriticTopupHitsANetworkFailure_DoesNotPersistThePartialGamesTheAdminSweepWould()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        var handler = StubHttpMessageHandler.Throws(new HttpRequestException(NewTransportFailureMessage()));
        var (service, credentials) = NewService(dataSource, openCriticClient: NewOpenCriticClient(handler));

        // Act
        await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain(
            dataSource.ExecutedCommands,
            command => command.CapturedCommandText?.Contains("INSERT INTO opencritic_cache", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenTheOpenCriticTopupHitsANetworkFailure_DoesNotAdvanceTheSharedPaginationCursor()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        var handler = StubHttpMessageHandler.Throws(new HttpRequestException(NewTransportFailureMessage()));
        var (service, credentials) = NewService(dataSource, openCriticClient: NewOpenCriticClient(handler));

        // Act
        await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain(
            dataSource.ExecutedCommands,
            command => command.CapturedCommandText?.Contains(
                "INSERT INTO opencritic_pagination_cursor", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenTheOpenCriticTopupHitsANetworkFailure_SkipsTheSecondTopupPlatform()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        var handler = StubHttpMessageHandler.Throws(new HttpRequestException(NewTransportFailureMessage()));
        var (service, credentials) = NewService(dataSource, openCriticClient: NewOpenCriticClient(handler));

        // Act
        await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenTheOpenCriticTopupHitsAServerError_MarksTheTopupIncompleteInsteadOfThrowing()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        var handler = StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var (service, credentials) = NewService(dataSource, openCriticClient: NewOpenCriticClient(handler));

        // Act
        var exception = await Record.ExceptionAsync(() => service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken));

        // Assert
        Assert.Null(exception);
        Assert.True(service.OpencriticTopupIncomplete);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenTheOpenCriticTopupRejectsTheKey_ThrowsEnrichmentAuthException()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        var handler = StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.Forbidden));
        var (service, credentials) = NewService(dataSource, openCriticClient: NewOpenCriticClient(handler));

        // Act
        var exception = await Record.ExceptionAsync(() => service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken));

        // Assert
        var authException = Assert.IsType<EnrichmentAuthException>(exception);
        Assert.Equal(EnrichmentProvider.OpenCritic, authException.Provider);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenTheOpenCriticTopupIsRateLimited_ThrowsEnrichmentRateLimitExceptionWithTheDefaultBackoff()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        var handler = StubHttpMessageHandler.Returns(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var (service, credentials) = NewService(dataSource, openCriticClient: NewOpenCriticClient(handler));

        // Act
        var exception = await Record.ExceptionAsync(() => service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken));

        // Assert
        var rateLimitException = Assert.IsType<EnrichmentRateLimitException>(exception);
        Assert.Equal(EnrichmentProvider.OpenCritic, rateLimitException.Provider);
        Assert.Equal(RateLimitBackoff.DefaultRetrySeconds, rateLimitException.RetryAfterSeconds);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenTheOpenCriticTopupPageIsNotExhausted_MarksTheTopupIncomplete()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        var handler = StubHttpMessageHandler.Always(FullPageWithRequestsNearlyExhausted);
        var (service, credentials) = NewService(dataSource, openCriticClient: NewOpenCriticClient(handler));

        // Act
        await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(service.OpencriticTopupIncomplete);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenTheOpenCriticTopupExhaustsBothPlatforms_LeavesTheTopupMarkedComplete()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        var handler = StubHttpMessageHandler.Always(() => Json(HttpStatusCode.OK, "[]"));
        var (service, credentials) = NewService(dataSource, openCriticClient: NewOpenCriticClient(handler));

        // Act
        await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(service.OpencriticTopupIncomplete);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenTheCacheAlreadyMatchesTheTitle_NeverSpendsAnOpenCriticRequestOnATopup()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var topCriticScore = NewOpenCriticScore();
        var tier = NewOpenCriticTierLabel();
        var percentRecommended = NewPercentRecommended();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow("{}"));
        dataSource.Enqueue(OpenCriticCacheRow(gameTitle, topCriticScore, tier, percentRecommended));
        var handler = StubHttpMessageHandler.Throws(NotCalled());
        var (service, credentials) = NewService(
            dataSource,
            rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())),
            openCriticClient: NewOpenCriticClient(handler));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(handler.Requests);
        Assert.Equal(topCriticScore, result.OcScore);
    }

    [Fact]
    public async Task EnrichGameAsync_AfterATopupHasAlreadyRunOnce_DoesNotAttemptASecondTopupForALaterGame()
    {
        // Arrange
        var firstGameTitle = NewGameTitle();
        var secondGameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        var handler = StubHttpMessageHandler.Always(() => Json(HttpStatusCode.OK, "[]"));
        var (service, credentials) = NewService(dataSource, openCriticClient: NewOpenCriticClient(handler));
        await service.EnrichGameAsync(
            firstGameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);
        var requestsAfterFirstGame = handler.Requests.Count;

        // Act
        await service.EnrichGameAsync(
            secondGameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(requestsAfterFirstGame, handler.Requests.Count);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenNoOpenCriticClientIsConfigured_MatchesTheCacheWithoutAttemptingATopup()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var topCriticScore = NewOpenCriticScore();
        var tier = NewOpenCriticTierLabel();
        var percentRecommended = NewPercentRecommended();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow("{}"));
        dataSource.Enqueue(OpenCriticCacheRow(gameTitle, topCriticScore, tier, percentRecommended));
        var (service, credentials) = NewService(dataSource, rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(topCriticScore, result.OcScore);
        Assert.False(service.OpencriticTopupIncomplete);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenNoRawgClientIsConfigured_ReportsTheGameAsNotRawgEnrichedWithoutReadingTheCache()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        var (service, credentials) = NewService(dataSource);

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.RawgEnriched);
        Assert.DoesNotContain(
            dataSource.ExecutedCommands,
            command => command.CapturedCommandText?.Contains("FROM rawg_cache", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenTheCachedRawgRowHasNoRawPayload_ReportsTheGameAsNotRawgEnriched()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow(null));
        dataSource.Enqueue(EmptyReader());
        var (service, credentials) = NewService(dataSource, rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.RawgEnriched);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenRawgSearchMatchesNothing_CachesTheLookedAndFoundNothingResult()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        var handler = StubHttpMessageHandler.Returns(Json(HttpStatusCode.OK, """{"results":[]}"""));
        var (service, credentials) = NewService(dataSource, rawgClient: NewRawgClient(handler));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.RawgEnriched);
        Assert.Contains(
            dataSource.ExecutedCommands,
            command => command.CapturedCommandText?.Contains("INSERT INTO rawg_cache", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenARawgServerErrorInterruptsAFailureStreak_ResetsTheConsecutiveTransportFailureCount()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var handler = ServerErrorOnAttempt(EnrichmentOrchestrationService.TransportFailureLimit);
        var (service, credentials) = NewService(new FakeDbDataSource(), rawgClient: NewRawgClient(handler));

        // Act
        await EnrichRepeatedlyAsync(
            service, credentials, gameTitle, EnrichmentOrchestrationService.TransportFailureLimit + 2);

        // Assert
        Assert.Empty(service.TransportUnavailableProviders);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenRawgMetacriticIsZero_LeavesTheCriticalScoreUnset()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow(RawgDetail(new RawgGameDetail { Metacritic = 0 })));
        dataSource.Enqueue(EmptyReader());
        var (service, credentials) = NewService(dataSource, rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result.CriticalScore);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenRawgReportsAMetacriticScore_UsesItAsTheCriticalScore()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var metacriticScore = NewCriticalScore();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow(RawgDetail(new RawgGameDetail { Metacritic = metacriticScore })));
        dataSource.Enqueue(EmptyReader());
        var (service, credentials) = NewService(dataSource, rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(metacriticScore, result.CriticalScore);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenOnlyRawgScores_ReportsTheScoreSourceAsRawgOnly()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var metacriticScore = NewCriticalScore();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow(RawgDetail(new RawgGameDetail { Metacritic = metacriticScore })));
        dataSource.Enqueue(EmptyReader());
        var (service, credentials) = NewService(dataSource, rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(EnrichmentOrchestrationService.RawgOnlyScoreSource, result.ScoreSource);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenOnlyOpenCriticScores_ReportsTheScoreSourceAsOcOnly()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var topCriticScore = NewOpenCriticScore();
        var tier = NewOpenCriticTierLabel();
        var percentRecommended = NewPercentRecommended();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow("{}"));
        dataSource.Enqueue(OpenCriticCacheRow(gameTitle, topCriticScore, tier, percentRecommended));
        var (service, credentials) = NewService(dataSource, rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(EnrichmentOrchestrationService.OpenCriticOnlyScoreSource, result.ScoreSource);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenBothProvidersScore_ReportsTheScoreSourceAsRawgPlusOc()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var metacriticScore = NewCriticalScore();
        var topCriticScore = NewOpenCriticScore();
        var tier = NewOpenCriticTierLabel();
        var percentRecommended = NewPercentRecommended();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow(RawgDetail(new RawgGameDetail { Metacritic = metacriticScore })));
        dataSource.Enqueue(OpenCriticCacheRow(gameTitle, topCriticScore, tier, percentRecommended));
        var (service, credentials) = NewService(dataSource, rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(EnrichmentOrchestrationService.RawgAndOpenCriticScoreSource, result.ScoreSource);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenNeitherProviderScores_LeavesTheScoreSourceUnset()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var (service, credentials) = NewService(new FakeDbDataSource());

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result.ScoreSource);
        Assert.Null(result.CriticalScore);
        Assert.Null(result.OcScore);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenAnOpenCriticMatchIsFound_CarriesItsTierAndPercentRecommended()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var topCriticScore = NewOpenCriticScore();
        var tier = NewOpenCriticTierLabel();
        var percentRecommended = NewPercentRecommended();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow("{}"));
        dataSource.Enqueue(OpenCriticCacheRow(gameTitle, topCriticScore, tier, percentRecommended));
        var (service, credentials) = NewService(dataSource, rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.OpencriticEnriched);
        Assert.Equal(tier, result.OcTier);
        Assert.Equal(percentRecommended, result.OcPercentRecommended);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenRawgReportsNoTags_LeavesMultiplayerUnknown()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow("{}"));
        dataSource.Enqueue(EmptyReader());
        var (service, credentials) = NewService(dataSource, rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result.Multiplayer);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenARawgTagContainsAMultiplayerKeyword_ReportsTheGameAsMultiplayer()
    {
        // Arrange
        var nonMultiplayerTag = "Singleplayer";
        var multiplayerKeywordTag = "Online Co-Op";
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow(RawgDetail(new RawgGameDetail { Tags = Named(nonMultiplayerTag, multiplayerKeywordTag) })));
        dataSource.Enqueue(EmptyReader());
        var (service, credentials) = NewService(dataSource, rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.Multiplayer);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenRawgHasTagsButNoneAreMultiplayer_ReportsTheGameAsSingleplayer()
    {
        // Arrange
        var nonMultiplayerTag = NewOpaqueTag();
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow(RawgDetail(new RawgGameDetail { Tags = Named(nonMultiplayerTag) })));
        dataSource.Enqueue(EmptyReader());
        var (service, credentials) = NewService(dataSource, rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.Multiplayer);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenPsnHasNoGenres_FallsBackToRawgGenres()
    {
        // Arrange
        var primaryGenreName = NewGenreName();
        var secondaryGenreName = NewGenreName();
        var primaryGenrePriority = Random.Shared.Next(1, 5);
        var secondaryGenrePriority = primaryGenrePriority + Random.Shared.Next(1, 5);
        var gameTitle = NewGameTitle();
        var titleId = NewTitleId();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow(RawgDetail(new RawgGameDetail { Genres = Named(secondaryGenreName, primaryGenreName) })));
        dataSource.Enqueue(PsnCatalogCacheRow([]));
        dataSource.Enqueue(EmptyReader());
        var (service, credentials) = NewService(
            dataSource,
            rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())),
            catalogClient: new FakeCatalogClient(NotCalled()));
        var priorities = Priorities(
            (primaryGenreName.ToLowerInvariant(), primaryGenrePriority),
            (secondaryGenreName.ToLowerInvariant(), secondaryGenrePriority));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, titleId, priorities, NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(primaryGenreName, result.Genre);
        Assert.Equal(secondaryGenreName, result.Subgenre);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenTitleIdIsNull_SkipsThePsnCatalogLookupEntirely()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        var catalogClient = new FakeCatalogClient(NotCalled());
        var (service, credentials) = NewService(dataSource, catalogClient: catalogClient);

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, catalogClient.CallCount);
        Assert.Null(result.PsnRating);
        Assert.False(result.PsnEnriched);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenPsnResolvesAConceptCarryingNoStarRating_StillReportsPsnEnriched()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var titleId = NewTitleId();
        var dataSource = new FakeDbDataSource();
        var (service, credentials) = NewService(
            dataSource,
            catalogClient: new FakeCatalogClient(new TitleConcept { ConceptId = NewConceptId() }));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, titleId, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.PsnEnriched);
        Assert.Null(result.PsnRating);
        Assert.Null(result.Genre);
        Assert.Null(result.Publisher);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenTheCachedPsnConceptIsReused_ReportsPsnEnrichedWithoutCallingTheCatalogClient()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var titleId = NewTitleId();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(PsnCatalogCacheRow(conceptFetchedAt: DateTimeOffset.UtcNow));
        var catalogClient = new FakeCatalogClient(NotCalled());
        var (service, credentials) = NewService(dataSource, catalogClient: catalogClient);

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, titleId, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.PsnEnriched);
        Assert.Equal(0, catalogClient.CallCount);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenPsnAnswersWithAConceptCarryingNoConceptId_LeavesPsnEnrichedFalse()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var titleId = NewTitleId();
        var dataSource = new FakeDbDataSource();
        var catalogClient = new FakeCatalogClient(new TitleConcept());
        var (service, credentials) = NewService(dataSource, catalogClient: catalogClient);

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, titleId, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(
            result.PsnEnriched,
            "a concept object with no concept id is PS Store answering with nothing, not a resolution.");
        Assert.DoesNotContain(
            dataSource.ExecutedCommands,
            command => command.CapturedCommandText?.Contains(
                "INSERT INTO psn_catalog_cache", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenTheCachedPsnRowResolvedNoConceptId_AsksTheCatalogClientAgainRatherThanServingIt()
    {
        // Arrange
        var freshStarRating = NewStarRating();
        var gameTitle = NewGameTitle();
        var titleId = NewTitleId();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(PsnCatalogCacheRow(conceptFetchedAt: DateTimeOffset.UtcNow, includeConceptId: false));
        dataSource.Enqueue(FakeDbCommand.WithNonQueryResult(1));
        dataSource.Enqueue(EmptyReader());
        var catalogClient = new FakeCatalogClient(new TitleConcept { ConceptId = NewConceptId(), StarRating = freshStarRating });
        var (service, credentials) = NewService(dataSource, catalogClient: catalogClient);

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, titleId, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, catalogClient.CallCount);
        Assert.Equal(freshStarRating, result.PsnRating);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenTheCachedPsnRowResolvedNoConceptIdAndPsnStillPublishesNone_LeavesPsnEnrichedFalseSoTheTitleIsRetried()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var titleId = NewTitleId();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(PsnCatalogCacheRow(conceptFetchedAt: DateTimeOffset.UtcNow, includeConceptId: false));
        var catalogClient = new FakeCatalogClient(new TitleConcept());
        var (service, credentials) = NewService(dataSource, catalogClient: catalogClient);

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, titleId, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, catalogClient.CallCount);
        Assert.False(
            result.PsnEnriched,
            "psn_enriched is the retry gate, so a cached row that resolved nothing must not flip it.");
    }

    [Fact]
    public async Task EnrichGameAsync_WhenPsnIsSkippedBecauseItAlreadySucceeded_StillReconcilesFromItsCachedConcept()
    {
        // Arrange
        var psnGenreName = NewGenreName();
        var psnPublisherName = NewPublisherName();
        var psnEsrbRating = NewEsrbRatingLabel();
        var gameTitle = NewGameTitle();
        var titleId = NewTitleId();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow(RawgDetail(new RawgGameDetail
        {
            Genres = Named(NewGenreName()),
            Publishers = Named(NewPublisherName()),
            EsrbRating = new RawgNamed { Name = NewEsrbRatingLabel() },
        })));
        dataSource.Enqueue(PsnCatalogCacheRow(
            [psnGenreName],
            publisher: psnPublisherName,
            contentRating: psnEsrbRating,
            ratingAuthority: EnrichmentOrchestrationService.EsrbAuthority));
        dataSource.Enqueue(EmptyReader());
        var (service, credentials) = NewService(
            dataSource,
            rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())),
            catalogClient: new FakeCatalogClient(NotCalled()));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle,
            titleId,
            EmptyPriorities(),
            NoTierRules,
            credentials,
            TestContext.Current.CancellationToken,
            OnlyRawgNeeded());

        // Assert
        Assert.Equal(psnGenreName, result.Genre);
        Assert.Equal(psnPublisherName, result.Publisher);
        Assert.Equal(psnEsrbRating, result.Esrb);
        Assert.False(
            result.PsnEnriched,
            "the pass never asked PS Store, so it must not claim a resolution that would open the psn_rating guard.");
        Assert.False(result.PsnAttempted);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenRawgIsSkippedBecauseItAlreadySucceeded_StillReconcilesFromItsCachedDetail()
    {
        // Arrange
        var rawgPublisherName = NewPublisherName();
        var rawgDeveloperName = NewPublisherName();
        var metacriticScore = NewCriticalScore();
        var gameTitle = NewGameTitle();
        var titleId = NewTitleId();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow(RawgDetail(new RawgGameDetail
        {
            Publishers = Named(rawgPublisherName),
            Developers = Named(rawgDeveloperName),
            Metacritic = metacriticScore,
        })));
        dataSource.Enqueue(PsnCatalogCacheRow(starRating: NewStarRating()));
        dataSource.Enqueue(EmptyReader());
        var (service, credentials) = NewService(dataSource, catalogClient: new FakeCatalogClient(NotCalled()));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle,
            titleId,
            EmptyPriorities(),
            NoTierRules,
            credentials,
            TestContext.Current.CancellationToken,
            OnlyPsnNeeded());

        // Assert
        Assert.Equal(rawgPublisherName, result.Publisher);
        Assert.Equal(rawgDeveloperName, result.Developer);
        Assert.Equal(metacriticScore, result.CriticalScore);
        Assert.True(result.PsnEnriched);
        Assert.False(
            result.RawgEnriched,
            "the pass never asked RAWG, so it must not claim a resolution that would open the developer guard.");
        Assert.False(result.RawgAttempted);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenOpenCriticIsSkippedBecauseItAlreadySucceeded_StillReportsBothScoreSources()
    {
        // Arrange
        var metacriticScore = NewCriticalScore();
        var topCriticScore = NewOpenCriticScore();
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow(RawgDetail(new RawgGameDetail { Metacritic = metacriticScore })));
        dataSource.Enqueue(OpenCriticCacheRow(
            gameTitle, topCriticScore, NewOpenCriticTierLabel(), NewPercentRecommended()));
        var (service, credentials) = NewService(
            dataSource, rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle,
            null,
            EmptyPriorities(),
            NoTierRules,
            credentials,
            TestContext.Current.CancellationToken,
            OnlyRawgNeeded());

        // Assert
        Assert.Equal(EnrichmentOrchestrationService.RawgAndOpenCriticScoreSource, result.ScoreSource);
        Assert.Equal(topCriticScore, result.OcScore);
        Assert.False(
            result.OpencriticEnriched,
            "the pass never asked OpenCritic, so it must not claim a resolution that would open the oc_score guard.");
        Assert.False(result.OpencriticAttempted);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenTheCatalogClientHitsATransportFailure_ResolvesEmptyPsnSignalsInsteadOfThrowing()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var titleId = NewTitleId();
        var dataSource = new FakeDbDataSource();
        var catalogClient = new FakeCatalogClient(new HttpRequestException(NewTransportFailureMessage()));
        var (service, credentials) = NewService(dataSource, catalogClient: catalogClient);

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, titleId, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result.PsnRating);
        Assert.Null(result.Genre);
        Assert.False(result.PsnEnriched);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenTheCatalogClientHitsATransportFailure_DoesNotWriteAPsnCatalogCacheRow()
    {
        // Arrange
        var gameTitle = NewGameTitle();
        var titleId = NewTitleId();
        var dataSource = new FakeDbDataSource();
        var (service, credentials) = NewService(dataSource, catalogClient: new FakeCatalogClient(new HttpRequestException(NewTransportFailureMessage())));

        // Act
        await service.EnrichGameAsync(
            gameTitle, titleId, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain(
            dataSource.ExecutedCommands,
            command => command.CapturedCommandText?.Contains(
                "INSERT INTO psn_catalog_cache", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenThePublisherMatchesAnAaaRule_ClassifiesTheTierAsAaa()
    {
        // Arrange
        var aaaPublisherName = NewPublisherName();
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow(RawgDetail(new RawgGameDetail { Publishers = Named(aaaPublisherName) })));
        dataSource.Enqueue(EmptyReader());
        var (service, credentials) = NewService(dataSource, rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), PublisherTierRuleSet.Prepare([AaaPublisherRule(aaaPublisherName)]), credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(PublisherTierRuleSet.AaaTier, result.AaaTier);
    }

    [Fact]
    public async Task EnrichGameAsync_WhenOnlyTheDeveloperMatchesAnAaaRule_ClassifiesTheTierFromTheDeveloper()
    {
        // Arrange
        var aaaDeveloperName = NewPublisherName();
        var gameTitle = NewGameTitle();
        var dataSource = new FakeDbDataSource();
        dataSource.Enqueue(RawgCacheRow(RawgDetail(
            new RawgGameDetail { Developers = Named(aaaDeveloperName), Publishers = [] })));
        dataSource.Enqueue(EmptyReader());
        var (service, credentials) = NewService(dataSource, rawgClient: NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())));

        // Act
        var result = await service.EnrichGameAsync(
            gameTitle, null, EmptyPriorities(), PublisherTierRuleSet.Prepare([AaaPublisherRule(aaaDeveloperName)]), credentials, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(PublisherTierRuleSet.AaaTier, result.AaaTier);
    }

    private static Dictionary<string, int> EmptyPriorities() =>
        new Dictionary<string, int>(StringComparer.Ordinal);

    private static EnrichmentNeed OnlyRawgNeeded() =>
        new(Guid.NewGuid().ToString(), Rawg: true, OpenCritic: false, Psn: false);

    private static EnrichmentNeed OnlyPsnNeeded() =>
        new(Guid.NewGuid().ToString(), Rawg: false, OpenCritic: false, Psn: true);

    private static Dictionary<string, int> Priorities(params (string Name, int Priority)[] entries) =>
        entries.ToDictionary(entry => entry.Name, entry => entry.Priority, StringComparer.Ordinal);

    private static PublisherTierRule AaaPublisherRule(string publisherOrDeveloperName) =>
        new(Guid.NewGuid(), publisherOrDeveloperName.ToLowerInvariant(), PublisherTierRuleSet.AaaTier, PublisherTierRuleSet.ExactMatchKind);

    private static (EnrichmentOrchestrationService Service, EnrichmentCredentials Credentials) NewService(
        FakeDbDataSource dataSource,
        IRawgClient? rawgClient = null,
        IOpenCriticClient? openCriticClient = null,
        ICatalogClient? catalogClient = null)
    {
        var service = new EnrichmentOrchestrationService(
            rawgClient ?? NewRawgClient(StubHttpMessageHandler.Throws(NotCalled())),
            openCriticClient ?? NewOpenCriticClient(StubHttpMessageHandler.Throws(NotCalled())),
            catalogClient ?? new FakeCatalogClient(NotCalled()),
            new EnrichmentRepository(dataSource),
            new OpenCriticCacheRepository(dataSource));
        return (service, Credentials(rawgClient, openCriticClient, catalogClient));
    }

    private static EnrichmentCredentials Credentials(
        IRawgClient? rawgClient,
        IOpenCriticClient? openCriticClient,
        ICatalogClient? catalogClient)
    {
        var rawgApiKey = Guid.NewGuid().ToString();
        var openCriticApiKey = Guid.NewGuid().ToString();
        return new()
        {
            Rawg = rawgClient is null ? null : new RawgCredential { ApiKey = rawgApiKey },
            OpenCritic = openCriticClient is null
                ? null
                : new OpenCriticCredential { RapidApiKey = openCriticApiKey },
            Psn = catalogClient is null
                ? null
                : new PsnSessionRotation([new PsnSession(null, null, NullPsnRateLimiter.Unthrottled)]),
        };
    }

    private static RawgClient NewRawgClient(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler), new Uri("https://api.rawg.io/api/"));

    private static OpenCriticClient NewOpenCriticClient(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler), new Uri("https://opencritic-api.p.rapidapi.com/"));

    private static InvalidOperationException NotCalled() => new("This collaborator must not be called.");

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static FakeDbCommand EmptyReader() => FakeDbCommand.WithReader(new DataTable());

    private static void EnqueueEmptyCacheReads(FakeDbDataSource dataSource, int enrichmentAttempts)
    {
        for (var attempt = 0; attempt < enrichmentAttempts; attempt++)
        {
            dataSource.Enqueue(EmptyReader());
            dataSource.Enqueue(EmptyReader());
        }
    }

    private static StubHttpMessageHandler ServerErrorOnAttempt(int serverErrorAttempt)
    {
        var attempts = 0;
        return StubHttpMessageHandler.Responds(() =>
        {
            attempts++;
            return attempts == serverErrorAttempt
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))
                : Task.FromException<HttpResponseMessage>(new HttpRequestException(NewTransportFailureMessage()));
        });
    }

    private static async Task EnrichRepeatedlyAsync(
        EnrichmentOrchestrationService service, EnrichmentCredentials credentials, string title, int enrichmentAttempts)
    {
        for (var attempt = 0; attempt < enrichmentAttempts; attempt++)
        {
            await service.EnrichGameAsync(
                title, null, EmptyPriorities(), NoTierRules, credentials, TestContext.Current.CancellationToken);
        }
    }

    private static HttpResponseMessage FullPageWithRequestsNearlyExhausted()
    {
        var entries = Enumerable
            .Range(1, OpenCriticClient.DefaultPageSize)
            .Select(id => JsonSerializer.Serialize(new OpenCriticGameEntry
            {
                Id = id,
                Name = $"Catalog Entry {id}",
                TopCriticScore = NewOpenCriticScore(),
                Tier = NewOpenCriticTierLabel(),
            }));
        var response = Json(HttpStatusCode.OK, $"[{string.Join(',', entries)}]");
        response.Headers.Add(OpenCriticClient.RemainingRequestsHeader, "1");
        return response;
    }

    private static string RawgDetail(RawgGameDetail detail) =>
        JsonSerializer.Serialize(detail, RawgWireFormat);

    private static RawgNamed[] Named(params string[] names) =>
        [.. names.Select(name => new RawgNamed { Name = name })];

    private static string NewGameTitle() => $"Title-{Guid.NewGuid():N}";

    private static string NewTitleId() => $"CUSA{Random.Shared.Next(10000, 100000)}_00";

    private static string NewConceptId() => Random.Shared.Next(1, 1_000_000).ToString(CultureInfo.InvariantCulture);

    private static string NewPublisherName() => $"Publisher-{Guid.NewGuid():N}";

    private static string NewGenreName() => $"Genre-{Guid.NewGuid():N}";

    private static string NewOpaqueTag() => $"Tag-{Guid.NewGuid():N}";

    private static string NewEsrbRatingLabel() => $"Rating-{Guid.NewGuid():N}";

    private static string NewAuthorityName() => $"Authority-{Guid.NewGuid():N}";

    private static string NewRawgReleasedText() => $"{Random.Shared.Next(1980, 2030)}-01-01";

    private static DateOnly NewReleaseDate() =>
        new(Random.Shared.Next(1980, 2030), Random.Shared.Next(1, 13), Random.Shared.Next(1, 28));

    private static double NewStarRating() => Random.Shared.Next(10, 51) / 10.0;

    private static double NewCriticalScore() => Random.Shared.Next(1, 101);

    private static double NewOpenCriticScore() => TestValues.NewOpenCriticScore();

    private static double NewPercentRecommended() => Random.Shared.Next(0, 1001) / 10.0;

    private static string NewOpenCriticTierLabel() => $"Tier-{Guid.NewGuid():N}";

    private static int NewRawgGameId() => TestValues.NewRawgGameId();

    private static int NewOpenCriticGameId() => Random.Shared.Next(1, 1_000_000);

    private static string NewCoverImageUrl() => $"https://example.test/cover-{Guid.NewGuid():N}.png";

    private static string NewTransportFailureMessage() => $"transport-failure-{Guid.NewGuid():N}";

    private static string NewProviderErrorBody() => $$"""{"detail":"{{Guid.NewGuid():N}}"}""";

    private static FakeDbCommand RawgCacheRow(string? raw)
    {
        var table = new DataTable();
        table.Columns.Add("normalized_title", typeof(string));
        table.Columns.Add("rawg_game_id", typeof(int));
        table.Columns.Add("raw", typeof(string));
        table.Rows.Add(NewGameTitle(), NewRawgGameId(), raw is null ? DBNull.Value : raw);
        return FakeDbCommand.WithReader(table);
    }

    private static FakeDbCommand OpenCriticCacheRow(string name, double topCriticScore, string tier, double percentRecommended)
    {
        var table = new DataTable();
        table.Columns.Add("oc_game_id", typeof(int));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("top_critic_score", typeof(double));
        table.Columns.Add("tier", typeof(string));
        table.Columns.Add("percent_recommended", typeof(double));
        table.Rows.Add(NewOpenCriticGameId(), name, topCriticScore, tier, percentRecommended);
        return FakeDbCommand.WithReader(table);
    }

    private static FakeDbCommand PsnCatalogCacheRow(
        string? publisher = null,
        string? contentRating = null,
        string? ratingAuthority = null,
        bool? multiplayer = null,
        DateOnly? releaseDate = null,
        double? starRating = null,
        DateTimeOffset? conceptFetchedAt = null,
        bool includeConceptFetchedAt = true,
        bool includeConceptId = true) =>
        PsnCatalogCacheRow(
            [],
            publisher,
            contentRating,
            ratingAuthority,
            multiplayer,
            releaseDate,
            starRating,
            conceptFetchedAt,
            includeConceptFetchedAt,
            includeConceptId);

    private static FakeDbCommand PsnCatalogCacheRow(
        string[] genres,
        string? publisher = null,
        string? contentRating = null,
        string? ratingAuthority = null,
        bool? multiplayer = null,
        DateOnly? releaseDate = null,
        double? starRating = null,
        DateTimeOffset? conceptFetchedAt = null,
        bool includeConceptFetchedAt = true,
        bool includeConceptId = true)
    {
        var table = new DataTable();
        table.Columns.Add("title_id", typeof(string));
        table.Columns.Add("concept_id", typeof(string));
        table.Columns.Add("genres", typeof(object));
        table.Columns.Add("star_rating", typeof(double));
        table.Columns.Add("publisher", typeof(string));
        table.Columns.Add("release_date", typeof(DateOnly));
        table.Columns.Add("cover_image_url", typeof(string));
        table.Columns.Add("content_rating", typeof(string));
        table.Columns.Add("rating_authority", typeof(string));
        table.Columns.Add("multiplayer", typeof(bool));
        table.Columns.Add("concept_fetched_at", typeof(DateTimeOffset));
        var resolvedAt = includeConceptFetchedAt
            ? conceptFetchedAt ?? DateTimeOffset.UtcNow
            : (DateTimeOffset?)null;
        table.Rows.Add(
            Guid.NewGuid().ToString(),
            includeConceptId ? Guid.NewGuid().ToString() : DBNull.Value,
            genres,
            starRating is null ? DBNull.Value : starRating,
            publisher is null ? DBNull.Value : publisher,
            releaseDate is null ? DBNull.Value : releaseDate,
            NewCoverImageUrl(),
            contentRating is null ? DBNull.Value : contentRating,
            ratingAuthority is null ? DBNull.Value : ratingAuthority,
            multiplayer is null ? DBNull.Value : multiplayer,
            resolvedAt is null ? DBNull.Value : resolvedAt);
        return FakeDbCommand.WithReader(table);
    }

    private sealed class FakeCatalogClient : ICatalogClient
    {
        private readonly Func<Task<TitleConcept>> _invoke;

        public FakeCatalogClient(TitleConcept result) => _invoke = () => Task.FromResult(result);

        public FakeCatalogClient(Exception toThrow) => _invoke = () => Task.FromException<TitleConcept>(toThrow);

        public int CallCount { get; private set; }

        public Task<TitleConcept> TitleConceptAsync(
            PsnSession session,
            string titleId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return _invoke();
        }
    }
}
