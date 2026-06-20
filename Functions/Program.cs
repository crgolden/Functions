#pragma warning disable SA1200
#pragma warning disable OPENAI001
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Data.Common;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Azure.Security.KeyVault.Secrets;
using Elastic.Ingest.Elasticsearch;
using Elastic.Ingest.Elasticsearch.DataStreams;
using Elastic.Serilog.Sinks;
using Elastic.Transport;
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
using OpenTelemetry.Trace;
using Resend;
using Serilog;
#pragma warning restore SA1200

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();
string resendApiToken;
ResponsesClient responsesClient;
var sqlConnectionStringBuilderSection = builder.Configuration.GetRequiredSection(nameof(SqlConnectionStringBuilder));
var sqlConnectionStringBuilder = sqlConnectionStringBuilderSection.Get<SqlConnectionStringBuilder>() ?? throw new InvalidOperationException($"Missing '{nameof(SqlConnectionStringBuilder)}' section.");
Uri openAIEndpoint = builder.Configuration.GetRequired<Uri>("OpenAIEndpoint"),
    storageUri = builder.Configuration.GetRequired<Uri>("StorageUri");
var responsesClientOptions = new ResponsesClientOptions { Endpoint = new Uri($"{openAIEndpoint}openai/v1/") };
if (builder.Environment.IsProduction())
{
    var serviceBusNamespace = builder.Configuration.GetRequired<string>("ServiceBusConnection:fullyQualifiedNamespace");
    var keyVaultUrl = builder.Configuration.GetRequired<Uri>("KeyVaultUri");
    var tokenCredential = new DefaultAzureCredential();
    var secretClient = new SecretClient(keyVaultUrl, tokenCredential);
    var secrets = secretClient.GetFunctionsSecrets();
    resendApiToken = secrets.ResendApiToken.Value;
    sqlConnectionStringBuilder.UserID = secrets.SqlServerUserId.Value;
    sqlConnectionStringBuilder.Password = secrets.SqlServerPassword.Value;
    var authenticationPolicy = new BearerTokenPolicy(tokenCredential, "https://cognitiveservices.azure.com/.default");
    responsesClient = new ResponsesClient(authenticationPolicy, responsesClientOptions);
    builder.Services.AddAzureClients(azureClientFactoryBuilder =>
    {
        azureClientFactoryBuilder.UseCredential(tokenCredential);
        azureClientFactoryBuilder.AddBlobServiceClient(storageUri).WithName("crgolden");
        azureClientFactoryBuilder.AddServiceBusClientWithNamespace(serviceBusNamespace).WithName("crgolden");
    });
    var elasticsearchNode = builder.Configuration.GetRequired<Uri>("ElasticsearchNode");
    var alloyEndpoint = builder.Configuration.GetRequired<Uri>("AlloyEndpoint");
    builder.Logging.AddSerilog(new LoggerConfiguration()
        .ReadFrom.Configuration(builder.Configuration)
        .WriteTo.Elasticsearch(
            [elasticsearchNode],
            opts =>
            {
                opts.DataStream = new DataStreamName("logs", "dotnet", nameof(Functions));
                opts.BootstrapMethod = BootstrapMethod.Failure;
            },
            transport =>
            {
                var header = new BasicAuthentication(secrets.ElasticsearchUsername.Value, secrets.ElasticsearchPassword.Value);
                transport.Authentication(header);
            })
        .CreateLogger());
    builder.Services
        .AddOpenTelemetry()
        .UseFunctionsWorkerDefaults()
        .UseAzureMonitorExporter()
        .WithMetrics(m => m.AddOtlpExporter(o => o.Endpoint = alloyEndpoint))
        .WithTracing(t => t.AddOtlpExporter(o => o.Endpoint = alloyEndpoint));
}
else
{
    var secrets = builder.Configuration.GetFunctionsSecrets();
    resendApiToken = secrets.ResendApiToken;
    builder.Services.AddAzureClients(azureClientFactoryBuilder =>
    {
        azureClientFactoryBuilder.AddBlobServiceClient(secrets.StorageConnectionString).WithName("crgolden");
        azureClientFactoryBuilder.AddServiceBusClient(secrets.ServiceBusConnectionString).WithName("crgolden");
    });
    var credential = new ApiKeyCredential(secrets.OpenAIApiKey);
    responsesClient = new ResponsesClient(credential, responsesClientOptions);
}

builder.Services.AddSingleton(responsesClient);
builder.Services.AddScoped<DbConnection>(sp =>
{
    var dbConnection = SqlClientFactory.Instance.CreateConnection() ?? throw new InvalidOperationException($"{nameof(SqlClientFactory)} failed to create a {nameof(DbConnection)}.");
    dbConnection.ConnectionString = sqlConnectionStringBuilder.ConnectionString;
    return dbConnection;
});
builder.Services.Configure<ResendClientOptions>(options => options.ApiToken = resendApiToken);
builder.Services.AddHttpClient<ResendClient>();
builder.Services.AddHttpClient();
builder.Services.AddTransient<IResend, ResendClient>();

await builder.Build().RunAsync();

#pragma warning restore OPENAI001
