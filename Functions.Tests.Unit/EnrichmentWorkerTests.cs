namespace Functions.Tests.Unit;

using System.ClientModel;
using System.Globalization;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Churches;
using Churches.Extraction;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Moq;
using OpenAI.Responses;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class EnrichmentWorkerTests
{
    private const int RetryableDeliveryCount = 1;

    private const int ExhaustedDeliveryCount = 3;

    [Fact]
    public void Constructor_WhenOpenAIModelNotConfigured_Throws()
    {
        // Arrange
        var openAI = new Mock<ResponsesClient>(MockBehavior.Strict);
        var busFactory = new Mock<IAzureClientFactory<ServiceBusClient>>(MockBehavior.Strict);
        busFactory.Setup(f => f.CreateClient(AzureClientNames.Crgolden)).Returns(Mock.Of<ServiceBusClient>());
        var blobFactory = new Mock<IAzureClientFactory<BlobServiceClient>>(MockBehavior.Strict);
        blobFactory.Setup(f => f.CreateClient(AzureClientNames.Crgolden)).Returns(Mock.Of<BlobServiceClient>());
        var config = new ConfigurationBuilder().Build();

        // Act
        var exception = Record.Exception(() =>
            new EnrichmentWorker(openAI.Object, busFactory.Object, blobFactory.Object, config));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
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
            .Setup(a => a.DeadLetterMessageAsync(message, null, DeadLetterReasons.MalformedPayload, null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        openAI.VerifyNoOtherCalls();
        geocodingSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(
            a => a.DeadLetterMessageAsync(message, null, DeadLetterReasons.MalformedPayload, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_WhenOpenAIFailsAndDeliveryCountLow_AbandonsForRetry()
    {
        // Arrange
        var openAI = FailingOpenAI();
        var (worker, geocodingSender) = BuildWorker(openAI);
        var payload = new EnrichmentRequest(Guid.NewGuid(), NewChurchUrl(), BlobPath: null, NewPartial());
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromObjectAsJson(payload),
            deliveryCount: RetryableDeliveryCount);
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions
            .Setup(a => a.AbandonMessageAsync(message, It.IsAny<IDictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        geocodingSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(
            a => a.AbandonMessageAsync(message, It.IsAny<IDictionary<string, object>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_WhenOpenAIFailsAndDeliveryCountHigh_DegradesAndCompletes()
    {
        // Arrange
        var partialCity = TestValues.NewCity();
        var partial = new EnrichmentPartialData(TestValues.NewChurchName(), partialCity, TestValues.NewStateCode(), TestValues.NewZip());
        var openAI = FailingOpenAI();
        var (worker, geocodingSender) = BuildWorker(openAI);
        var payload = new EnrichmentRequest(Guid.NewGuid(), NewChurchUrl(), BlobPath: null, partial);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromObjectAsJson(payload),
            deliveryCount: ExhaustedDeliveryCount);
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        geocodingSender.Verify(
            s => s.SendMessageAsync(
                It.Is<ServiceBusMessage>(m => m.Body.ToString().Contains(partialCity, StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void TryParseEnrichment_CleanJsonAllFieldsValid_MapsEveryField()
    {
        // Arrange
        var enrichedName = TestValues.NewChurchName();
        var enrichedCity = TestValues.NewCity();
        var enrichedLanguage = TestValues.NewLanguageName();
        var enrichedDenomination = TestValues.NewDenominationName();
        var enrichedWorshipStyle = Random.Shared.Next(1, 6);
        var json = EnrichmentJson(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [EnrichmentResponseFields.CanonicalName] = enrichedName,
            [EnrichmentResponseFields.City] = enrichedCity,
            [EnrichmentResponseFields.State] = TestValues.NewStateCode(),
            [EnrichmentResponseFields.Zip] = TestValues.NewZip(),
            [EnrichmentResponseFields.WorshipStyle] = enrichedWorshipStyle,
            [EnrichmentResponseFields.PrimaryLanguage] = enrichedLanguage,
            [EnrichmentResponseFields.Denomination] = enrichedDenomination,
            [EnrichmentResponseFields.AcceptsLgbtq] = true,
            [EnrichmentResponseFields.WheelchairAccessible] = false,
            [EnrichmentResponseFields.HasNursery] = true,
            [EnrichmentResponseFields.HasYouthProgram] = false,
        });

        // Act
        var result = EnrichmentWorker.TryParseEnrichment(json, NewPartial());

        // Assert
        Assert.Equal(enrichedName, result.CanonicalName);
        Assert.Equal(enrichedCity, result.City);
        Assert.Equal(enrichedWorshipStyle, result.WorshipStyle);
        Assert.Equal(enrichedLanguage, result.PrimaryLanguage);
        Assert.Equal(enrichedDenomination, result.Denomination);
        Assert.True(result.AcceptsLGBTQ);
        Assert.False(result.WheelchairAccessible);
        Assert.True(result.HasNursery);
        Assert.False(result.HasYouthProgram);
    }

    [Fact]
    public void TryParseEnrichment_BlankPrimaryLanguage_DefaultsToEnglish()
    {
        // Arrange
        var json = EnrichmentJson(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [EnrichmentResponseFields.CanonicalName] = TestValues.NewChurchName(),
            [EnrichmentResponseFields.PrimaryLanguage] = string.Empty,
        });

        // Act
        var result = EnrichmentWorker.TryParseEnrichment(json, NewPartial());

        // Assert
        Assert.Equal(ChurchDefaults.PrimaryLanguage, result.PrimaryLanguage);
    }

    [Fact]
    public void TryParseEnrichment_BlankCanonicalName_FallsBackToPartial()
    {
        // Arrange
        var partial = NewPartial();
        var enrichedCity = TestValues.NewCity();
        var json = EnrichmentJson(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [EnrichmentResponseFields.CanonicalName] = "   ",
            [EnrichmentResponseFields.City] = enrichedCity,
        });

        // Act
        var result = EnrichmentWorker.TryParseEnrichment(json, partial);

        // Assert
        Assert.Equal(partial.CanonicalName, result.CanonicalName);
        Assert.Equal(enrichedCity, result.City);
    }

    [Fact]
    public void EnrichmentAttributes_DenominationAndWorshipStyle_AreEmitted()
    {
        // Arrange
        var denomination = TestValues.NewDenominationName();
        var worshipStyle = Random.Shared.Next(1, 6);
        var enriched = new EnrichedData(
            TestValues.NewChurchName(),
            TestValues.NewCity(),
            TestValues.NewStateCode(),
            TestValues.NewZip(),
            worshipStyle,
            ChurchDefaults.PrimaryLanguage,
            null,
            null,
            null,
            null,
            denomination,
            [],
            [],
            []);

        // Act
        var attributes = EnrichmentWorker.EnrichmentAttributes(enriched);

        // Assert
        Assert.Contains(attributes, a =>
            string.Equals(a.Key, ChurchAttributeKeys.Denomination, StringComparison.Ordinal)
            && string.Equals(a.Value, denomination, StringComparison.Ordinal)
            && string.Equals(a.Source, ChurchImportSources.Enrichment, StringComparison.Ordinal));
        Assert.Contains(attributes, a =>
            string.Equals(a.Key, ChurchAttributeKeys.WorshipStyle, StringComparison.Ordinal)
            && string.Equals(a.Value, worshipStyle.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            && string.Equals(a.Source, ChurchImportSources.Enrichment, StringComparison.Ordinal));
    }

    [Fact]
    public void EnrichmentAttributes_NoSignals_ReturnsEmpty()
    {
        // Arrange
        var enriched = new EnrichedData(
            TestValues.NewChurchName(),
            null,
            null,
            null,
            ChurchWorshipStyles.Unknown,
            ChurchDefaults.PrimaryLanguage,
            null,
            null,
            null,
            null,
            null,
            [],
            [],
            []);

        // Act
        var attributes = EnrichmentWorker.EnrichmentAttributes(enriched);

        // Assert
        Assert.Empty(attributes);
    }

    [Fact]
    public void TryParseEnrichment_ServiceSchedules_AreParsed()
    {
        // Arrange
        var firstDay = (byte)Random.Shared.Next(0, 3);
        var firstStartTime = TestValues.NewServiceTime();
        var firstDescription = TestValues.NewServiceDescription();
        var json = EnrichmentJson(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [EnrichmentResponseFields.CanonicalName] = TestValues.NewChurchName(),
            [EnrichmentResponseFields.ServiceSchedules] = new[]
            {
                ScheduleObject(firstDay, firstStartTime, firstDescription),
                ScheduleObject((byte)Random.Shared.Next(3, 7), TestValues.NewServiceTime(), TestValues.NewServiceDescription()),
            },
        });

        // Act
        var result = EnrichmentWorker.TryParseEnrichment(json, NewPartial());

        // Assert
        Assert.Equal(2, result.ServiceSchedules.Count);
        Assert.Equal(firstDay, result.ServiceSchedules[0].DayOfWeek);
        Assert.Equal(firstStartTime, result.ServiceSchedules[0].StartTime);
        Assert.Equal(firstDescription, result.ServiceSchedules[0].Description);
    }

    [Fact]
    public void TryParseEnrichment_ServiceSchedulesAbsent_ReturnsEmpty()
    {
        // Arrange
        var json = NamedOnlyEnrichmentJson();

        // Act
        var result = EnrichmentWorker.TryParseEnrichment(json, NewPartial());

        // Assert
        Assert.Empty(result.ServiceSchedules);
    }

    [Fact]
    public void TryParseEnrichment_Ministries_AreParsed()
    {
        // Arrange
        var describedName = TestValues.NewMinistryName();
        var describedDescription = TestValues.NewMinistryDescription();
        var json = EnrichmentJson(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [EnrichmentResponseFields.CanonicalName] = TestValues.NewChurchName(),
            [EnrichmentResponseFields.Ministries] = new[]
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [EnrichmentResponseFields.Name] = describedName,
                    [EnrichmentResponseFields.Description] = describedDescription,
                },
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [EnrichmentResponseFields.Name] = TestValues.NewMinistryName(),
                },
            },
        });

        // Act
        var result = EnrichmentWorker.TryParseEnrichment(json, NewPartial());

        // Assert
        Assert.Equal(2, result.Ministries.Count);
        Assert.Equal(describedName, result.Ministries[0].Name);
        Assert.Equal(describedDescription, result.Ministries[0].Description);
        Assert.Null(result.Ministries[1].Description);
    }

    [Fact]
    public void TryParseEnrichment_Campuses_AreParsed()
    {
        // Arrange
        var completeName = TestValues.NewCampusName();
        var completeCity = TestValues.NewCity();
        var json = EnrichmentJson(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [EnrichmentResponseFields.CanonicalName] = TestValues.NewChurchName(),
            [EnrichmentResponseFields.Campuses] = new[]
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [EnrichmentResponseFields.Name] = completeName,
                    [EnrichmentResponseFields.Street] = TestValues.NewStreet(),
                    [EnrichmentResponseFields.City] = completeCity,
                    [EnrichmentResponseFields.State] = TestValues.NewStateCode(),
                    [EnrichmentResponseFields.Zip] = TestValues.NewZip(),
                },
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [EnrichmentResponseFields.Name] = TestValues.NewCampusName(),
                    [EnrichmentResponseFields.City] = TestValues.NewCity(),
                },
            },
        });

        // Act
        var result = EnrichmentWorker.TryParseEnrichment(json, NewPartial());

        // Assert
        var parsedCampus = Assert.Single(result.Campuses);
        Assert.Equal(completeName, parsedCampus.Name);
        Assert.Equal(completeCity, parsedCampus.City);
    }

    [Fact]
    public void TryParseEnrichment_DenominationAbsent_ReturnsNull()
    {
        // Act
        var result = EnrichmentWorker.TryParseEnrichment(NamedOnlyEnrichmentJson(), NewPartial());

        // Assert
        Assert.Null(result.Denomination);
    }

    [Fact]
    public void TryParseEnrichment_JsonWrappedInProse_SlicesBracesAndParses()
    {
        // Arrange
        var enrichedName = TestValues.NewChurchName();
        var innerJson = EnrichmentJson(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [EnrichmentResponseFields.CanonicalName] = enrichedName,
        });
        var prose = $"Here is the data:\n```json\n{innerJson}\n```";

        // Act
        var result = EnrichmentWorker.TryParseEnrichment(prose, NewPartial());

        // Assert
        Assert.Equal(enrichedName, result.CanonicalName);
    }

    [Fact]
    public void TryParseEnrichment_NoOpeningBrace_FallsBackToPartial()
    {
        // Arrange
        var partial = NewPartial();

        // Act
        var result = EnrichmentWorker.TryParseEnrichment(NewProseWithoutJson(), partial);

        // Assert
        Assert.Equal(partial.CanonicalName, result.CanonicalName);
        Assert.Equal(partial.City, result.City);
    }

    [Fact]
    public void TryParseEnrichment_OpeningBraceNoClose_FallsBackToPartial()
    {
        // Arrange
        var partial = NewPartial();

        // Act
        var result = EnrichmentWorker.TryParseEnrichment($"{{ {NewProseWithoutJson()}", partial);

        // Assert
        Assert.Equal(partial.CanonicalName, result.CanonicalName);
    }

    [Fact]
    public void TryParseEnrichment_MalformedJsonInsideBraces_FallsBackToPartial()
    {
        // Arrange
        var partial = NewPartial();

        // Act
        var result = EnrichmentWorker.TryParseEnrichment($"{{{NewProseWithoutJson()}}}", partial);

        // Assert
        Assert.Equal(partial.CanonicalName, result.CanonicalName);
        Assert.Equal(ChurchWorshipStyles.Unknown, result.WorshipStyle);
        Assert.Equal(ChurchDefaults.PrimaryLanguage, result.PrimaryLanguage);
    }

    [Fact]
    public void TryParseEnrichment_CanonicalNameWrongKind_FallsBackForThatField()
    {
        // Arrange
        var partial = NewPartial();
        var enrichedCity = TestValues.NewCity();
        var json = EnrichmentJson(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [EnrichmentResponseFields.CanonicalName] = Random.Shared.Next(1, 1000),
            [EnrichmentResponseFields.City] = enrichedCity,
        });

        // Act
        var result = EnrichmentWorker.TryParseEnrichment(json, partial);

        // Assert
        Assert.Equal(partial.CanonicalName, result.CanonicalName);
        Assert.Equal(enrichedCity, result.City);
    }

    [Fact]
    public void TryParseEnrichment_CityKeyAbsent_FallsBackForThatField()
    {
        // Arrange
        var partial = NewPartial();
        var enrichedName = TestValues.NewChurchName();
        var json = EnrichmentJson(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [EnrichmentResponseFields.CanonicalName] = enrichedName,
        });

        // Act
        var result = EnrichmentWorker.TryParseEnrichment(json, partial);

        // Assert
        Assert.Equal(enrichedName, result.CanonicalName);
        Assert.Equal(partial.City, result.City);
    }

    [Fact]
    public void TryParseEnrichment_AcceptsLgbtqTrue_ReturnsTrue()
    {
        // Arrange
        var json = EnrichmentJson(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [EnrichmentResponseFields.AcceptsLgbtq] = true,
        });

        // Act
        var result = EnrichmentWorker.TryParseEnrichment(json, NewPartial());

        // Assert
        Assert.True(result.AcceptsLGBTQ);
    }

    [Fact]
    public void TryParseEnrichment_AcceptsLgbtqFalse_ReturnsFalse()
    {
        // Arrange
        var json = EnrichmentJson(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [EnrichmentResponseFields.AcceptsLgbtq] = false,
        });

        // Act
        var result = EnrichmentWorker.TryParseEnrichment(json, NewPartial());

        // Assert
        Assert.False(result.AcceptsLGBTQ);
    }

    [Fact]
    public void TryParseEnrichment_AcceptsLgbtqNullLiteral_ReturnsNull()
    {
        // Arrange
        var json = EnrichmentJson(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [EnrichmentResponseFields.AcceptsLgbtq] = null,
        });

        // Act
        var result = EnrichmentWorker.TryParseEnrichment(json, NewPartial());

        // Assert
        Assert.Null(result.AcceptsLGBTQ);
    }

    [Fact]
    public void TryParseEnrichment_WorshipStyleAndLanguageAbsent_UseDefaults()
    {
        // Act
        var result = EnrichmentWorker.TryParseEnrichment(NamedOnlyEnrichmentJson(), NewPartial());

        // Assert
        Assert.Equal(ChurchWorshipStyles.Unknown, result.WorshipStyle);
        Assert.Equal(ChurchDefaults.PrimaryLanguage, result.PrimaryLanguage);
    }

    [Fact]
    public void BuildPageContent_HtmlIsNull_ReturnsNotAvailable()
    {
        // Act
        var content = EnrichmentWorker.BuildPageContent(null);

        // Assert
        Assert.Equal(ChurchDefaults.PageContentUnavailable, content);
    }

    [Fact]
    public void BuildPageContent_ShortHtml_ReturnedUnchanged()
    {
        // Arrange
        var html = $"<html><body>{TestValues.NewChurchName()}</body></html>";

        // Act
        var result = EnrichmentWorker.BuildPageContent(html);

        // Assert
        Assert.Equal(html, result);
    }

    [Fact]
    public void BuildPageContent_HtmlExceedsCap_IsTruncatedToCap()
    {
        // Arrange
        var htmlPadding = (char)Random.Shared.Next('a', 'z' + 1);
        var overlongHtml = new string(htmlPadding, EnrichmentWorker.MaxHtmlCharsInPrompt + Random.Shared.Next(1, 5000));

        // Act
        var result = EnrichmentWorker.BuildPageContent(overlongHtml);

        // Assert
        Assert.Equal(EnrichmentWorker.MaxHtmlCharsInPrompt, result.Length);
        Assert.Equal(overlongHtml[..EnrichmentWorker.MaxHtmlCharsInPrompt], result);
    }

    private static Mock<ResponsesClient> FailingOpenAI()
    {
        var openAI = new Mock<ResponsesClient>(MockBehavior.Strict);
        openAI
            .Setup(o => o.CreateResponseAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<ResponseItem>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ClientResultException($"failure{Guid.NewGuid():N}"));
        return openAI;
    }

    private static Dictionary<string, object?> ScheduleObject(byte dayOfWeek, string startTime, string description) =>
        new(StringComparer.Ordinal)
        {
            [EnrichmentResponseFields.DayOfWeek] = dayOfWeek,
            [EnrichmentResponseFields.StartTime] = startTime,
            [EnrichmentResponseFields.Description] = description,
        };

    private static string EnrichmentJson(Dictionary<string, object?> fields) => JsonSerializer.Serialize(fields);

    private static string NamedOnlyEnrichmentJson() =>
        EnrichmentJson(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [EnrichmentResponseFields.CanonicalName] = TestValues.NewChurchName(),
        });

    private static EnrichmentPartialData NewPartial() =>
        new(TestValues.NewChurchName(), TestValues.NewCity(), TestValues.NewStateCode(), TestValues.NewZip());

    private static (EnrichmentWorker Worker, Mock<ServiceBusSender> GeocodingSender) BuildWorker(Mock<ResponsesClient> openAI)
    {
        var geocodingSender = new Mock<ServiceBusSender>(MockBehavior.Strict);
        geocodingSender.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        geocodingSender.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var serviceBusClient = new Mock<ServiceBusClient>(MockBehavior.Strict);
        serviceBusClient.Setup(c => c.CreateSender(ChurchQueueNames.GeocodingRequests)).Returns(geocodingSender.Object);

        var busFactory = new Mock<IAzureClientFactory<ServiceBusClient>>(MockBehavior.Strict);
        busFactory.Setup(f => f.CreateClient(AzureClientNames.Crgolden)).Returns(serviceBusClient.Object);

        var blobFactory = new Mock<IAzureClientFactory<BlobServiceClient>>(MockBehavior.Strict);
        blobFactory.Setup(f => f.CreateClient(AzureClientNames.Crgolden)).Returns(Mock.Of<BlobServiceClient>());

        var configuredModel = $"model{Guid.NewGuid():N}";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([new(ChurchSettingKeys.OpenAIModel, configuredModel)])
            .Build();

        return (new EnrichmentWorker(openAI.Object, busFactory.Object, blobFactory.Object, config), geocodingSender);
    }

    private static string LowercaseToken(int length) =>
        string.Concat(Enumerable.Range(0, length).Select(_ => (char)Random.Shared.Next('a', 'z' + 1)));

    private static string NewChurchUrl() => $"https://{LowercaseToken(12)}.example";

    private static string NewProseWithoutJson() => $"no json here {LowercaseToken(10)}";
}
