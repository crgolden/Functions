namespace Functions.Tests;

using System.Data;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Moq;
using OpenAI.Responses;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class EnrichmentWorkerTests
{
    [Fact]
    public void Constructor_WhenOpenAIModelNotConfigured_Throws()
    {
        // Arrange
        var openAI = new Mock<ResponsesClient>(MockBehavior.Strict);
        var config = new ConfigurationBuilder().Build();

        // Act / Assert
        Assert.Throws<InvalidOperationException>(() =>
            new EnrichmentWorker(openAI.Object, new FakeDbConnection(), config));
    }

    [Fact]
    public async Task Run_WhenPayloadIsNull_CompletesWithoutCallingOpenAI()
    {
        // Arrange
        var openAI = new Mock<ResponsesClient>(MockBehavior.Strict);
        var worker = BuildWorker(new FakeDbConnection(), openAI);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString("null"));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        openAI.VerifyNoOtherCalls();
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

    // --- UpsertChurchAsync (internal instance; FakeDbConnection) ---
    [Fact]
    public async Task UpsertChurchAsync_ExistingChurchConnectionClosed_OpensAndUpdates()
    {
        // Arrange — lookup returns an existing ChurchId, so the row is updated; connection starts Closed
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithScalarResult(Guid.CreateVersion7(DateTimeOffset.UtcNow)));
        var worker = BuildWorker(connection, new Mock<ResponsesClient>(MockBehavior.Strict));

        // Act
        await worker.UpsertChurchAsync(Guid.NewGuid(), PopulatedData(), TestContext.Current.CancellationToken);

        // Assert — connection was opened; lookup + UPDATE executed (no INSERT/link)
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(2, connection.ExecutedCommands.Count);
        Assert.Contains("UPDATE [dbo].[Churches]", connection.ExecutedCommands[1].CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpsertChurchAsync_NewChurchConnectionOpen_InsertsAndLinks()
    {
        // Arrange — lookup returns null (no existing ChurchId) so the row is new; connection already Open
        var connection = new FakeDbConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.Enqueue(FakeDbCommand.WithScalarResult(null));
        var worker = BuildWorker(connection, new Mock<ResponsesClient>(MockBehavior.Strict));

        // Act
        await worker.UpsertChurchAsync(Guid.NewGuid(), PopulatedData(), TestContext.Current.CancellationToken);

        // Assert — lookup + INSERT into Churches + link UPDATE on CrawlSources
        Assert.Equal(3, connection.ExecutedCommands.Count);
        Assert.Contains("INSERT INTO [dbo].[Churches]", connection.ExecutedCommands[1].CommandText, StringComparison.Ordinal);
        Assert.Contains("UPDATE [dbo].[CrawlSources]", connection.ExecutedCommands[2].CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpsertChurchAsync_NewChurchNullOptionals_BindsDbNull()
    {
        // Arrange — null strings and null nullable-bools must all coalesce to DBNull
        var connection = new FakeDbConnection();
        var worker = BuildWorker(connection, new Mock<ResponsesClient>(MockBehavior.Strict));
        var data = new EnrichedData(null, null, null, null, 0, "English", null, null, null, null);

        // Act
        await worker.UpsertChurchAsync(Guid.NewGuid(), data, TestContext.Current.CancellationToken);

        // Assert
        var insert = connection.ExecutedCommands[1];
        Assert.Equal(DBNull.Value, insert.Parameters["@Name"].Value);
        Assert.Equal(DBNull.Value, insert.Parameters["@Lgbtq"].Value);
        Assert.Equal(DBNull.Value, insert.Parameters["@Youth"].Value);
    }

    [Fact]
    public async Task UpsertChurchAsync_NewChurchPopulatedOptionals_BindsValues()
    {
        // Arrange
        var connection = new FakeDbConnection();
        var worker = BuildWorker(connection, new Mock<ResponsesClient>(MockBehavior.Strict));

        // Act
        await worker.UpsertChurchAsync(Guid.NewGuid(), PopulatedData(), TestContext.Current.CancellationToken);

        // Assert — populated optionals bind their values; slug joins name-city-state
        var insert = connection.ExecutedCommands[1];
        Assert.Equal("Grace Church", insert.Parameters["@Name"].Value);
        Assert.Equal("grace-church-phoenix-az", insert.Parameters["@Slug"].Value);
        Assert.True(insert.Parameters["@Lgbtq"].Value is true);
        Assert.True(insert.Parameters["@Youth"].Value is false);
    }

    private static EnrichmentPartialData Partial() =>
        new("PartialName", "PartialCity", "PartialState", "00000");

    private static EnrichedData PopulatedData() =>
        new(
            CanonicalName: "Grace Church",
            City: "Phoenix",
            State: "AZ",
            Zip: "85001",
            WorshipStyle: 2,
            PrimaryLanguage: "Spanish",
            AcceptsLGBTQ: true,
            WheelchairAccessible: false,
            HasNursery: true,
            HasYouthProgram: false);

    private static EnrichmentWorker BuildWorker(FakeDbConnection connection, Mock<ResponsesClient> openAI)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([new("OpenAIModel", "gpt-4.1-mini")])
            .Build();
        return new EnrichmentWorker(openAI.Object, connection, config);
    }
}
