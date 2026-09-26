namespace Functions.Tests.Unit;

using System.Net;
using AngleSharp;
using AngleSharp.Dom;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Functions.Churches;
using Functions.Churches.Extraction;
using Functions.Tests.Unit.TestSupport;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Moq;
using static Functions.Tests.Unit.TestSupport.MarkupSyntaxFixtureConstants;

[Trait("Category", "Unit")]
public sealed class ExtractorWorkerTests
{
    [Fact]
    public async Task ExtractPhone_ItempropTelephonePresent_ReturnsItempropValue()
    {
        // Arrange
        var itempropPhone = Generated.NewParenthesizedPhoneNumber();
        var bodyPhone = Generated.NewParenthesizedPhoneNumber();
        var doc = await ParseHtmlAsync(
            $"{Itemprop(MicrodataProperties.Telephone, $"  {itempropPhone}  ")}{Markup.Element(HtmlParagraph, bodyPhone)}");

        // Act
        var phone = ExtractorWorker.ExtractPhone(doc);

        // Assert
        Assert.Equal(itempropPhone, phone);
    }

    [Fact]
    public async Task ExtractPhone_NoItempropButBodyHasMatch_ReturnsRegexMatch()
    {
        // Arrange
        var bodyPhone = Generated.NewPhoneNumber();
        var doc = await ParseHtmlAsync(
            Markup.Element(HtmlParagraph, $"{Generated.NewLettersOnlyToken()} {bodyPhone} {Generated.NewLettersOnlyToken()}"));

        // Act
        var phone = ExtractorWorker.ExtractPhone(doc);

        // Assert
        Assert.Equal(bodyPhone, phone);
    }

