using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using QuotesBff;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        services
            .AddOptions<BffOptions>()
            .Bind(context.Configuration.GetSection(BffOptions.SectionName))
            .Validate(o => { o.Validate(); return true; })
            .ValidateOnStart();

        services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());

        services.AddHttpClient(ApiProxyFunction.HttpClientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<BffOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
    })
    .Build();

host.Run();
