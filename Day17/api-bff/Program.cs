using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using QuotesBff;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddOptions<BffOptions>()
    .Bind(builder.Configuration.GetSection(BffOptions.SectionName))
    // Fail at startup rather than on the first request. Same reasoning as the
    // Week-1 API's own options validation (Day 4): a misconfigured proxy that
    // starts successfully and 500s per-request is much harder to diagnose than
    // one that refuses to start.
    .Validate(o => { o.Validate(); return true; })
    .ValidateOnStart();

// ONE credential for the process, not one per request.
//
// DefaultAzureCredential rather than ManagedIdentityCredential so the same code
// path runs locally (it falls through to the Azure CLI / VS Code credential
// under `az login`) and in Azure (where it binds to the Function App's
// system-assigned identity via IMDS). No #if DEBUG, and no local secret to make
// development work.
//
// It also caches the token and refreshes it before expiry, which is why there
// is no hand-rolled token cache anywhere in this project -- and why the token
// is never written to disk, to a setting, or to a log.
builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());

// IHttpClientFactory, not a `new HttpClient()` per invocation. A Function can be
// invoked hundreds of times a second on one instance; a per-invocation
// HttpClient exhausts the socket pool and starts failing with
// SocketException: Address already in use, minutes after everything looked fine.
builder.Services
    .AddHttpClient(ApiProxyFunction.HttpClientName, (sp, client) =>
    {
        var options = sp.GetRequiredService<IOptions<BffOptions>>().Value;
        client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(30);
    });

builder.Build().Run();
