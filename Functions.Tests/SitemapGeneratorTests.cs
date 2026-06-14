namespace Functions.Tests;

using Azure.Storage.Blobs;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Moq;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class SitemapGeneratorTests
{
    private readonly FakeDbConnection _connection = new();
    private readonly Mock<IAzureClientFactory<BlobServiceClient>> _blobFactoryMock = new(MockBehavior.Strict);

    public SitemapGeneratorTests()
    {
        _blobFactoryMock.Setup(f => f.CreateClient("crgolden")).Returns(new Mock<BlobServiceClient>(MockBehavior.Strict).Object);
    }

    [Fact]
    public void Constructor_WhenBaseUrlNotConfigured_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Create(baseUrl: null));
    }

    [Fact]
    public void Constructor_WhenBaseUrlConfigured_Succeeds()
    {
        var ex = Record.Exception(() => Create("https://localhost:7135"));
        Assert.Null(ex);
    }

    private SitemapGenerator Create(string? baseUrl) =>
        new(_connection,
            _blobFactoryMock.Object,
            new ConfigurationBuilder().AddInMemoryCollection([new("ChurchesBaseUrl", baseUrl)]).Build());
}
