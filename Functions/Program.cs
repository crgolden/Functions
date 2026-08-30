#pragma warning disable SA1200
#pragma warning disable OPENAI001
using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.Identity;
using Elastic.CommonSchema;
using Elastic.Ingest.Elasticsearch;
using Elastic.Ingest.Elasticsearch.DataStreams;
using Elastic.Serilog.Sinks;
using Elastic.Transport;
using Functions;
using Functions.Curator;
using Functions.Extensions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OpenAI.Responses;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

#pragma warning restore SA1200

Telemetry.SemanticConventions.OptInToStableDatabaseConventionsUnlessAlreadyChosen();
var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();
builder.UseMiddleware<ExceptionHandlingMiddleware>();
ResponsesClient responsesClient;
var openAIEndpoint = builder.Configuration.GetRequired<Uri>("OpenAIEndpoint");
var responsesClientOptions = new ResponsesClientOptions { Endpoint = new Uri($"{openAIEndpoint}openai/v1/") };
if (builder.Environment.IsProduction())
{
    var tokenCredential = new DefaultAzureCredential();
    var authenticationPolicy = new BearerTokenPolicy(tokenCredential, "https://cognitiveservices.azure.com/.default");
    responsesClient = new ResponsesClient(authenticationPolicy, responsesClientOptions);
    var elasticsearchNode = builder.Configuration.GetRequired<Uri>("ElasticsearchNode");
    var alloyEndpoint = builder.Configuration.GetRequired<Uri>("AlloyEndpoint");
    var blobUri = builder.Configuration.GetRequired<Uri>("BlobUri");
    var dataProtectionKeyIdentifier = builder.Configuration.GetRequired<Uri>("DataProtectionKeyIdentifier");
    var applicationName = builder.Configuration.GetRequired<string>("WEBSITE_SITE_NAME");
    var elasticsearchUsername = builder.Configuration.GetRequired<string>("ElasticsearchUsername");
    var elasticsearchPassword = builder.Configuration.GetRequired<string>("ElasticsearchPassword");
    builder.Logging.AddSerilog(new LoggerConfiguration()
        .ReadFrom.Configuration(builder.Configuration)
        .WriteTo.Elasticsearch(
            [elasticsearchNode],
            opts =>
            {
                opts.DataStream = new DataStreamName("logs", "app", nameof(Functions));
                opts.BootstrapMethod = BootstrapMethod.Failure;
                opts.TextFormatting.MapCustom = (ecsDocument, _) =>
                {
                    ecsDocument.Service ??= new Service();
                    ecsDocument.Service.Name = applicationName;
                    return ecsDocument;
                };
            },
            transport =>
            {
                var header = new BasicAuthentication(elasticsearchUsername, elasticsearchPassword);
                transport.Authentication(header);
            })
        .CreateLogger());
    builder.Services
        .AddOpenTelemetry()
        .ConfigureResource(rb => rb
            .AddService(applicationName, null, typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0")
            .AddAttributes(new Dictionary<string, object>
            {
                ["deployment.environment"] = builder.Environment.EnvironmentName.ToLowerInvariant(),
            }))
        .UseFunctionsWorkerDefaults()
        .WithMetrics(m => m
            .AddMeter(nameof(Functions))
            .AddRuntimeInstrumentation()
            .AddOtlpExporter(o => o.Endpoint = alloyEndpoint))
        .WithTracing(t => t
            .SetSampler(new AlwaysOnSampler())
            .AddSource(nameof(Functions))
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRedisInstrumentation()
            .AddNpgsql()
            .AddSqlClientInstrumentation()
            .AddOtlpExporter(o => o.Endpoint = alloyEndpoint));
    builder.Services
        .AddDataProtection()
        .SetApplicationName(applicationName)
        .PersistKeysToAzureBlobStorage(blobUri, tokenCredential)
        .ProtectKeysWithAzureKeyVault(dataProtectionKeyIdentifier, tokenCredential);
}
else
{
    var openAIApiKey = builder.Configuration.GetRequired<string>("OpenAIApiKey");
    var credential = new ApiKeyCredential(openAIApiKey);
    responsesClient = new ResponsesClient(credential, responsesClientOptions);
    builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
}

builder.Services.AddCuratorServices(builder.Configuration, responsesClient);

await builder.Build().RunAsync();

#pragma warning restore OPENAI001
