using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ReviewGuard.Core;
using ReviewGuard.Functions;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

const string section = "ReviewGuard";
builder.Services.AddOptions<ReviewDepthOptions>().BindConfiguration(section);
builder.Services.AddOptions<AlertOptions>().BindConfiguration(section);
builder.Services.AddOptions<WebhookOptions>().BindConfiguration(section)
    .Validate(o => !string.IsNullOrWhiteSpace(o.WebhookSecret), "ReviewGuard:WebhookSecret is required")
    .ValidateOnStart();
builder.Services.AddOptions<AzureDevOpsOptions>().BindConfiguration(section)
    .Validate(o => Uri.IsWellFormedUriString(o.OrganizationUrl, UriKind.Absolute), "ReviewGuard:OrganizationUrl is required")
    .Validate(o => !string.IsNullOrWhiteSpace(o.Pat), "ReviewGuard:Pat is required")
    .ValidateOnStart();

builder.Services.AddHttpClient<IAzureDevOpsClient, AzureDevOpsClient>();
builder.Services.AddHttpClient<IAlertSender, TeamsAlertSender>();

// Local: connection string (Azurite). Azure: Managed Identity on the table endpoint, no keys.
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var tableUri = config["AuditTableServiceUri"];
    return string.IsNullOrWhiteSpace(tableUri)
        ? new TableServiceClient(config["AzureWebJobsStorage"])
        : new TableServiceClient(new Uri(tableUri), new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned));
});
builder.Services.AddSingleton<IAuditStore, TableAuditStore>();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<ReviewDepthOptions>>().Value);
builder.Services.AddScoped<ReviewGuardService>();

builder.Build().Run();
