namespace Functions.Tests.Unit;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Curator.Library;
using Curator.Psn;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class PsnTrophyClientTests
{
    private const int NextPageOffset = 50;
    private const int AccessTokenLifetimeSeconds = 3600;

    private static readonly JsonSerializerOptions PsnWireFormat =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public async Task TrophyTitlesAsync_ReturnsTheTitlesOnASinglePage()
    {
        // Arrange
        var npCommunicationId = NewNpCommunicationId();
        var gameName = NewGameName();
        var progress = NewProgress();
        var handler = StubHttpMessageHandler.Always(
            () => Page([Entry(npCommunicationId, gameName, progress)], nextOffset: null));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        var titles = await client.TrophyTitlesAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var title = Assert.Single(titles);
        Assert.Equal(gameName, title.Name);
        Assert.Equal(npCommunicationId, title.NpCommunicationId);
        Assert.Equal(progress, title.Progress);
    }

    [Fact]
    public async Task TrophyTitlesAsync_StopsPaging_WhenTheLastPageOmitsNextOffset()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Always(() => Page([Entry(NewNpCommunicationId(), NewGameName(), NewProgress())], nextOffset: null));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        await client.TrophyTitlesAsync(session, limit: TrophyMatchService.TrophyTitlesLimit, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TrophyTitlesAsync_StopsPaging_WhenAPageComesBackEmpty()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Sequence(
            Page([Entry(NewNpCommunicationId(), NewGameName(), NewProgress())], nextOffset: NextPageOffset),
            Page([], nextOffset: NextPageOffset * 2));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        var titles = await client.TrophyTitlesAsync(
            session,
            limit: TrophyMatchService.TrophyTitlesLimit,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(titles);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task TrophyTitlesAsync_KeepsPaging_WhileTheResponseAdvertisesAFurtherOffset()
    {
        // Arrange
        var firstPageTitleId = NewNpCommunicationId();
        var secondPageTitleId = NewNpCommunicationId();
        var handler = StubHttpMessageHandler.Sequence(
            Page([Entry(firstPageTitleId, NewGameName(), NewProgress())], nextOffset: NextPageOffset),
            Page([Entry(secondPageTitleId, NewGameName(), NewProgress())], nextOffset: null));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        var titles = await client.TrophyTitlesAsync(
            session,
            limit: TrophyMatchService.TrophyTitlesLimit,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(
            [firstPageTitleId, secondPageTitleId],
            titles.Select(title => title.NpCommunicationId));
    }

    [Fact]
    public async Task TrophyTitlesAsync_NeverRequestsMoreThanTheCallersRemainingLimit()
    {
        // Arrange
        var limitBelowOnePage = Random.Shared.Next(1, NextPageOffset);
        var handler = StubHttpMessageHandler.Always(() => Page([Entry(NewNpCommunicationId(), NewGameName(), NewProgress())], nextOffset: null));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        await client.TrophyTitlesAsync(
            session, limit: limitBelowOnePage, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(
            $"limit={limitBelowOnePage}",
            handler.Requests[0].RequestUri?.Query,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrophyTitlesAsync_TargetsTheAuthenticatedUser()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Always(() => Page([], nextOffset: null));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        await client.TrophyTitlesAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("/api/trophy/v1/users/me/trophyTitles", handler.Requests[0].RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task TrophyTitlesByTitleIdAsync_KeysEachResultByTheRequestedTitleIdItCameBackUnder()
    {
        // Arrange
        var firstTitleId = NewTitleId();
        var secondTitleId = NewTitleId();
        var firstNpCommunicationId = NewNpCommunicationId();
        var secondNpCommunicationId = NewNpCommunicationId();
        var firstProgress = NewProgress();
        var handler = StubHttpMessageHandler.Returns(Titles(
            Title(firstTitleId, Entry(firstNpCommunicationId, NewGameName(), firstProgress)),
            Title(secondTitleId, Entry(secondNpCommunicationId, NewGameName(), NewProgress()))));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        var titles = await client.TrophyTitlesByTitleIdAsync(
            session, [firstTitleId, secondTitleId], TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(firstNpCommunicationId, titles[firstTitleId].NpCommunicationId);
        Assert.Equal(firstProgress, titles[firstTitleId].Progress);
        Assert.Equal(secondNpCommunicationId, titles[secondTitleId].NpCommunicationId);
    }

    [Fact]
    public async Task TrophyTitlesByTitleIdAsync_OmitsATitleWhosePsnEntryCarriesNoUsableProgress()
    {
        // Arrange
        var titleIdWithoutProgress = NewTitleId();
        var titleIdWithNoTrophyTitlesAtAll = NewTitleId();
        var handler = StubHttpMessageHandler.Returns(Titles(
            Title(
                titleIdWithoutProgress,
                Entry(NewNpCommunicationId(), NewGameName(), progress: null)),
            Title(titleIdWithNoTrophyTitlesAtAll)));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        var titles = await client.TrophyTitlesByTitleIdAsync(
            session,
            [titleIdWithoutProgress, titleIdWithNoTrophyTitlesAtAll],
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(titles);
    }

    [Fact]
    public async Task TrophyTitlesByTitleIdAsync_SendsEveryRequestedTitleIdAsOneCommaSeparatedParameter()
    {
        // Arrange
        var firstTitleId = NewTitleId();
        var secondTitleId = NewTitleId();
        var handler = StubHttpMessageHandler.Returns(Titles());
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        await client.TrophyTitlesByTitleIdAsync(
            session, [firstTitleId, secondTitleId], TestContext.Current.CancellationToken);

        // Assert
        var requestedUri = Assert.IsType<Uri>(handler.Requests[0].RequestUri);
        var query = Uri.UnescapeDataString(requestedUri.Query);
        Assert.Contains($"npTitleIds={firstTitleId},{secondTitleId}", query, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TrophyTitlesByTitleIdAsync_ReturnsNothing_WhenPsnKnowsNoneOfTheTitles()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Returns(Titles());
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        var titles = await client.TrophyTitlesByTitleIdAsync(
            session, [NewTitleId()], TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(titles);
    }

    [Fact]
    public async Task TrophyTitlesAsync_StopsPaging_WhenTheResponseReportsNextOffsetZero()
    {
        // Arrange
        var handler = StubHttpMessageHandler.Always(
            () => Page([Entry(NewNpCommunicationId(), NewGameName(), NewProgress())], nextOffset: 0));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        await client.TrophyTitlesAsync(session, limit: TrophyMatchService.TrophyTitlesLimit, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TrophyTitlesByTitleIdAsync_OmitsATitlePsnKnowsButHasNoTrophyTitlesFor()
    {
        // Arrange
        var knownTitleId = NewTitleId();
        var handler = StubHttpMessageHandler.Returns(Titles(Title(knownTitleId)));
        var session = await ReadySessionAsync(handler);
        var client = new PsnTrophyClient();

        // Act
        var titles = await client.TrophyTitlesByTitleIdAsync(
            session, [knownTitleId], TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(titles);
    }

    private static string NewNpCommunicationId() => TestValues.NewNpCommunicationId();

    private static string NewTitleId() =>
        TestValues.NewTitleId();

    private static string NewGameName() => TestValues.NewGameName();

    private static int NewProgress() => Random.Shared.Next(1, 100);

    private static HttpResponseMessage Page(IReadOnlyList<PsnTrophyTitle> entries, int? nextOffset) =>
        Json(JsonSerializer.Serialize(
            new PsnTrophyTitlesResponse
            {
                TrophyTitles = entries,
                NextOffset = nextOffset,
                TotalItemCount = entries.Count,
            },
            PsnWireFormat));

    private static HttpResponseMessage Titles(params PsnTitleTrophyTitles[] titles) =>
        Json(JsonSerializer.Serialize(
            new PsnTitleTrophyTitlesResponse { Titles = titles }, PsnWireFormat));

    private static PsnTitleTrophyTitles Title(string npTitleId, params PsnTrophyTitle[] trophyTitles) =>
        new() { NpTitleId = npTitleId, TrophyTitles = trophyTitles };

    private static PsnTrophyTitle Entry(string npCommunicationId, string name, int? progress) =>
        new() { NpCommunicationId = npCommunicationId, TrophyTitleName = name, Progress = progress };

    private static async Task<PsnSession> ReadySessionAsync(StubHttpMessageHandler handler) =>
        await PsnSession.RestoreAsync(
            null,
            SeededStore(),
            rateLimiter: NullPsnRateLimiter.Unthrottled,
            httpClient: new HttpClient(handler),
            cancellationToken: TestContext.Current.CancellationToken);

    private static InMemoryPsnTokenStore SeededStore()
    {
        var store = new InMemoryPsnTokenStore();
        store.SaveAsync(
            new PsnTokenResponse
            {
                AccessToken = TestValues.NewAccessToken(),
                ExpiresIn = AccessTokenLifetimeSeconds,
                AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            },
            TestContext.Current.CancellationToken);
        return store;
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
