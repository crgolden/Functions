namespace Functions.Tests.Unit;

using System.Data;
using System.IO.Compression;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Moq;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class SitemapGeneratorTests
{
    private const string BaseUrl = "https://crgolden-churches.azurewebsites.net";

    [Fact]
    public void Constructor_WhenBaseUrlNotConfigured_Throws()
    {
        var blobFactoryMock = new Mock<IAzureClientFactory<BlobServiceClient>>(MockBehavior.Strict);
        blobFactoryMock.Setup(f => f.CreateClient("crgolden")).Returns(new Mock<BlobServiceClient>(MockBehavior.Strict).Object);

        Assert.Throws<InvalidOperationException>(() => new SitemapGenerator(
            new FakeDbConnection(),
            blobFactoryMock.Object,
            new ConfigurationBuilder().AddInMemoryCollection([new("ChurchesBaseUrl", null)]).Build()));
    }

    [Fact]
    public void Constructor_WhenBaseUrlConfigured_Succeeds()
    {
        var blobFactoryMock = new Mock<IAzureClientFactory<BlobServiceClient>>(MockBehavior.Strict);
        blobFactoryMock.Setup(f => f.CreateClient("crgolden")).Returns(new Mock<BlobServiceClient>(MockBehavior.Strict).Object);

        var ex = Record.Exception(() => new SitemapGenerator(
            new FakeDbConnection(),
            blobFactoryMock.Object,
            new ConfigurationBuilder().AddInMemoryCollection([new("ChurchesBaseUrl", BaseUrl)]).Build()));

        Assert.Null(ex);
    }

    [Fact]
    public async Task Run_WhenChunkExactlyFull_UploadsSingleGzippedChunkAndIndexReferencingChurchesBaseUrl()
    {
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(BuildSlugTable(49_999)));

        var (containerMock, uploads, _) = BuildContainer([]);
        var sitemapGenerator = BuildGenerator(connection, containerMock);

        await sitemapGenerator.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        var chunkUploads = uploads.Where(u => u.BlobName.StartsWith("sitemaps/sitemap-", StringComparison.Ordinal)).ToList();
        var indexUpload = Assert.Single(uploads, u => u.BlobName == "sitemap-index.xml");

        var chunkUpload = Assert.Single(chunkUploads);
        Assert.Equal("sitemaps/sitemap-1.xml.gz", chunkUpload.BlobName);
        Assert.Equal("application/gzip", chunkUpload.ContentType);
        var chunkXml = Gunzip(chunkUpload.Bytes);
        Assert.Equal(50_000, CountOccurrences(chunkXml, "<url>"));
        Assert.Contains($"<loc>{BaseUrl}/</loc>", chunkXml, StringComparison.Ordinal);
        Assert.Contains($"<loc>{BaseUrl}/churches/church-049998</loc>", chunkXml, StringComparison.Ordinal);

        Assert.Equal("application/xml", indexUpload.ContentType);
        var indexXml = Encoding.UTF8.GetString(indexUpload.Bytes);
        Assert.Equal(1, CountOccurrences(indexXml, "<sitemap>"));
        Assert.Contains($"<loc>{BaseUrl}/sitemaps/sitemap-1.xml.gz</loc>", indexXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_WhenChunkOverflowsByOne_UploadsTwoChunksAndIndexReferencingBoth()
    {
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(BuildSlugTable(50_000)));

        var (containerMock, uploads, _) = BuildContainer([]);
        var sitemapGenerator = BuildGenerator(connection, containerMock);

        await sitemapGenerator.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        var chunkUploads = uploads.Where(u => u.BlobName.StartsWith("sitemaps/sitemap-", StringComparison.Ordinal))
            .OrderBy(u => u.BlobName, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(2, chunkUploads.Count);

        var firstChunkXml = Gunzip(chunkUploads[0].Bytes);
        Assert.Equal(50_000, CountOccurrences(firstChunkXml, "<url>"));

        var secondChunkXml = Gunzip(chunkUploads[1].Bytes);
        Assert.Equal(1, CountOccurrences(secondChunkXml, "<url>"));
        Assert.Contains($"<loc>{BaseUrl}/churches/church-049999</loc>", secondChunkXml, StringComparison.Ordinal);

        var indexXml = Encoding.UTF8.GetString(Assert.Single(uploads, u => u.BlobName == "sitemap-index.xml").Bytes);
        Assert.Equal(2, CountOccurrences(indexXml, "<sitemap>"));
        Assert.Contains($"<loc>{BaseUrl}/sitemaps/sitemap-1.xml.gz</loc>", indexXml, StringComparison.Ordinal);
        Assert.Contains($"<loc>{BaseUrl}/sitemaps/sitemap-2.xml.gz</loc>", indexXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_WhenNoActiveChurches_UploadsSingleChunkWithHomepageOnly()
    {
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(BuildSlugTable(0)));

        var (containerMock, uploads, _) = BuildContainer([]);
        var sitemapGenerator = BuildGenerator(connection, containerMock);

        await sitemapGenerator.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        var chunkUpload = Assert.Single(uploads, u => u.BlobName == "sitemaps/sitemap-1.xml.gz");
        var chunkXml = Gunzip(chunkUpload.Bytes);
        Assert.Equal(1, CountOccurrences(chunkXml, "<url>"));
        Assert.Contains($"<loc>{BaseUrl}/</loc>", chunkXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_WhenPreviousRunHadMoreChunks_DeletesOrphanedChunksBeyondCurrentCount()
    {
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(BuildSlugTable(0)));

        var (containerMock, _, deleted) = BuildContainer(
            ["sitemaps/sitemap-1.xml.gz", "sitemaps/sitemap-2.xml.gz", "sitemaps/sitemap-3.xml.gz"]);
        var sitemapGenerator = BuildGenerator(connection, containerMock);

        await sitemapGenerator.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        Assert.Equal(["sitemaps/sitemap-2.xml.gz", "sitemaps/sitemap-3.xml.gz"], deleted.OrderBy(n => n, StringComparer.Ordinal));
    }

    private static SitemapGenerator BuildGenerator(FakeDbConnection connection, Mock<BlobContainerClient> containerMock)
    {
        var blobServiceClientMock = new Mock<BlobServiceClient>(MockBehavior.Strict);
        blobServiceClientMock.Setup(s => s.GetBlobContainerClient("$web")).Returns(containerMock.Object);

        var blobFactoryMock = new Mock<IAzureClientFactory<BlobServiceClient>>(MockBehavior.Strict);
        blobFactoryMock.Setup(f => f.CreateClient("crgolden")).Returns(blobServiceClientMock.Object);

        return new SitemapGenerator(
            connection,
            blobFactoryMock.Object,
            new ConfigurationBuilder().AddInMemoryCollection([new("ChurchesBaseUrl", BaseUrl)]).Build());
    }

    private static (Mock<BlobContainerClient> Container, List<CapturedUpload> Uploads, List<string> Deleted) BuildContainer(
        IReadOnlyList<string> existingChunkBlobNames)
    {
        var uploads = new List<CapturedUpload>();
        var deleted = new List<string>();
        var blobClientMocks = new Dictionary<string, Mock<BlobClient>>();

        Mock<BlobClient> GetOrCreateBlobClientMock(string blobName)
        {
            if (blobClientMocks.TryGetValue(blobName, out var existingMock))
            {
                return existingMock;
            }

            var blobClientMock = new Mock<BlobClient>(MockBehavior.Strict);
            blobClientMock
                .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
                .Callback<Stream, BlobUploadOptions, CancellationToken>((stream, options, _) =>
                {
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    uploads.Add(new CapturedUpload(blobName, options.HttpHeaders?.ContentType ?? string.Empty, buffer.ToArray()));
                })
                .ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());
            blobClientMock
                .Setup(b => b.DeleteIfExistsAsync(It.IsAny<DeleteSnapshotsOption>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
                .Callback(() => deleted.Add(blobName))
                .ReturnsAsync(Mock.Of<Response<bool>>());
            blobClientMocks[blobName] = blobClientMock;
            return blobClientMock;
        }

        var containerMock = new Mock<BlobContainerClient>(MockBehavior.Strict);
        containerMock.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns<string>(name => GetOrCreateBlobClientMock(name).Object);
        containerMock
            .Setup(c => c.GetBlobsAsync(It.IsAny<BlobTraits>(), It.IsAny<BlobStates>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(new FakeAsyncPageable<BlobItem>(existingChunkBlobNames.Select(n => BlobsModelFactory.BlobItem(name: n)).ToList()));

        return (containerMock, uploads, deleted);
    }

    private static DataTable BuildSlugTable(int count)
    {
        var table = new DataTable();
        table.Columns.Add("Slug", typeof(string));
        for (var i = 0; i < count; i++)
        {
            table.Rows.Add($"church-{i:D6}");
        }

        return table;
    }

    private static string Gunzip(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}

internal sealed record CapturedUpload(string BlobName, string ContentType, byte[] Bytes);
