#pragma warning disable SA1200
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Azure.Security.KeyVault.Secrets;
using Functions.Extensions;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Resend;
#pragma warning restore SA1200

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();
string resendApiToken;
if (builder.Environment.IsProduction())
{
    var tokenCredential = new DefaultAzureCredential();
    var keyVaultUrl = builder.Configuration.GetRequired<Uri>("KeyVaultUri");
    var secretClient = new SecretClient(keyVaultUrl, tokenCredential);
    var resendApiTokenSecret = await secretClient.GetSecretAsync("ResendApiToken");
    resendApiToken = resendApiTokenSecret.Value.Value;
    builder.Services
        .AddOpenTelemetry()
        .UseFunctionsWorkerDefaults()
        .UseAzureMonitorExporter();
}
else
{
    resendApiToken = builder.Configuration.GetRequired<string>("ResendApiToken");
}

builder.Services.Configure<ResendClientOptions>(configureOptions =>
    {
        configureOptions.ApiToken = resendApiToken;
    })
    .AddHttpClient<ResendClient>().Services
    .AddTransient<IResend, ResendClient>();

await builder.Build().RunAsync();
