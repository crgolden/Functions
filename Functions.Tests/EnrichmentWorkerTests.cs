namespace Functions.Tests;

using Azure.Messaging.ServiceBus;
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
        var config = new ConfigurationBuilder().Build();

        // Act / Assert
        Assert.Throws<InvalidOperationException>(() =>
            new EnrichmentWorker(openAI.Object, busFactory.Object, config));
    }

    [Fact]
    public async Task Run_WhenPayloadIsNull_CompletesWithoutCallingOpenAI()
    {
        // Arrange
        var openAI = new Mock<ResponsesClient>(MockBehavior.Strict);
        var (worker, geocodingSender) = BuildWorker(openAI);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString("null"));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        openAI.VerifyNoOtherCalls();
        geocodingSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- TryParseEnrichment (pure, internal static) ---
    [Fact]
    public void TryParseEnrichment_CleanJsonAllFieldsValid_MapsEveryField()
    {
        // Arrange
        const string json = """
            {"canonicalName":"Grace Church","city":"Phoenix","state":"AZ","zip":"85001",
             "worshipStyle":2,"primaryLanguage":"Spanish","acceptsLGBTQ":true,
             "wheelchairAccessible":false,"hasNursery":true,"hasYouthProgram":false}
            """;

        // Act
        var result = EnrichmentWorker.TryParseEnrichment(json, Partial());

        // Assert
        Assert.Equal("Grace Church", result.CanonicalName);
        Assert.Equal("Phoenix", result.City);
        Assert.Equal(2, result.WorshipStyle);
        Assert.Equal("Spanish", result.PrimaryLanguage);
        Assert.True(result.AcceptsLGBTQ);
        Assert.False(result.WheelchairAccessible);
        Assert.True(result.HasNursery);
        Assert.False(result.HasYouthProgram);
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

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([new("OpenAIModel", "gpt-4.1-mini")])
            .Build();

        return (new EnrichmentWorker(openAI.Object, busFactory.Object, config), geocodingSender);
    }
}
