namespace Functions.Tests.Unit;

using System.ClientModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Churches;
using Churches.Extraction;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Moq;
using OpenAI.Responses;
using TestSupport;
using static EnrichmentWorkerFixtureConstants;
using static TestSupport.MarkupSyntaxFixtureConstants;

[Trait("Category", "Unit")]
public sealed class EnrichmentWorkerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void Constructor_WhenOpenAIModelNotConfigured_Throws()
    {
        // Arrange
        var openAI = new Mock<ResponsesClient>(MockBehavior.Strict);
        var senders = FakeServiceBus.CreateSenders().Senders;
        var config = new ConfigurationBuilder().Build();

        // Act
        var exception = Record.Exception(() =>
            new EnrichmentWorker(openAI.Object, Mock.Of<IOpenAIRateLimiter>(), senders, config));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public async Task Run_WhenPayloadIsNull_DeadLettersMessageWithoutCallingOpenAI()
    {
        // Arrange
        var openAI = new Mock<ResponsesClient>(MockBehavior.Strict);
        var (worker, geocodingSender, _) = BuildWorker(openAI);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString(JsonResponse.NullLiteral));
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
        var (worker, geocodingSender, _) = BuildWorker(openAI);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: EnrichmentRequestBody(),
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
        var partial = new EnrichmentPartialData(TestValues.NewChurchName(), TestValues.NewStreet(), partialCity, TestValues.NewStateCode(), TestValues.NewZip());
        var openAI = FailingOpenAI();
        var (worker, geocodingSender, _) = BuildWorker(openAI);
        var payload = new EnrichmentRequest(TestValues.NewCrawlSourceId(), TestValues.NewWebsite(), PageText: null, partial);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromObjectAsJson(payload),
            deliveryCount: ExhaustedDeliveryCount);
        var actions = CompletingActions(message);

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
    public async Task Run_WhenTheRateGateIsFull_DefersTheSameRequestPastTheGatesWaitAndCompletesWithoutCallingOpenAI()
    {
        // Arrange
        var openAI = new Mock<ResponsesClient>(MockBehavior.Strict);
        var secondsUntilGateRoom = (double)TestValues.NewSecondsUntilTheWindowHasRoom();
        var (worker, geocodingSender, deferred) = BuildWorker(openAI, secondsUntilGateRoom);
        var body = EnrichmentRequestBody();
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: body);
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        openAI.VerifyNoOtherCalls();
        geocodingSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        var (deferredMessage, enqueueAt) = Assert.Single(deferred);
        Assert.Equal(body.ToString(), deferredMessage.Body.ToString());
        Assert.Equal(1, deferredMessage.ApplicationProperties[EnrichmentWorker.GateDeferralsProperty]);
        Assert.InRange(enqueueAt, Now.AddSeconds(secondsUntilGateRoom), Now.AddSeconds(secondsUntilGateRoom + RedisOpenAIRateLimiter.WindowSeconds));
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenTheRateGateStaysFullPastTheDeferralLimit_DegradesToThePartialDataAndCompletes()
    {
        // Arrange
        var openAI = new Mock<ResponsesClient>(MockBehavior.Strict);
        var partialCity = TestValues.NewCity();
        var partial = new EnrichmentPartialData(TestValues.NewChurchName(), TestValues.NewStreet(), partialCity, TestValues.NewStateCode(), TestValues.NewZip());
        var secondsUntilGateRoom = TestValues.NewSecondsUntilTheWindowHasRoom();
        var (worker, geocodingSender, deferred) = BuildWorker(openAI, secondsUntilGateRoom);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromObjectAsJson(new EnrichmentRequest(TestValues.NewCrawlSourceId(), TestValues.NewWebsite(), PageText: null, partial)),
            properties: new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [EnrichmentWorker.GateDeferralsProperty] = EnrichmentWorker.MaxGateDeferrals,
            });
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        openAI.VerifyNoOtherCalls();
        Assert.Empty(deferred);
        geocodingSender.Verify(
            s => s.SendMessageAsync(
                It.Is<ServiceBusMessage>(m => m.Body.ToString().Contains(partialCity, StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenOpenAIThrottles_DefersPastRetryAfterAndCompletesWithoutDegrading_EvenAtTheDeliveryCeiling()
    {
        // Arrange
        var retryAfterSeconds = TestValues.NewRetryAfterSeconds();
        var openAI = Throwing(ThrottledException(retryAfterSeconds));
        var (worker, geocodingSender, deferred) = BuildWorker(openAI);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: EnrichmentRequestBody(),
            deliveryCount: ExhaustedDeliveryCount);
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        geocodingSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        var (deferredMessage, enqueueAt) = Assert.Single(deferred);
        Assert.Equal(1, deferredMessage.ApplicationProperties[EnrichmentWorker.ThrottledAttemptsProperty]);
        Assert.InRange(enqueueAt, Now.AddSeconds(retryAfterSeconds), Now.AddSeconds(retryAfterSeconds + RedisOpenAIRateLimiter.WindowSeconds));
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenOpenAIThrottlesAtTheAttemptLimit_DegradesToThePartialDataAndCompletes()
    {
        // Arrange
        var partialCity = TestValues.NewCity();
        var partial = new EnrichmentPartialData(TestValues.NewChurchName(), TestValues.NewStreet(), partialCity, TestValues.NewStateCode(), TestValues.NewZip());
        var openAI = Throwing(ThrottledException(TestValues.NewRetryAfterSeconds()));
        var (worker, geocodingSender, deferred) = BuildWorker(openAI);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromObjectAsJson(new EnrichmentRequest(TestValues.NewCrawlSourceId(), TestValues.NewWebsite(), PageText: null, partial)),
            properties: new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [EnrichmentWorker.ThrottledAttemptsProperty] = EnrichmentWorker.MaxThrottledAttempts,
            });
        var actions = CompletingActions(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(deferred);
        geocodingSender.Verify(
            s => s.SendMessageAsync(
                It.Is<ServiceBusMessage>(m => m.Body.ToString().Contains(partialCity, StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_CapsOutputTokensAndAsksForMinimalReasoning_SoHiddenReasoningCannotRunUnbounded()
    {
        // Arrange
        CreateResponseOptions? sentOptions = null;
        var openAI = new Mock<ResponsesClient>(MockBehavior.Strict);
        openAI
            .Setup(o => o.CreateResponseAsync(It.IsAny<CreateResponseOptions>(), It.IsAny<CancellationToken>()))
            .Callback<CreateResponseOptions, CancellationToken>((options, _) => sentOptions = options)
            .ThrowsAsync(new ClientResultException(TestValues.NewErrorMessage()));
        var (worker, _, _) = BuildWorker(openAI);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: EnrichmentRequestBody(),
            deliveryCount: ExhaustedDeliveryCount);

        // Act
        await worker.Run(message, CompletingActions(message).Object, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(sentOptions);
        Assert.Equal(EnrichmentWorker.MaxOutputTokens, sentOptions.MaxOutputTokenCount);
        Assert.NotNull(sentOptions.ReasoningOptions);
        Assert.Equal(ResponseReasoningEffortLevel.Minimal, sentOptions.ReasoningOptions.ReasoningEffortLevel);
    }

    [Fact]
    public void RetryAfterSeconds_ReadsTheThrottledResponsesRetryAfterHeader()
    {
        // Arrange
        var retryAfterSeconds = TestValues.NewRetryAfterSeconds();

        // Act
        var seconds = EnrichmentWorker.RetryAfterSeconds(ThrottledException(retryAfterSeconds));

        // Assert
        Assert.Equal(retryAfterSeconds, seconds);
    }

    [Fact]
    public void RetryAfterSeconds_PrefersAzureOpenAIsMillisecondHeader_OverRetryAfter()
    {
        // Arrange
        var retryAfterMilliseconds = Random.Shared.Next(1, 600_000);
        var exception = ThrottledException(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [EnrichmentWorker.RetryAfterMillisecondsHeader] = retryAfterMilliseconds.ToString(CultureInfo.InvariantCulture),
            [EnrichmentWorker.RetryAfterHeader] = TestValues.NewRetryAfterSeconds().ToString(CultureInfo.InvariantCulture),
        });

        // Act
        var seconds = EnrichmentWorker.RetryAfterSeconds(exception);

        // Assert
        Assert.Equal(retryAfterMilliseconds / (double)TimeSpan.MillisecondsPerSecond, seconds);
    }

    [Fact]
    public void RetryAfterSeconds_WithoutAResponse_FallsBackToTheDefault()
    {
        // Act
        var seconds = EnrichmentWorker.RetryAfterSeconds(new ClientResultException(TestValues.NewErrorMessage()));

        // Assert
        Assert.Equal(EnrichmentWorker.DefaultThrottleRetrySeconds, seconds);
    }

    [Fact]
    public void DeferralDelaySeconds_NeverDefersBeforeTheEarliestMoment_AndSpreadsWithinADoublingWindow()
    {
        // Arrange
        var earliestSeconds = (double)TestValues.NewRetryAfterSeconds();
        var previousDeferrals = Random.Shared.Next(0, EnrichmentWorker.MaxBackoffDoublings);

        // Act
        var delay = EnrichmentWorker.DeferralDelaySeconds(earliestSeconds, previousDeferrals);

        // Assert
        Assert.InRange(delay, earliestSeconds, earliestSeconds + (RedisOpenAIRateLimiter.WindowSeconds * (1 << previousDeferrals)));
    }

    [Fact]
    public void DeferralDelaySeconds_StopsDoublingAtTheBackoffCap()
    {
        // Arrange
        var earliestSeconds = (double)TestValues.NewRetryAfterSeconds();
        var previousDeferrals = Random.Shared.Next(EnrichmentWorker.MaxBackoffDoublings, 1_000);

        // Act
        var delay = EnrichmentWorker.DeferralDelaySeconds(earliestSeconds, previousDeferrals);

        // Assert
        Assert.InRange(delay, earliestSeconds, earliestSeconds + (RedisOpenAIRateLimiter.WindowSeconds * (1 << EnrichmentWorker.MaxBackoffDoublings)));
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
            [EnrichmentResponseFields.PrimaryLanguage] = TestValues.NewBlankRun(),
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
            [EnrichmentResponseFields.CanonicalName] = TestValues.NewBlankRun(),
            [EnrichmentResponseFields.City] = enrichedCity,
        });

        // Act
        var result = EnrichmentWorker.TryParseEnrichment(json, partial);

        // Assert
        Assert.Equal(partial.CanonicalName, result.CanonicalName);
        Assert.Equal(enrichedCity, result.City);
    }

    [Fact]
    public void TryParseEnrichment_Street_IsMapped()
    {
        // Arrange
        var enrichedStreet = TestValues.NewStreet();
        var json = EnrichmentJson(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [EnrichmentResponseFields.CanonicalName] = TestValues.NewChurchName(),
            [EnrichmentResponseFields.Street] = enrichedStreet,
        });

        // Act
        var result = EnrichmentWorker.TryParseEnrichment(json, NewPartial());

        // Assert
        Assert.Equal(enrichedStreet, result.Street);
    }

    [Fact]
    public void TryParseEnrichment_StreetAbsent_FallsBackToTheExtractorsStreet()
    {
        // Arrange
        var partial = NewPartial();

        // Act
        var result = EnrichmentWorker.TryParseEnrichment(NamedOnlyEnrichmentJson(), partial);

        // Assert
        Assert.Equal(partial.Street, result.Street);
    }

    [Fact]
    public void EnrichmentAttributes_DenominationAndWorshipStyle_AreEmitted()
    {
        // Arrange
        var denomination = TestValues.NewDenominationName();
        var worshipStyle = Random.Shared.Next(1, 6);
        var enriched = new EnrichedData(
            TestValues.NewChurchName(),
            TestValues.NewStreet(),
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
        var firstDay = TestValues.NewEarlyWeekDayOfWeek();
        var firstStartTime = TestValues.NewServiceTime();
        var firstDescription = TestValues.NewServiceDescription();
        var secondDay = TestValues.NewLateWeekDayOfWeek();
        var secondStartTime = TestValues.NewServiceTime();
        var secondDescription = TestValues.NewServiceDescription();
        var json = EnrichmentJson(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [EnrichmentResponseFields.CanonicalName] = TestValues.NewChurchName(),
            [EnrichmentResponseFields.ServiceSchedules] = new[]
            {
                ScheduleObject(firstDay, firstStartTime, firstDescription),
                ScheduleObject(secondDay, secondStartTime, secondDescription),
            },
        });

        // Act
        var result = EnrichmentWorker.TryParseEnrichment(json, NewPartial());

        // Assert
        Assert.Equal(
            [new ServiceScheduleData(firstDay, firstStartTime, firstDescription), new ServiceScheduleData(secondDay, secondStartTime, secondDescription)],
            result.ServiceSchedules);
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
        var undescribedName = TestValues.NewMinistryName();
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
                    [EnrichmentResponseFields.Name] = undescribedName,
                },
            },
        });

        // Act
        var result = EnrichmentWorker.TryParseEnrichment(json, NewPartial());

        // Assert
        Assert.Equal([new MinistryData(describedName, describedDescription), new MinistryData(undescribedName, null)], result.Ministries);
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
        var prose = $"{TestValues.NewProseWithoutJson()}{LineBreak}{MarkdownJsonFenceOpening}{LineBreak}{innerJson}{LineBreak}{MarkdownFenceClosing}";

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
        var result = EnrichmentWorker.TryParseEnrichment(TestValues.NewProseWithoutJson(), partial);

        // Assert
        Assert.Equal(partial.CanonicalName, result.CanonicalName);
        Assert.Equal(partial.Street, result.Street);
        Assert.Equal(partial.City, result.City);
    }

    [Fact]
    public void TryParseEnrichment_OpeningBraceNoClose_FallsBackToPartial()
    {
        // Arrange
        var partial = NewPartial();

        // Act
        var result = EnrichmentWorker.TryParseEnrichment($"{{ {TestValues.NewProseWithoutJson()}", partial);

        // Assert
        Assert.Equal(partial.CanonicalName, result.CanonicalName);
    }

    [Fact]
    public void TryParseEnrichment_MalformedJsonInsideBraces_FallsBackToPartial()
    {
        // Arrange
        var partial = NewPartial();

        // Act
        var result = EnrichmentWorker.TryParseEnrichment($"{{{TestValues.NewProseWithoutJson()}}}", partial);

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
        var numericCanonicalName = Random.Shared.Next(1, 1000);
        var json = EnrichmentJson(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [EnrichmentResponseFields.CanonicalName] = numericCanonicalName,
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
    public void BuildPrompt_CarriesThePageTextTheExtractorForwarded()
    {
        // Arrange
        var pageText = TestValues.NewProseWithoutAPhoneNumber();
        var request = new EnrichmentRequest(TestValues.NewCrawlSourceId(), TestValues.NewWebsite(), pageText, NewPartial());

        // Act
        var prompt = EnrichmentWorker.BuildPrompt(request);

        // Assert
        Assert.Contains(pageText, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_WithoutForwardedPageText_UsesThePlaceholder()
    {
        // Arrange
        var request = new EnrichmentRequest(TestValues.NewCrawlSourceId(), TestValues.NewWebsite(), PageText: null, NewPartial());

        // Act
        var prompt = EnrichmentWorker.BuildPrompt(request);

        // Assert
        Assert.Contains(ChurchDefaults.PageContentUnavailable, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildPageContentAsync_KeepsTheVisibleText_AndDropsScriptsStylesAndMarkup()
    {
        // Arrange
        var firstParagraph = TestValues.NewChurchName();
        var secondParagraph = TestValues.NewCity();
        var scriptText = TestValues.NewLettersOnlyToken();
        var styleText = TestValues.NewLettersOnlyToken();
        var html = Markup.HtmlDocument(
            $"{Markup.Element(HtmlStyle, styleText)}{Markup.Element(HtmlParagraph, firstParagraph)}{LineBreak}  "
            + $"{Markup.Element(HtmlScript, scriptText)}{LineBreak}{Markup.Element(HtmlParagraph, secondParagraph)}");

        // Act
        var content = await EnrichmentWorker.BuildPageContentAsync(html);

        // Assert
        Assert.Equal($"{firstParagraph} {secondParagraph}", content);
    }

    [Fact]
    public async Task BuildPageContentAsync_TextExceedsCap_IsTruncatedToCap()
    {
        // Arrange
        var overlongText = new string(TestValues.NewPaddingChar(), EnrichmentWorker.MaxPageTextCharsInPrompt + TestValues.NewOverflowMargin());

        // Act
        var content = await EnrichmentWorker.BuildPageContentAsync(Markup.HtmlDocument(Markup.Element(HtmlParagraph, overlongText)));

        // Assert
        Assert.Equal(overlongText[..EnrichmentWorker.MaxPageTextCharsInPrompt], content);
    }

    private static Mock<ResponsesClient> FailingOpenAI() => Throwing(new ClientResultException(TestValues.NewErrorMessage()));

    private static Mock<ResponsesClient> Throwing(ClientResultException exception)
    {
        var openAI = new Mock<ResponsesClient>(MockBehavior.Strict);
        openAI
            .Setup(o => o.CreateResponseAsync(It.IsAny<CreateResponseOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);
        return openAI;
    }

    private static ClientResultException ThrottledException(int retryAfterSeconds) =>
        ThrottledException(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [EnrichmentWorker.RetryAfterHeader] = retryAfterSeconds.ToString(CultureInfo.InvariantCulture),
        });

    private static ClientResultException ThrottledException(IReadOnlyDictionary<string, string> headers) =>
        new(TestValues.NewErrorMessage(), new StubPipelineResponse((int)HttpStatusCode.TooManyRequests, headers));

    private static Mock<ServiceBusMessageActions> CompletingActions(ServiceBusReceivedMessage message)
    {
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return actions;
    }

    private static BinaryData EnrichmentRequestBody() =>
        BinaryData.FromObjectAsJson(new EnrichmentRequest(TestValues.NewCrawlSourceId(), TestValues.NewWebsite(), PageText: null, NewPartial()));

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
        new(TestValues.NewChurchName(), TestValues.NewStreet(), TestValues.NewCity(), TestValues.NewStateCode(), TestValues.NewZip());

    private static (EnrichmentWorker Worker, Mock<ServiceBusSender> GeocodingSender, List<(ServiceBusMessage Message, DateTimeOffset EnqueueAt)> Deferred) BuildWorker(
        Mock<ResponsesClient> openAI,
        double? secondsUntilGateRoom = null)
    {
        var geocodingSender = new Mock<ServiceBusSender>(MockBehavior.Strict);
        geocodingSender.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        geocodingSender.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var deferred = new List<(ServiceBusMessage Message, DateTimeOffset EnqueueAt)>();
        var enrichmentSender = new Mock<ServiceBusSender>(MockBehavior.Strict);
        enrichmentSender
            .Setup(s => s.ScheduleMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns((ServiceBusMessage m, DateTimeOffset enqueueAt, CancellationToken _) =>
            {
                deferred.Add((m, enqueueAt));
                return Task.FromResult((long)deferred.Count);
            });
        var serviceBusClient = new Mock<ServiceBusClient>(MockBehavior.Strict);
        serviceBusClient.Setup(c => c.CreateSender(ChurchQueueNames.GeocodingRequests)).Returns(geocodingSender.Object);
        serviceBusClient.Setup(c => c.CreateSender(ChurchQueueNames.EnrichmentRequests)).Returns(enrichmentSender.Object);

        var rateLimiter = new Mock<IOpenAIRateLimiter>(MockBehavior.Strict);
        rateLimiter
            .Setup(l => l.TryAcquireAsync(EnrichmentWorker.EstimatedTokensPerRequest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(secondsUntilGateRoom);

        var busFactory = new Mock<IAzureClientFactory<ServiceBusClient>>(MockBehavior.Strict);
        busFactory.Setup(f => f.CreateClient(AzureClientNames.Crgolden)).Returns(serviceBusClient.Object);

        var configuredModel = TestValues.NewFieldValue();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([new(ChurchSettingKeys.OpenAIModel, configuredModel)])
            .Build();

        return (new EnrichmentWorker(openAI.Object, rateLimiter.Object, new ChurchQueueSenders(busFactory.Object), config, new FakeTimeProvider(Now)), geocodingSender, deferred);
    }
}
