namespace Functions.Tests.Unit;

using System.Net;
using AngleSharp;
using AngleSharp.Dom;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Churches;
using Churches.Extraction;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Moq;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class ExtractorWorkerTests
{
    [Fact]
    public async Task ExtractPhone_ItempropTelephonePresent_ReturnsItempropValue()
    {
        // Arrange
        var itempropPhone = TestValues.NewParenthesizedPhoneNumber();
        var bodyPhone = TestValues.NewParenthesizedPhoneNumber();
        var doc = await ParseHtmlAsync(
            $"{Itemprop(MicrodataProperties.Telephone, $"  {itempropPhone}  ")}<p>{bodyPhone}</p>");

        // Act
        var phone = ExtractorWorker.ExtractPhone(doc);

        // Assert
        Assert.Equal(itempropPhone, phone);
    }

    [Fact]
    public async Task ExtractPhone_NoItempropButBodyHasMatch_ReturnsRegexMatch()
    {
        // Arrange
        var bodyPhone = TestValues.NewPhoneNumber();
        var doc = await ParseHtmlAsync(
            $"<p>{TestValues.NewLettersOnlyToken()} {bodyPhone} {TestValues.NewLettersOnlyToken()}</p>");

        // Act
        var phone = ExtractorWorker.ExtractPhone(doc);

        // Assert
        Assert.Equal(bodyPhone, phone);
    }

    [Fact]
    public async Task ExtractPhone_NoItempropNoMatch_ReturnsNull()
    {
        // Arrange
        var doc = await ParseHtmlAsync($"<p>{TestValues.NewProseWithoutAPhoneNumber()}</p>");

        // Act
        var phone = ExtractorWorker.ExtractPhone(doc);

        // Assert
        Assert.Null(phone);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_FullMicrodata_ScoresHighWithItempropName()
    {
        // Arrange
        var churchName = TestValues.NewChurchName();
        var city = TestValues.NewCity();
        var state = TestValues.NewStateCode();
        var zip = TestValues.NewZip();
        var websiteUrl = TestValues.NewWebsite();
        var html = FullMicrodataHtml(churchName, city, state, zip, TestValues.NewPhoneNumber());

        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync(html, websiteUrl);

        // Assert
        Assert.Equal(churchName, result.CanonicalName);
        Assert.Equal(city, result.City);
        Assert.Equal(state, result.State);
        Assert.Equal(zip, result.Zip);
        Assert.Equal(
            (ExtractorWorker.AddressFieldConfidenceWeight * 4) + ExtractorWorker.ContactConfidenceWeight,
            result.Confidence);
        Assert.Equal(websiteUrl, result.Website);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_NoItempropNameButH1Present_NameFromH1()
    {
        // Arrange
        var headingName = TestValues.NewChurchName();

        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync($"<h1>{headingName}</h1>", TestValues.NewWebsite());

        // Assert
        Assert.Equal(headingName, result.CanonicalName);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_NoItempropNoH1ButTitlePresent_NameFromTitle()
    {
        // Arrange
        var titleName = TestValues.NewChurchName();
        var html = $"<html><head><title>{titleName}</title></head><body><p>{TestValues.NewProseWithoutAPhoneNumber()}</p></body></html>";

        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync(html, TestValues.NewWebsite());

        // Assert
        Assert.Equal(titleName, result.CanonicalName);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_BlankItempropName_FallsBackToH1()
    {
        // Arrange
        var headingName = TestValues.NewChurchName();
        var html = $"{Itemprop(MicrodataProperties.Name, "   ")}<h1>{headingName}</h1>";

        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync(html, TestValues.NewWebsite());

        // Assert
        Assert.Equal(headingName, result.CanonicalName);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_BlankEmailHref_EmailIsNull()
    {
        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync("<a href=\"mailto:\">email</a>", TestValues.NewWebsite());

        // Assert
        Assert.Null(result.EmailAddress);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_NoNameSource_NameIsBlankAndNotScored()
    {
        // Arrange
        var city = TestValues.NewCity();

        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync(
            Itemprop(MicrodataProperties.AddressLocality, city), TestValues.NewWebsite());

        // Assert
        Assert.Null(result.CanonicalName);
        Assert.Equal(city, result.City);
        Assert.Equal(ExtractorWorker.AddressFieldConfidenceWeight, result.Confidence);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_CityOnly_AddsCityScore()
    {
        // Arrange
        var city = TestValues.NewCity();

        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync(
            Itemprop(MicrodataProperties.AddressLocality, city), TestValues.NewWebsite());

        // Assert
        Assert.Equal(city, result.City);
        Assert.Equal(ExtractorWorker.AddressFieldConfidenceWeight, result.Confidence);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_StateOnly_AddsStateScore()
    {
        // Arrange
        var state = TestValues.NewStateCode();

        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync(
            Itemprop(MicrodataProperties.AddressRegion, state), TestValues.NewWebsite());

        // Assert
        Assert.Equal(state, result.State);
        Assert.Equal(ExtractorWorker.AddressFieldConfidenceWeight, result.Confidence);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_ZipOnly_AddsZipScore()
    {
        // Arrange
        var zip = TestValues.NewZip();

        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync(
            Itemprop(MicrodataProperties.PostalCode, zip), TestValues.NewWebsite());

        // Assert
        Assert.Equal(zip, result.Zip);
        Assert.Equal(ExtractorWorker.AddressFieldConfidenceWeight, result.Confidence);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_PhoneOnlyNoEmail_AddsContactScore()
    {
        // Arrange
        var phone = TestValues.NewPhoneNumber();

        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync(
            Itemprop(MicrodataProperties.Telephone, phone), TestValues.NewWebsite());

        // Assert
        Assert.Equal(phone, result.PhoneNumber);
        Assert.Equal(ExtractorWorker.ContactConfidenceWeight, result.Confidence);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_EmailOnlyNoPhone_AddsContactScore()
    {
        // Arrange
        var emailAddress = TestValues.NewEmailAddress();

        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync(
            $"<a href=\"mailto:{emailAddress}\">email</a>", TestValues.NewWebsite());

        // Assert
        Assert.Equal(emailAddress, result.EmailAddress);
        Assert.Equal(ExtractorWorker.ContactConfidenceWeight, result.Confidence);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_NeitherPhoneNorEmail_NoContactScore()
    {
        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync(
            Itemprop(MicrodataProperties.AddressLocality, TestValues.NewCity()), TestValues.NewWebsite());

        // Assert
        Assert.Null(result.PhoneNumber);
        Assert.Null(result.EmailAddress);
        Assert.Equal(ExtractorWorker.AddressFieldConfidenceWeight, result.Confidence);
    }

    [Fact]
    public async Task Run_PayloadIsNull_DeadLettersMessage()
    {
        // Arrange
        var (worker, geocodingSender, enrichmentSender) = BuildWorker(html: null);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString("null"));
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions
            .Setup(a => a.DeadLetterMessageAsync(message, null, DeadLetterReasons.MalformedPayload, null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        geocodingSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        enrichmentSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(
            a => a.DeadLetterMessageAsync(message, null, DeadLetterReasons.MalformedPayload, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_BlankBlobPath_CompletesWithoutExtraction()
    {
        // Arrange
        var (worker, geocodingSender, enrichmentSender) = BuildWorker(html: null);
        var payload = new ExtractionRequest(Guid.NewGuid(), string.Empty, TestValues.NewWebsite());
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromObjectAsJson(payload));
        var actions = CompletingActionsFor(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        geocodingSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        enrichmentSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_BlobNotFound_CompletesWithoutSendingAnything()
    {
        // Arrange
        var (worker, geocodingSender, enrichmentSender) = BuildWorker(html: null);
        var message = ExtractionMessage();
        var actions = CompletingActionsFor(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        geocodingSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        enrichmentSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_HighConfidenceWithCity_SendsGeocodingRequest()
    {
        // Arrange
        var html = FullMicrodataHtml(TestValues.NewChurchName(), TestValues.NewCity(), TestValues.NewStateCode(), TestValues.NewZip(), TestValues.NewPhoneNumber());
        var (worker, geocodingSender, enrichmentSender) = BuildWorker(html);
        var message = ExtractionMessage();
        var actions = CompletingActionsFor(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        geocodingSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        enrichmentSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_LowConfidence_SendsEnrichmentRequestCarryingThePageText()
    {
        // Arrange
        var headingName = TestValues.NewChurchName();
        var (worker, geocodingSender, enrichmentSender) = BuildWorker($"<h1>{headingName}</h1>");
        var message = ExtractionMessage();
        var actions = CompletingActionsFor(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        geocodingSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        var sent = Assert.IsType<ServiceBusMessage>(Assert.Single(enrichmentSender.Invocations).Arguments[0]);
        var request = sent.Body.ToObjectFromJson<EnrichmentRequest>();
        Assert.NotNull(request);
        Assert.Equal(headingName, request.PageText);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_LowConfidence_ForwardsTheExtractedStreetForGeocoding()
    {
        // Arrange
        var street = TestValues.NewStreet();
        var html = string.Join(
            '\n',
            $"<h1>{TestValues.NewChurchName()}</h1>",
            Itemprop(MicrodataProperties.StreetAddress, street));
        var (worker, _, enrichmentSender) = BuildWorker(html);
        var message = ExtractionMessage();
        var actions = CompletingActionsFor(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        var sent = Assert.IsType<ServiceBusMessage>(Assert.Single(enrichmentSender.Invocations).Arguments[0]);
        var request = sent.Body.ToObjectFromJson<EnrichmentRequest>();
        Assert.NotNull(request);
        Assert.Equal(street, request.Partial.Street);
    }

    [Fact]
    public async Task Run_HighConfidenceButNoCity_SendsEnrichmentRequest()
    {
        // Arrange
        var html = string.Join(
            '\n',
            $"<h1>{TestValues.NewChurchName()}</h1>",
            Itemprop(MicrodataProperties.AddressRegion, TestValues.NewStateCode()),
            Itemprop(MicrodataProperties.PostalCode, TestValues.NewZip()),
            Itemprop(MicrodataProperties.Telephone, TestValues.NewPhoneNumber()));
        var (worker, geocodingSender, enrichmentSender) = BuildWorker(html);
        var message = ExtractionMessage();
        var actions = CompletingActionsFor(message);

        // Act
        await worker.Run(message, actions.Object, TestContext.Current.CancellationToken);

        // Assert
        geocodingSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        enrichmentSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        actions.Verify(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static string Itemprop(string property, string value) =>
        $"<span itemprop=\"{property}\">{value}</span>";

    private static string FullMicrodataHtml(string name, string city, string state, string zip, string phone) =>
        string.Join(
            '\n',
            Itemprop(MicrodataProperties.Name, name),
            Itemprop(MicrodataProperties.AddressLocality, city),
            Itemprop(MicrodataProperties.AddressRegion, state),
            Itemprop(MicrodataProperties.PostalCode, zip),
            Itemprop(MicrodataProperties.Telephone, phone));

    private static ServiceBusReceivedMessage ExtractionMessage() =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromObjectAsJson(
                new ExtractionRequest(Guid.NewGuid(), TestValues.NewBlobPath(), TestValues.NewWebsite())));

    private static Mock<ServiceBusMessageActions> CompletingActionsFor(ServiceBusReceivedMessage message)
    {
        var actions = new Mock<ServiceBusMessageActions>(MockBehavior.Strict);
        actions.Setup(a => a.CompleteMessageAsync(message, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return actions;
    }

    private static async Task<IDocument> ParseHtmlAsync(string html)
    {
        var context = BrowsingContext.New(Configuration.Default);
        return await context.OpenAsync(req => req.Content(html));
    }

    private static (ExtractorWorker Worker, Mock<ServiceBusSender> GeocodingSender, Mock<ServiceBusSender> EnrichmentSender) BuildWorker(string? html)
    {
        var response = Mock.Of<Response>();

        var blobClient = new Mock<BlobClient>(MockBehavior.Strict);
        if (html is null)
        {
            blobClient
                .Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new RequestFailedException((int)HttpStatusCode.NotFound, TestValues.NewErrorMessage()));
        }
        else
        {
            blobClient
                .Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response.FromValue(
                    BlobsModelFactory.BlobDownloadResult(content: BinaryData.FromString(html)),
                    response));
        }

        var containerClient = new Mock<BlobContainerClient>(MockBehavior.Strict);
        containerClient.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blobClient.Object);

        var blobServiceClient = new Mock<BlobServiceClient>(MockBehavior.Strict);
        blobServiceClient.Setup(s => s.GetBlobContainerClient(BlobContainerNames.Churches)).Returns(containerClient.Object);

        var blobFactory = new Mock<IAzureClientFactory<BlobServiceClient>>(MockBehavior.Strict);
        blobFactory.Setup(f => f.CreateClient(AzureClientNames.Crgolden)).Returns(blobServiceClient.Object);

        var geocodingSender = new Mock<ServiceBusSender>(MockBehavior.Strict);
        geocodingSender.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        geocodingSender.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var enrichmentSender = new Mock<ServiceBusSender>(MockBehavior.Strict);
        enrichmentSender.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        enrichmentSender.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var serviceBusClient = new Mock<ServiceBusClient>(MockBehavior.Strict);
        serviceBusClient.Setup(c => c.CreateSender(ChurchQueueNames.GeocodingRequests)).Returns(geocodingSender.Object);
        serviceBusClient.Setup(c => c.CreateSender(ChurchQueueNames.EnrichmentRequests)).Returns(enrichmentSender.Object);

        var serviceBusFactory = new Mock<IAzureClientFactory<ServiceBusClient>>(MockBehavior.Strict);
        serviceBusFactory.Setup(f => f.CreateClient(AzureClientNames.Crgolden)).Returns(serviceBusClient.Object);

        return (new ExtractorWorker(blobFactory.Object, new ChurchQueueSenders(serviceBusFactory.Object)), geocodingSender, enrichmentSender);
    }
}
