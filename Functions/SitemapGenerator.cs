namespace Functions;

using System.Data;
using System.Data.Common;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Extensions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;

public class SitemapGenerator
{
    private const int UrlsPerChunk = 50_000;
    private const string ChunkPrefix = "sitemaps/sitemap-";
    private const string ChunkSuffix = ".xml.gz";
    private const string IndexBlobName = "sitemap-index.xml";

    private readonly BlobServiceClient _blobServiceClient;
    private readonly DbConnection _dbConnection;
    private readonly Uri _baseUrl;

    public SitemapGenerator(
        DbConnection dbConnection,
        IAzureClientFactory<BlobServiceClient> blobServiceClientFactory,
        IConfiguration configuration)
    {
        _blobServiceClient = blobServiceClientFactory.CreateClient("crgolden");
        _dbConnection = dbConnection;
        _baseUrl = configuration.GetRequired<Uri>("ChurchesBaseUrl");
    }

    [Function(nameof(SitemapGenerator))]
    public async Task Run(
        [TimerTrigger("0 0 3 * * *")] TimerInfo timer,
        CancellationToken cancellationToken = default)
    {
        if (_dbConnection.State == ConnectionState.Closed)
        {
            await _dbConnection.OpenAsync(cancellationToken);
        }

        var blobContainerClient = _blobServiceClient.GetBlobContainerClient("$web");
        var chunkCount = await WriteChunksAsync(blobContainerClient, cancellationToken);
        await WriteIndexAsync(blobContainerClient, chunkCount, cancellationToken);
        await DeleteOrphanedChunksAsync(blobContainerClient, chunkCount, cancellationToken);
    }

    private static StringBuilder StartChunk()
    {
        var xml = new StringBuilder();
        xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        xml.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");
        return xml;
    }

    private static async Task UploadChunkAsync(
        BlobContainerClient blobContainerClient,
        int chunkNumber,
        StringBuilder xml,
        CancellationToken cancellationToken)
    {
        xml.AppendLine("</urlset>");

        using var gzipStream = new MemoryStream();
        await using (var gzip = new GZipStream(gzipStream, CompressionLevel.Optimal, leaveOpen: true))
        await using (var writer = new StreamWriter(gzip, Encoding.UTF8))
        {
            await writer.WriteAsync(xml.ToString());
        }

        gzipStream.Position = 0;
        var blobClient = blobContainerClient.GetBlobClient($"{ChunkPrefix}{chunkNumber}{ChunkSuffix}");
        var blobUploadOptions = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders
            {
                ContentType = "application/gzip",
            },
        };
        await blobClient.UploadAsync(gzipStream, blobUploadOptions, cancellationToken);
    }

    private static bool IsOrphanedChunk(string blobName, int chunkCount)
    {
        var numberSpan = blobName.AsSpan(ChunkPrefix.Length, blobName.Length - ChunkPrefix.Length - ChunkSuffix.Length);
        return int.TryParse(numberSpan, out var blobChunkNumber) && blobChunkNumber > chunkCount;
    }

    private static async Task DeleteOrphanedChunksAsync(
        BlobContainerClient blobContainerClient,
        int chunkCount,
        CancellationToken cancellationToken)
    {
        var orphanedBlobNames = await blobContainerClient
            .GetBlobsAsync(BlobTraits.None, BlobStates.None, ChunkPrefix, cancellationToken)
            .Select(blobItem => blobItem.Name)
            .Where(blobName => IsOrphanedChunk(blobName, chunkCount))
            .ToListAsync(cancellationToken);

        foreach (var blobName in orphanedBlobNames)
        {
            await blobContainerClient.GetBlobClient(blobName).DeleteIfExistsAsync(cancellationToken: cancellationToken);
        }
    }

    private async Task<int> WriteChunksAsync(BlobContainerClient blobContainerClient, CancellationToken cancellationToken)
    {
        var baseUrl = _baseUrl.ToString().TrimEnd('/');

        await using var dbCommand = _dbConnection.CreateCommand();
        dbCommand.CommandText = "SELECT [Slug] FROM [dbo].[Churches] WHERE [IsActive] = 1 ORDER BY [Slug] ASC";
        await using var reader = await dbCommand.ExecuteReaderAsync(cancellationToken);

        var chunkNumber = 1;
        var urlsInChunk = 0;
        var xml = StartChunk();
        xml.AppendLine($"  <url><loc>{baseUrl}/</loc><changefreq>daily</changefreq></url>");
        urlsInChunk++;

        while (await reader.ReadAsync(cancellationToken))
        {
            if (urlsInChunk == UrlsPerChunk)
            {
                await UploadChunkAsync(blobContainerClient, chunkNumber, xml, cancellationToken);
                chunkNumber++;
                urlsInChunk = 0;
                xml = StartChunk();
            }

            var slug = (string)reader[0];
            xml.AppendLine($"  <url><loc>{baseUrl}/churches/{slug}</loc><changefreq>weekly</changefreq></url>");
            urlsInChunk++;
        }

        await UploadChunkAsync(blobContainerClient, chunkNumber, xml, cancellationToken);
        return chunkNumber;
    }

    private async Task WriteIndexAsync(BlobContainerClient blobContainerClient, int chunkCount, CancellationToken cancellationToken)
    {
        var baseUrl = _baseUrl.ToString().TrimEnd('/');
        var lastmod = DateTime.UtcNow.ToString("yyyy-MM-dd");

        var xml = new StringBuilder();
        xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        xml.AppendLine("<sitemapindex xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");
        for (var i = 1; i <= chunkCount; i++)
        {
            xml.AppendLine("  <sitemap>");
            xml.AppendLine($"    <loc>{baseUrl}/{ChunkPrefix}{i}{ChunkSuffix}</loc>");
            xml.AppendLine($"    <lastmod>{lastmod}</lastmod>");
            xml.AppendLine("  </sitemap>");
        }

        xml.AppendLine("</sitemapindex>");

        var blobClient = blobContainerClient.GetBlobClient(IndexBlobName);
        var blobUploadOptions = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders
            {
                ContentType = "application/xml",
            },
        };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml.ToString()));
        await blobClient.UploadAsync(stream, blobUploadOptions, cancellationToken);
    }
}
