using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using QuotesApi.Messaging;

namespace QuotesApi.Extensions;

/// <summary>
/// Wires Azure Service Bus into the DI container.
///
/// The Enabled guard is the same pattern ObservabilityExtensions uses for the
/// OTLP exporter: never attempt a network connection at startup when no
/// namespace is configured, because every integration test that boots the real
/// Program.cs via WebApplicationFactory would fail with a connection error.
///
/// When Enabled = false:
///   - IQuoteEventPublisher is registered as NoOpQuoteEventPublisher (logs at Debug)
///   - No ServiceBusClient, no ServiceBusSender, no processor, no worker.
///   - All quote writes succeed normally and publish nothing.
///
/// When Enabled = true:
///   - ServiceBusClient is a SINGLETON. It owns one AMQP connection; creating
///     one per request is the classic Service Bus performance bug.
///   - ServiceBusSender is likewise resolved once per topic name.
///   - QuoteEventProcessorService (BackgroundService) is registered as a hosted
///     service and starts consuming the audit subscription immediately.
/// </summary>
public static class MessagingExtensions
{
    public static IServiceCollection AddMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<ServiceBusOptions>()
            .Bind(configuration.GetSection(ServiceBusOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var opts = configuration
            .GetSection(ServiceBusOptions.SectionName)
            .Get<ServiceBusOptions>() ?? new ServiceBusOptions();

        if (!opts.Enabled)
        {
            // No namespace configured — use the no-op publisher.
            // All other registrations are skipped so no AMQP connection
            // is ever attempted.
            services.AddSingleton<IQuoteEventPublisher, NoOpQuoteEventPublisher>();
            return services;
        }

        // --- Real Service Bus wiring ---

        // AddAzureClients registers ServiceBusClient as a singleton and
        // wires DefaultAzureCredential automatically. No connection string.
        services.AddAzureClients(clientBuilder =>
        {
            clientBuilder.AddServiceBusClientWithNamespace(opts.FullyQualifiedNamespace)
                         .WithCredential(new DefaultAzureCredential());
        });

        // Sender is likewise long-lived: one per topic, resolved once and
        // cached. Creating a new sender per request tears down the AMQP
        // link on every call.
        services.AddSingleton(sp =>
        {
            var client = sp.GetRequiredService<ServiceBusClient>();
            return client.CreateSender(opts.TopicName);
        });

        services.AddSingleton<IQuoteEventPublisher, ServiceBusQuoteEventPublisher>();

        // Scoped: handlers resolve a scoped QuotesDbContext, so they must
        // not outlive a single message-processing scope. Keyed by subscription
        // name, which is how each worker finds its own handler.
        // Keyed by the CONFIGURED names, not by string literals: the worker
        // resolves its handler by the subscription name it was built with, so
        // renaming a subscription in configuration has to move both together
        // or the lookup throws at the first message instead of at startup.
        services.AddKeyedScoped<IQuoteEventHandler, AuditQuoteEventHandler>(
            opts.AuditSubscription!);
        services.AddKeyedScoped<IQuoteEventHandler, SearchIndexQuoteEventHandler>(
            opts.SearchIndexSubscription!);

        // Scoped store: same reason.
        services.AddScoped<IProcessedMessageStore, EfProcessedMessageStore>();

        // ONE WORKER PER SUBSCRIPTION. Both subscriptions are consumed by
        // this app: "audit" takes every event, "search-index" takes only the
        // content changes its SQL filter lets through. Registering the worker
        // twice (rather than once, with the search-index handler registered
        // and unreachable) is what makes the fan-out real instead of narrated.
        //
        // Registered as IHostedService instances built by a factory, because
        // AddHostedService<T> registers one instance of a type and these two
        // differ only by a constructor argument. Each owns its own processor.
        services.AddSingleton<IHostedService>(sp =>
            CreateWorker(sp, opts.TopicName!, opts.AuditSubscription!));

        services.AddSingleton<IHostedService>(sp =>
            CreateWorker(sp, opts.TopicName!, opts.SearchIndexSubscription!));

        return services;
    }

    /// <summary>
    /// Builds one worker for one subscription: its processor, with the
    /// settings the whole exercise turns on, and the subscription name it
    /// resolves its handler and dedupe rows by.
    /// </summary>
    private static QuoteEventProcessorService CreateWorker(
        IServiceProvider sp,
        string topicName,
        string subscriptionName)
    {
        var client = sp.GetRequiredService<ServiceBusClient>();
        var serviceBusOptions = sp.GetRequiredService<IOptions<ServiceBusOptions>>().Value;

        var processor = client.CreateProcessor(
            topicName,
            subscriptionName,
            new ServiceBusProcessorOptions
            {
                // PeekLock is the default but stating it here is intentional:
                // ReceiveAndDelete would make retries and DLQ impossible.
                ReceiveMode = ServiceBusReceiveMode.PeekLock,

                // > 1 is what makes this instance itself a competing consumer.
                MaxConcurrentCalls = serviceBusOptions.MaxConcurrentCalls,

                // FALSE is the decision the whole exercise turns on:
                // auto-complete makes every outcome (complete/abandon/DLQ)
                // implicit. Explicit completion means every path is
                // intentional code.
                AutoCompleteMessages = false,

                // Without lock renewal, a handler that runs longer than
                // LockDuration finishes work on an expired lock,
                // CompleteAsync throws MessageLockLost, and the message
                // redelivers -- exactly what the idempotency store exists to
                // absorb, but better to avoid the redelivery in the first place.
                MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(
                    serviceBusOptions.MaxAutoLockRenewalMinutes),

                // Prefetch is left at 0 initially. Prefetched messages are
                // locked while sitting in the client-side buffer; an aggressive
                // prefetch with a slow handler produces lock expiry and
                // redelivery. Tune only with a measured reason.
                PrefetchCount = serviceBusOptions.PrefetchCount,
            });

        return new QuoteEventProcessorService(
            subscriptionName,
            processor,
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IOptions<ServiceBusOptions>>(),
            sp.GetRequiredService<ILogger<QuoteEventProcessorService>>());
    }
}
