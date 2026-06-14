namespace Functions;

using System.Data;
using System.Data.Common;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Extensions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;

public class SitemapGenerator
{
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
        var baseUrl = _baseUrl.ToString().TrimEnd('/');
        if (_dbConnection.State == ConnectionState.Closed)
        {
            await _dbConnection.OpenAsync(cancellationToken);
        }

        await using var dbCommand = _dbConnection.CreateCommand();
        dbCommand.CommandText = "SELECT [Slug] FROM [dbo].[Churches] WHERE [IsActive] = 1 ORDER BY [CreatedAt] DESC";
        await using var reader = await dbCommand.ExecuteReaderAsync(cancellationToken);

        var xml = new StringBuilder();
        xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        xml.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");
        xml.AppendLine($"  <url><loc>{baseUrl}/</loc><changefreq>daily</changefreq></url>");
        while (await reader.ReadAsync(cancellationToken))
        {
            var slug = (string)reader[0];
            xml.AppendLine($"  <url><loc>{baseUrl}/churches/{slug}</loc><changefreq>weekly</changefreq></url>");
        }

        xml.AppendLine("</urlset>");

        var blobContainerClient = _blobServiceClient.GetBlobContainerClient("$web");
        var blobClient = blobContainerClient.GetBlobClient("sitemap.xml");
        var blobUploadOptions = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders
            {
                ContentType = "application/xml"
            }
        };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml.ToString()));
        await blobClient.UploadAsync(stream, blobUploadOptions, cancellationToken);
    }
}
