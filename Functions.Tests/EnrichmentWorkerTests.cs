namespace Functions.Tests;

using System.ClientModel;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Moq;
using OpenAI.Responses;

[Trait("Category", "Unit")]
public sealed class EnrichmentWorkerTests
{
    [Fact]
    public void Constructor_WhenOpenAIModelNotConfigured_Throws()
    {
        // Arrange — CreateClient must succeed so the constructor reaches GetRequired<string>
        var openAI = new Mock<ResponsesClient>(MockBehavior.Strict);
        var busFactory = new Mock<IAzureClientFactory<ServiceBusClient>>(MockBehavior.Strict);
        busFactory.Setup(f => f.CreateClient("crgolden")).Returns(Mock.Of<ServiceBusClient>());
        var blobFactory = new Mock<IAzureClientFactory<BlobServiceClient>>(MockBehavior.Strict);
        blobFactory.Setup(f => f.CreateClient("crgolden")).Returns(Mock.Of<BlobServiceClient>());
        var config = new ConfigurationBuilder().Build();

        // Act / Assert
        Assert.Throws<InvalidOperationException>(() =>
            new EnrichmentWorker(openAI.Object, busFactory.Object, blobFactory.Object, config));
    }

    [Fact]
    public async Task Run_WhenPayloadIsNull_DeadLettersMessageWithoutCallingOpenAI()
    {
        // Arrange
        var openAI = new Mock<ResponsesClient>(MockBehavior.Strict);
        var (worker, geocodingSender) = BuildWorker(openAI);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString("null"));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions
            .Setup(a => a.DeadLetterMessageAsync(message, null, "malformed-payload", null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        openAI.VerifyNoOtherCalls();
        geocodingSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.DeadLetterMessageAsync(message, null, "malformed-payload", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenOpenAIFailsAndDeliveryCountLow_AbandonsForRetry()
    {
        // Arrange — delivery count below the retry ceiling: quiet abandon, no rethrow, no degrade
        var openAI = new Mock<ResponsesClient>(MockBehavior.Strict);
        openAI
            .Setup(o => o.CreateResponseAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<ResponseItem>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ClientResultException("transient"));
        var (worker, geocodingSender) = BuildWorker(openAI);
        var payload = new EnrichmentRequest(Guid.NewGuid(), "https://grace.example", BlobPath: null, Partial());
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromObjectAsJson(payload),
            deliveryCount: 1);
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions
            .Setup(a => a.AbandonMessageAsync(message, It.IsAny<IDictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        geocodingSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.AbandonMessageAsync(message, It.IsAny<IDictionary<string, object>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenOpenAIFailsAndDeliveryCountHigh_DegradesAndCompletes()
    {
        // Arrange — delivery count at the retry ceiling: degrade to the extractor's partial data
        // and route it straight to geocoding so the pipeline completes.
        var openAI = new Mock<ResponsesClient>(MockBehavior.Strict);
        openAI
            .Setup(o => o.CreateResponseAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<ResponseItem>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ClientResultException("persistent"));
        var (worker, geocodingSender) = BuildWorker(openAI);
        var payload = new EnrichmentRequest(Guid.NewGuid(), "https://grace.example", BlobPath: null, Partial());
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromObjectAsJson(payload),
            deliveryCount: 3);
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert — partial data routed to geocoding; message completed, not abandoned
        geocodingSender.Verify(
            s => s.SendMessageAsync(
                It.Is<ServiceBusMessage>(m => m.Body.ToString().Contains("PartialCity", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- TryParseEnrichment (pure, internal static) ---
    [Fact]
    public void TryParseEnrichment_CleanJsonAllFieldsValid_MapsEveryField()
    {
        // Arrange
        const string json = """
            {"canonicalName":"Grace Church","city":"Phoenix","state":"AZ","zip":"85001",
             "worshipStyle":2,"primaryLanguage":"Spanish","denomination":"Baptist","acceptsLGBTQ":true,
             "wheelchairAccessible":false,"hasNursery":true,"hasYouthProgram":false}
            """;

        // Act
        var result = EnrichmentWorker.TryParseEnrichment(json, Partial());

        // Assert
        Assert.Equal("Grace Church", result.CanonicalName);
        Assert.Equal("Phoenix", result.City);
        Assert.Equal(2, result.WorshipStyle);
        Assert.Equal("Spanish", result.PrimaryLanguage);
        Assert.Equal("Baptist", result.Denomination);
        Assert.True(result.AcceptsLGBTQ);
        Assert.False(result.WheelchairAccessible);
        Assert.True(result.HasNursery);
        Assert.False(result.HasYouthProgram);
    }

    [Fact]
    public void EnrichmentAttributes_DenominationAndWorshipStyle_AreEmitted()
    {
        var enriched = new EnrichedData("Grace", "Phoenix", "AZ", "85001", 2, "English", null, null, null, null, "Baptist", [], [], []);

        var attributes = EnrichmentWorker.EnrichmentAttributes(enriched);

        Assert.Contains(attributes, a => a.Key == "denomination" && a.Value == "Baptist" && a.Source == "enrichment");
        Assert.Contains(attributes, a => a.Key == "worship_style" && a.Value == "2" && a.Source == "enrichment");
    }

    [Fact]
    public void EnrichmentAttributes_NoSignals_ReturnsEmpty()
    {
        var enriched = new EnrichedData("Grace", null, null, null, 0, "English", null, null, null, null, null, [], [], []);

        Assert.Empty(EnrichmentWorker.EnrichmentAttributes(enriched));
    }

    [Fact]
    public void TryParseEnrichment_ServiceSchedules_AreParsed()
    {
        const string json = """
            {"canonicalName":"Grace","serviceSchedules":[{"dayOfWeek":0,"startTime":"10:30","description":"Sunday Worship"},{"dayOfWeek":3,"startTime":"19:00","description":"Bible Study"}]}
            """;

        var result = EnrichmentWorker.TryParseEnrichment(json, Partial());

        Assert.Equal(2, result.ServiceSchedules.Count);
        Assert.Equal((byte)0, result.ServiceSchedules[0].DayOfWeek);
        Assert.Equal("10:30", result.ServiceSchedules[0].StartTime);
        Assert.Equal("Sunday Worship", result.ServiceSchedules[0].Description);
    }

    [Fact]
    public void TryParseEnrichment_ServiceSchedulesAbsent_ReturnsEmpty()
    {
        var result = EnrichmentWorker.TryParseEnrichment("{\"canonicalName\":\"Grace\"}", Partial());

        Assert.Empty(result.ServiceSchedules);
    }

    [Fact]
    public void TryParseEnrichment_Ministries_AreParsed()
    {
        const string json = """
            {"canonicalName":"Grace","ministries":[{"name":"Youth Group","description":"Teens"},{"name":"Food Bank"}]}
            """;

        var result = EnrichmentWorker.TryParseEnrichment(json, Partial());

        Assert.Equal(2, result.Ministries.Count);
        Assert.Equal("Youth Group", result.Ministries[0].Name);
        Assert.Equal("Teens", result.Ministries[0].Description);
        Assert.Null(result.Ministries[1].Description);
    }

    [Fact]
    public void TryParseEnrichment_Campuses_AreParsed()
    {
        const string json = """
            {"canonicalName":"Grace","campuses":[{"name":"North Campus","street":"1 N St","city":"Denver","state":"CO","zip":"80201"},{"name":"Incomplete","city":"Denver"}]}
            """;

        var result = EnrichmentWorker.TryParseEnrichment(json, Partial());

        // Second campus is missing state/zip and is dropped.
        Assert.Single(result.Campuses);
        Assert.Equal("North Campus", result.Campuses[0].Name);
        Assert.Equal("Denver", result.Campuses[0].City);
    }

    [Fact]
    public void TryParseEnrichment_DenominationAbsent_ReturnsNull()
    {
        // Act
        var result = EnrichmentWorker.TryParseEnrichment("{\"canonicalName\":\"Grace\"}", Partial());

        // Assert
        Assert.Null(result.Denomination);
    }

    [Fact]
    public void TryParseEnrichment_JsonWrappedInProse_SlicesBracesAndParses()
    {
        // Arrange — fenced/prose-wrapped output; both slice operands (start >= 0 && end > start) are true
        const string json = "Here is the data:\n```json\n{\"canonicalName\":\"Grace\"}\n```";

        // Act
        var result = EnrichmentWorker.TryParseEnrichment(json, Partial());

        // Assert
        Assert.Equal("Grace", result.CanonicalName);
    }

    [Fact]
    public void TryParseEnrichment_NoOpeningBrace_FallsBackToPartial()
    {
        // Arrange — no '{', so the slice AND short-circuits (first operand false) and Parse throws → catch
        var result = EnrichmentWorker.TryParseEnrichment("no json here", Partial());

        // Assert
        Assert.Equal("PartialName", result.CanonicalName);
        Assert.Equal("PartialCity", result.City);
    }

    [Fact]
    public void TryParseEnrichment_OpeningBraceNoClose_FallsBackToPartial()
    {
        // Arrange — '{' present but no '}', so end > start is false; Parse throws → catch
        var result = EnrichmentWorker.TryParseEnrichment("{ broken", Partial());

        // Assert
        Assert.Equal("PartialName", result.CanonicalName);
    }

    [Fact]
    public void TryParseEnrichment_MalformedJsonInsideBraces_FallsBackToPartial()
    {
        // Arrange — braces slice cleanly but the content is not valid JSON → outer catch
        var result = EnrichmentWorker.TryParseEnrichment("{not valid json}", Partial());

        // Assert
        Assert.Equal("PartialName", result.CanonicalName);
        Assert.Equal(0, result.WorshipStyle);
        Assert.Equal("English", result.PrimaryLanguage);
    }

    [Fact]
    public void TryParseEnrichment_CanonicalNameWrongKind_FallsBackForThatField()
    {
        // Arrange — canonicalName is a number, so GetStr returns null → ?? partial.CanonicalName
        var result = EnrichmentWorker.TryParseEnrichment("{\"canonicalName\":123,\"city\":\"Phoenix\"}", Partial());

        // Assert
        Assert.Equal("PartialName", result.CanonicalName);
        Assert.Equal("Phoenix", result.City);
    }

    [Fact]
    public void TryParseEnrichment_CityKeyAbsent_FallsBackForThatField()
    {
        // Arrange — city key missing, so GetStr returns null → ?? partial.City
        var result = EnrichmentWorker.TryParseEnrichment("{\"canonicalName\":\"Grace\"}", Partial());

        // Assert
        Assert.Equal("Grace", result.CanonicalName);
        Assert.Equal("PartialCity", result.City);
    }

    [Fact]
    public void TryParseEnrichment_AcceptsLgbtqTrue_ReturnsTrue()
    {
        // Act
        var result = EnrichmentWorker.TryParseEnrichment("{\"acceptsLGBTQ\":true}", Partial());

        // Assert
        Assert.True(result.AcceptsLGBTQ);
    }

    [Fact]
    public void TryParseEnrichment_AcceptsLgbtqFalse_ReturnsFalse()
    {
        // Act
        var result = EnrichmentWorker.TryParseEnrichment("{\"acceptsLGBTQ\":false}", Partial());

        // Assert
        Assert.False(result.AcceptsLGBTQ);
    }

    [Fact]
    public void TryParseEnrichment_AcceptsLgbtqNullLiteral_ReturnsNull()
    {
        // Arrange — a JSON null is neither True nor False, so GetBool returns null
        var result = EnrichmentWorker.TryParseEnrichment("{\"acceptsLGBTQ\":null}", Partial());

        // Assert
        Assert.Null(result.AcceptsLGBTQ);
    }

    [Fact]
    public void TryParseEnrichment_WorshipStyleAndLanguageAbsent_UseDefaults()
    {
        // Arrange — worshipStyle absent → GetInt 0; primaryLanguage absent → ?? "English"
        var result = EnrichmentWorker.TryParseEnrichment("{\"canonicalName\":\"Grace\"}", Partial());

        // Assert
        Assert.Equal(0, result.WorshipStyle);
        Assert.Equal("English", result.PrimaryLanguage);
    }

    // --- BuildPageContent (pure, internal static) ---
    [Fact]
    public void BuildPageContent_HtmlIsNull_ReturnsNotAvailable()
    {
        Assert.Equal("Not available.", EnrichmentWorker.BuildPageContent(null));
    }

    [Fact]
    public void BuildPageContent_ShortHtml_ReturnedUnchanged()
    {
        const string html = "<html><body>Grace Church, 123 Main St, Phoenix AZ</body></html>";

        var result = EnrichmentWorker.BuildPageContent(html);

        Assert.Equal(html, result);
    }

    [Fact]
    public void BuildPageContent_HtmlExceedsCap_IsTruncatedToCap()
    {
        var html = new string('a', 25_000);

        var result = EnrichmentWorker.BuildPageContent(html);

        Assert.Equal(20_000, result.Length);
        Assert.Equal(html[..20_000], result);
    }

    private static EnrichmentPartialData Partial() =>
        new("PartialName", "PartialCity", "PartialState", "00000");

    private static (EnrichmentWorker Worker, Mock<ServiceBusSender> GeocodingSender) BuildWorker(Mock<ResponsesClient> openAI)
    {
        var geocodingSender = new Mock<ServiceBusSender>(MockBehavior.Strict);
        geocodingSender.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        geocodingSender.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var serviceBusClient = new Mock<ServiceBusClient>(MockBehavior.Strict);
        serviceBusClient.Setup(c => c.CreateSender("geocoding-requests")).Returns(geocodingSender.Object);

        var busFactory = new Mock<IAzureClientFactory<ServiceBusClient>>(MockBehavior.Strict);
        busFactory.Setup(f => f.CreateClient("crgolden")).Returns(serviceBusClient.Object);

        var blobFactory = new Mock<IAzureClientFactory<BlobServiceClient>>(MockBehavior.Strict);
        blobFactory.Setup(f => f.CreateClient("crgolden")).Returns(Mock.Of<BlobServiceClient>());

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([new("OpenAIModel", "gpt-4.1-mini")])
            .Build();

        return (new EnrichmentWorker(openAI.Object, busFactory.Object, blobFactory.Object, config), geocodingSender);
    }
}
