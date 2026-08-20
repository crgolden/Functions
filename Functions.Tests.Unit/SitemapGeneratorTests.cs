namespace Functions.Tests.Unit;

using System.Data;
using System.IO.Compression;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Churches;
using Churches.Publishing;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Moq;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class SitemapGeneratorTests
{
    private static readonly string BaseUrl = $"https://{Guid.NewGuid():N}.example";

    private static readonly string SlugPrefix = $"church{Guid.NewGuid():N}-";

    private static readonly DateTimeOffset ChurchUpdatedAt =
        DateTimeOffset.UtcNow.AddDays(-Random.Shared.Next(1, 500));

    [Fact]
    public void Constructor_WhenBaseUrlNotConfigured_Throws()
    {
        // Arrange
        var blobFactoryMock = new Mock<IAzureClientFactory<BlobServiceClient>>(MockBehavior.Strict);
        blobFactoryMock
            .Setup(f => f.CreateClient(AzureClientNames.Crgolden))
            .Returns(new Mock<BlobServiceClient>(MockBehavior.Strict).Object);

        // Act
        var exception = Record.Exception(() => new SitemapGenerator(
            new FakeDbConnection(),
            blobFactoryMock.Object,
            new ConfigurationBuilder().AddInMemoryCollection([new(ChurchSettingKeys.ChurchesBaseUrl, null)]).Build()));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void Constructor_WhenBaseUrlConfigured_Succeeds()
    {
        // Arrange
        var blobFactoryMock = new Mock<IAzureClientFactory<BlobServiceClient>>(MockBehavior.Strict);
        blobFactoryMock
            .Setup(f => f.CreateClient(AzureClientNames.Crgolden))
            .Returns(new Mock<BlobServiceClient>(MockBehavior.Strict).Object);

        // Act
        var exception = Record.Exception(() => new SitemapGenerator(
            new FakeDbConnection(),
            blobFactoryMock.Object,
            new ConfigurationBuilder().AddInMemoryCollection([new(ChurchSettingKeys.ChurchesBaseUrl, BaseUrl)]).Build()));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public async Task Run_WhenChunkExactlyFull_UploadsSingleGzippedChunkAndIndexReferencingChurchesBaseUrl()
    {
        // Arrange
        var churchesFillingOneChunk = SitemapGenerator.UrlsPerChunk - 1;
        var lastChurchIndex = churchesFillingOneChunk - 1;
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(BuildSlugTable(churchesFillingOneChunk)));

        var (containerMock, uploads, _) = BuildContainer([]);
        var sitemapGenerator = BuildGenerator(connection, containerMock);

        // Act
        await sitemapGenerator.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        var chunkUploads = uploads.Where(u => u.BlobName.StartsWith(SitemapGenerator.ChunkPrefix, StringComparison.Ordinal)).ToList();
        var indexUpload = Assert.Single(uploads, u => string.Equals(u.BlobName, SitemapGenerator.IndexBlobName, StringComparison.Ordinal));

        var chunkUpload = Assert.Single(chunkUploads);
        Assert.Equal(ChunkBlobName(1), chunkUpload.BlobName);
        Assert.Equal(SitemapGenerator.GzipContentType, chunkUpload.ContentType);
        var chunkXml = Gunzip(chunkUpload.Bytes);
        Assert.Equal(SitemapGenerator.UrlsPerChunk, CountOccurrences(chunkXml, SitemapGenerator.UrlElement));
        Assert.Contains($"<loc>{BaseUrl}/</loc><lastmod>{DateTimeOffset.UtcNow:yyyy-MM-dd}</lastmod>", chunkXml, StringComparison.Ordinal);
        Assert.Contains(
            $"<loc>{BaseUrl}/churches/{Slug(lastChurchIndex)}</loc><lastmod>{ChurchUpdatedAt:yyyy-MM-dd}</lastmod>",
            chunkXml,
            StringComparison.Ordinal);

        Assert.Equal(SitemapGenerator.XmlContentType, indexUpload.ContentType);
        var indexXml = Encoding.UTF8.GetString(indexUpload.Bytes);
        Assert.Equal(1, CountOccurrences(indexXml, SitemapGenerator.SitemapElement));
        Assert.Contains($"<loc>{BaseUrl}/{ChunkBlobName(1)}</loc>", indexXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_WhenChunkOverflowsByOne_UploadsTwoChunksAndIndexReferencingBoth()
    {
        // Arrange
        var churchesOverflowingOneChunk = SitemapGenerator.UrlsPerChunk;
        var overflowChurchIndex = churchesOverflowingOneChunk - 1;
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(BuildSlugTable(churchesOverflowingOneChunk)));

        var (containerMock, uploads, _) = BuildContainer([]);
        var sitemapGenerator = BuildGenerator(connection, containerMock);

        // Act
        await sitemapGenerator.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        var chunkUploads = uploads.Where(u => u.BlobName.StartsWith(SitemapGenerator.ChunkPrefix, StringComparison.Ordinal))
            .OrderBy(u => u.BlobName, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(2, chunkUploads.Count);

        var firstChunkXml = Gunzip(chunkUploads[0].Bytes);
        Assert.Equal(SitemapGenerator.UrlsPerChunk, CountOccurrences(firstChunkXml, SitemapGenerator.UrlElement));

        var secondChunkXml = Gunzip(chunkUploads[1].Bytes);
        Assert.Equal(1, CountOccurrences(secondChunkXml, SitemapGenerator.UrlElement));
        Assert.Contains($"<loc>{BaseUrl}/churches/{Slug(overflowChurchIndex)}</loc>", secondChunkXml, StringComparison.Ordinal);

        var indexUpload = Assert.Single(uploads, u => string.Equals(u.BlobName, SitemapGenerator.IndexBlobName, StringComparison.Ordinal));
        var indexXml = Encoding.UTF8.GetString(indexUpload.Bytes);
        Assert.Equal(2, CountOccurrences(indexXml, SitemapGenerator.SitemapElement));
        Assert.Contains($"<loc>{BaseUrl}/{ChunkBlobName(1)}</loc>", indexXml, StringComparison.Ordinal);
        Assert.Contains($"<loc>{BaseUrl}/{ChunkBlobName(2)}</loc>", indexXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_WhenNoActiveChurches_UploadsSingleChunkWithHomepageOnly()
    {
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(BuildSlugTable(0)));

        var (containerMock, uploads, _) = BuildContainer([]);
        var sitemapGenerator = BuildGenerator(connection, containerMock);

        // Act
        await sitemapGenerator.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        var chunkUpload = Assert.Single(uploads, u => string.Equals(u.BlobName, ChunkBlobName(1), StringComparison.Ordinal));
        var chunkXml = Gunzip(chunkUpload.Bytes);
        Assert.Equal(1, CountOccurrences(chunkXml, SitemapGenerator.UrlElement));
        Assert.Contains($"<loc>{BaseUrl}/</loc><lastmod>{DateTimeOffset.UtcNow:yyyy-MM-dd}</lastmod>", chunkXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_WhenPreviousRunHadMoreChunks_DeletesOrphanedChunksBeyondCurrentCount()
    {
        // Arrange
        var connection = new FakeDbConnection();
        connection.Enqueue(FakeDbCommand.WithReader(BuildSlugTable(0)));

        var (containerMock, _, deleted) = BuildContainer(
            [ChunkBlobName(1), ChunkBlobName(2), ChunkBlobName(3)]);
        var sitemapGenerator = BuildGenerator(connection, containerMock);

        // Act
        await sitemapGenerator.Run(new TimerInfo(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([ChunkBlobName(2), ChunkBlobName(3)], deleted.OrderBy(n => n, StringComparer.Ordinal));
    }

    private static string ChunkBlobName(int chunkNumber) => $"{SitemapGenerator.ChunkPrefix}{chunkNumber}{SitemapGenerator.ChunkSuffix}";

    private static string Slug(int churchIndex) => $"{SlugPrefix}{churchIndex:D6}";

    private static SitemapGenerator BuildGenerator(FakeDbConnection connection, Mock<BlobContainerClient> containerMock)
    {
        var blobServiceClientMock = new Mock<BlobServiceClient>(MockBehavior.Strict);
        blobServiceClientMock.Setup(s => s.GetBlobContainerClient(BlobContainerNames.StaticWebsite)).Returns(containerMock.Object);

        var blobFactoryMock = new Mock<IAzureClientFactory<BlobServiceClient>>(MockBehavior.Strict);
        blobFactoryMock.Setup(f => f.CreateClient(AzureClientNames.Crgolden)).Returns(blobServiceClientMock.Object);

        return new SitemapGenerator(
            connection,
            blobFactoryMock.Object,
            new ConfigurationBuilder().AddInMemoryCollection([new(ChurchSettingKeys.ChurchesBaseUrl, BaseUrl)]).Build());
    }

    private static (Mock<BlobContainerClient> Container, List<CapturedUpload> Uploads, List<string> Deleted) BuildContainer(
        IReadOnlyList<string> existingChunkBlobNames)
    {
        var uploads = new List<CapturedUpload>();
        var deleted = new List<string>();
        var blobClientMocks = new Dictionary<string, Mock<BlobClient>>(StringComparer.Ordinal);

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
                    uploads.Add(new CapturedUpload(blobName, options.HttpHeaders?.ContentType, buffer.ToArray()));
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
        table.Columns.Add("UpdatedAt", typeof(DateTime));
        for (var i = 0; i < count; i++)
        {
            table.Rows.Add(Slug(i), ChurchUpdatedAt.UtcDateTime);
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

internal sealed record CapturedUpload(string BlobName, string? ContentType, byte[] Bytes);
