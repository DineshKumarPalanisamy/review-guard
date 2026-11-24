using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReviewGuard.Core;
using ReviewGuard.Functions;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddOptions<ReviewDepthOptions>().BindConfiguration(ReviewDepthOptions.SectionName);
builder.Services.AddOptions<WebhookOptions>().BindConfiguration(WebhookOptions.SectionName)
    .Validate(o => !string.IsNullOrWhiteSpace(o.WebhookSecret), "ReviewGuard:WebhookSecret is required")
    .ValidateOnStart();

builder.Build().Run();
