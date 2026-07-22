#pragma warning disable SA1200
#pragma warning disable OPENAI001
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Data.Common;
using Azure.Identity;
using Elastic.Ingest.Elasticsearch;
using Elastic.Ingest.Elasticsearch.DataStreams;
using Elastic.Serilog.Sinks;
using Elastic.Transport;
using Functions;
using Functions.Extensions;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI.Responses;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Resend;
using Serilog;
#pragma warning restore SA1200

const string azureClientName = "crgolden";
var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();
builder.UseMiddleware<ExceptionHandlingMiddleware>();
string resendApiToken = builder.Configuration.GetRequired<string>("ResendApiToken");
ResponsesClient responsesClient;
var sqlConnectionStringBuilderSection = builder.Configuration.GetRequiredSection(nameof(SqlConnectionStringBuilder));
var sqlConnectionStringBuilder = sqlConnectionStringBuilderSection.Get<SqlConnectionStringBuilder>() ?? throw new InvalidOperationException($"Missing '{nameof(SqlConnectionStringBuilder)}' section.");
Uri openAIEndpoint = builder.Configuration.GetRequired<Uri>("OpenAIEndpoint"),
    storageUri = builder.Configuration.GetRequired<Uri>("StorageUri");
var responsesClientOptions = new ResponsesClientOptions { Endpoint = new Uri($"{openAIEndpoint}openai/v1/") };
if (builder.Environment.IsProduction())
{
    var serviceBusNamespace = builder.Configuration.GetRequired<string>("ServiceBusConnection:fullyQualifiedNamespace");
    var tokenCredential = new DefaultAzureCredential();
    var authenticationPolicy = new BearerTokenPolicy(tokenCredential, "https://cognitiveservices.azure.com/.default");
    responsesClient = new ResponsesClient(authenticationPolicy, responsesClientOptions);
    builder.Services.AddAzureClients(azureClientFactoryBuilder =>
    {
        azureClientFactoryBuilder.UseCredential(tokenCredential);
        azureClientFactoryBuilder.AddBlobServiceClient(storageUri).WithName(azureClientName);
        azureClientFactoryBuilder.AddServiceBusClientWithNamespace(serviceBusNamespace).WithName(azureClientName);
        azureClientFactoryBuilder.AddServiceBusAdministrationClientWithNamespace(serviceBusNamespace).WithName(azureClientName);
    });
    var elasticsearchNode = builder.Configuration.GetRequired<Uri>("ElasticsearchNode");
    var alloyEndpoint = builder.Configuration.GetRequired<Uri>("AlloyEndpoint");
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
                    ecsDocument.Service ??= new Elastic.CommonSchema.Service();
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
            .AddService(applicationName, null, typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"))
        .UseFunctionsWorkerDefaults()
        .WithMetrics(m => m
            .AddMeter(nameof(Functions))
            .AddRuntimeInstrumentation()
            .AddOtlpExporter(o => o.Endpoint = alloyEndpoint))
        .WithTracing(t => t
            .SetSampler(new AlwaysOnSampler())
            .AddOtlpExporter(o => o.Endpoint = alloyEndpoint));
}
else
{
    var storageConnectionString = builder.Configuration.GetRequired<string>("StorageConnectionString");
    var serviceBusConnectionString = builder.Configuration.GetRequired<string>("ServiceBusConnection");
    var openAIApiKey = builder.Configuration.GetRequired<string>("OpenAIApiKey");
    builder.Services.AddAzureClients(azureClientFactoryBuilder =>
    {
        azureClientFactoryBuilder.AddBlobServiceClient(storageConnectionString).WithName(azureClientName);
        azureClientFactoryBuilder.AddServiceBusClient(serviceBusConnectionString).WithName(azureClientName);
        azureClientFactoryBuilder.AddServiceBusAdministrationClient(serviceBusConnectionString).WithName(azureClientName);
    });
    var credential = new ApiKeyCredential(openAIApiKey);
    responsesClient = new ResponsesClient(credential, responsesClientOptions);
}

builder.Services.AddSingleton(responsesClient);
builder.Services.AddScoped<DbConnection>(sp =>
{
    var dbConnection = SqlClientFactory.Instance.CreateConnection() ?? throw new InvalidOperationException($"{nameof(SqlClientFactory)} failed to create a {nameof(DbConnection)}.");
    dbConnection.ConnectionString = sqlConnectionStringBuilder.ConnectionString;
    return dbConnection;
});
builder.Services.AddScoped<ChurchWriter>();
builder.Services.Configure<ResendClientOptions>(options => options.ApiToken = resendApiToken);
builder.Services.AddHttpClient<ResendClient>();
builder.Services.AddHttpClient();
builder.Services.AddTransient<IResend, ResendClient>();

await builder.Build().RunAsync();

#pragma warning restore OPENAI001