    [Fact]
    public async Task ExtractPhone_NoItempropNoMatch_ReturnsNull()
    {
        // Arrange
        var doc = await ParseHtmlAsync(Markup.Element(HtmlParagraph, Generated.NewProseWithoutAPhoneNumber()));

        // Act
        var phone = ExtractorWorker.ExtractPhone(doc);

        // Assert
        Assert.Null(phone);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_FullMicrodata_ScoresHighWithItempropName()
    {
        // Arrange
        var churchName = Generated.NewChurchName();
        var city = Generated.NewCity();
        var state = Generated.NewStateCodeText();
        var zip = Generated.NewZip();
        var websiteUrl = Generated.NewWebsite();
        string[] scoredAddressFields = [churchName, city, state, zip];
        var html = FullMicrodataHtml(churchName, city, state, zip, Generated.NewPhoneNumber());

        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync(html, websiteUrl);

        // Assert
        Assert.Equal(churchName, result.CanonicalName);
        Assert.Equal(city, result.City);
        Assert.Equal(state, result.State);
        Assert.Equal(zip, result.Zip);
        Assert.Equal(
            (ExtractorWorker.AddressFieldConfidenceWeight * scoredAddressFields.Length) + ExtractorWorker.ContactConfidenceWeight,
            result.Confidence);
        Assert.Equal(websiteUrl, result.Website);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_NoItempropNameButH1Present_NameFromH1()
    {
        // Arrange
        var headingName = Generated.NewChurchName();

        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync(Markup.Element(HtmlHeading, headingName), Generated.NewWebsite());

        // Assert
        Assert.Equal(headingName, result.CanonicalName);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_NoItempropNoH1ButTitlePresent_NameFromTitle()
    {
        // Arrange
        var titleName = Generated.NewChurchName();
        var head = Markup.Element(HtmlHead, Markup.Element(HtmlTitle, titleName));
        var body = Markup.Element(HtmlBody, Markup.Element(HtmlParagraph, Generated.NewProseWithoutAPhoneNumber()));
        var html = Markup.Element(HtmlRoot, head + body);

        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync(html, Generated.NewWebsite());

        // Assert
        Assert.Equal(titleName, result.CanonicalName);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_BlankItempropName_FallsBackToH1()
    {
        // Arrange
        var headingName = Generated.NewChurchName();
        var html = Itemprop(MicrodataProperties.Name, Generated.NewBlankRun()) + Markup.Element(HtmlHeading, headingName);

        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync(html, Generated.NewWebsite());

        // Assert
        Assert.Equal(headingName, result.CanonicalName);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_BlankEmailHref_EmailIsNull()
    {
        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync(
            Markup.ElementWithAttribute(HtmlAnchor, HtmlHrefAttribute, MailtoScheme, Generated.NewLettersOnlyToken()),
            Generated.NewWebsite());

        // Assert
        Assert.Null(result.EmailAddress);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_NoNameSource_NameIsBlankAndNotScored()
    {
        // Arrange
        var city = Generated.NewCity();

        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync(
            Itemprop(MicrodataProperties.AddressLocality, city), Generated.NewWebsite());

        // Assert
        Assert.Null(result.CanonicalName);
        Assert.Equal(city, result.City);
        Assert.Equal(ExtractorWorker.AddressFieldConfidenceWeight, result.Confidence);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_CityOnly_AddsCityScore()
    {
        // Arrange
        var city = Generated.NewCity();

        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync(
            Itemprop(MicrodataProperties.AddressLocality, city), Generated.NewWebsite());

        // Assert
        Assert.Equal(city, result.City);
        Assert.Equal(ExtractorWorker.AddressFieldConfidenceWeight, result.Confidence);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_StateOnly_AddsStateScore()
    {
        // Arrange
        var state = Generated.NewStateCodeText();

        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync(
            Itemprop(MicrodataProperties.AddressRegion, state), Generated.NewWebsite());

        // Assert
        Assert.Equal(state, result.State);
        Assert.Equal(ExtractorWorker.AddressFieldConfidenceWeight, result.Confidence);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_ZipOnly_AddsZipScore()
    {
        // Arrange
        var zip = Generated.NewZip();

        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync(
            Itemprop(MicrodataProperties.PostalCode, zip), Generated.NewWebsite());

        // Assert
        Assert.Equal(zip, result.Zip);
        Assert.Equal(ExtractorWorker.AddressFieldConfidenceWeight, result.Confidence);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_PhoneOnlyNoEmail_AddsContactScore()
    {
        // Arrange
        var phone = Generated.NewPhoneNumber();

        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync(
            Itemprop(MicrodataProperties.Telephone, phone), Generated.NewWebsite());

        // Assert
        Assert.Equal(phone, result.PhoneNumber);
        Assert.Equal(ExtractorWorker.ContactConfidenceWeight, result.Confidence);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_EmailOnlyNoPhone_AddsContactScore()
    {
        // Arrange
        var emailAddress = Generated.NewEmailAddress();

        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync(
            Markup.ElementWithAttribute(HtmlAnchor, HtmlHrefAttribute, $"{MailtoScheme}{emailAddress}", Generated.NewLettersOnlyToken()),
            Generated.NewWebsite());

        // Assert
        Assert.Equal(emailAddress, result.EmailAddress);
        Assert.Equal(ExtractorWorker.ContactConfidenceWeight, result.Confidence);
    }

    [Fact]
    public async Task ExtractFromHtmlAsync_NeitherPhoneNorEmail_NoContactScore()
    {
        // Act
        var result = await ExtractorWorker.ExtractFromHtmlAsync(
            Itemprop(MicrodataProperties.AddressLocality, Generated.NewCity()), Generated.NewWebsite());

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
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString(JsonResponse.NullLiteral));
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
        var payload = new ExtractionRequest(Generated.NewCrawlSourceId(), string.Empty, Generated.NewWebsite());
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
        var html = FullMicrodataHtml(Generated.NewChurchName(), Generated.NewCity(), Generated.NewStateCodeText(), Generated.NewZip(), Generated.NewPhoneNumber());
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
        var headingName = Generated.NewChurchName();
        var (worker, geocodingSender, enrichmentSender) = BuildWorker(Markup.Element(HtmlHeading, headingName));
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
        var street = Generated.NewStreet();
        var html = string.Join(
            '\n',
            Markup.Element(HtmlHeading, Generated.NewChurchName()),
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
            Markup.Element(HtmlHeading, Generated.NewChurchName()),
            Itemprop(MicrodataProperties.AddressRegion, Generated.NewStateCodeText()),
            Itemprop(MicrodataProperties.PostalCode, Generated.NewZip()),
            Itemprop(MicrodataProperties.Telephone, Generated.NewPhoneNumber()));
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
        Markup.ElementWithAttribute(HtmlSpan, HtmlItempropAttribute, property, value);

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
                new ExtractionRequest(Generated.NewCrawlSourceId(), Generated.NewBlobPath(), Generated.NewWebsite())));

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
                .ThrowsAsync(new RequestFailedException((int)HttpStatusCode.NotFound, Generated.NewErrorMessage()));
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
