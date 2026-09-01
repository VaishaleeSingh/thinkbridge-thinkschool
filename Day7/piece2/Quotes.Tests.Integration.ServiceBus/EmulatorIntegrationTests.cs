using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuotesApi.Data;
using QuotesApi.Messaging;

namespace Quotes.Tests.Integration.ServiceBus;

/// <summary>
/// End-to-end tests against the Service Bus emulator.
///
/// What each test actually proves, and why the assertions are where they are:
///
/// - The application's worker consumes the AUDIT subscription only. So the
///   round-trip test asserts on QuoteAuditEntries. Asserting on
///   QuoteSearchProjections would never pass: nothing in the running app
///   consumes search-index, and a test that can never pass is worse than no
///   test, because it reads like coverage.
///
/// - Fan-out is therefore proved directly at the broker: publish a Created and
///   a Deleted, then receive from the search-index subscription and assert the
///   Deleted never arrives. That is the subscription filter doing its job, and
///   it fails loudly if the $Default TrueFilter is ever left in place.
///
/// - Idempotency is proved by sending the SAME MessageId twice and asserting
///   one audit row.
/// </summary>
[Collection("ServiceBusEmulator")]
public class EmulatorIntegrationTests : IAsyncDisposable
{
    private readonly ServiceBusEmulatorFixture _fixture;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbFile = $"sb-test-{Guid.NewGuid():N}.db";

    public EmulatorIntegrationTests(ServiceBusEmulatorFixture fixture)
    {
        _fixture = fixture;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");

            // Turn messaging on for this host. FullyQualifiedNamespace has to
            // be set even though the client below is replaced: ServiceBusOptions
            // validates it on start when Enabled is true, and ValidateOnStart
            // means the host refuses to boot without it.
            builder.UseSetting("ServiceBus:Enabled", "true");
            builder.UseSetting("ServiceBus:FullyQualifiedNamespace", "localhost");
            builder.UseSetting("ServiceBus:TopicName", "quote-events");
            builder.UseSetting("ServiceBus:AuditSubscription", "audit");

            builder.ConfigureServices(services =>
            {
                var dbDescriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<QuotesDbContext>));
                if (dbDescriptor is not null) services.Remove(dbDescriptor);

                services.AddDbContext<QuotesDbContext>(options =>
                    options.UseSqlite($"Data Source={_dbFile}"));

                // Replace the managed-identity client with one pointed at the
                // emulator. Transport stays at the default (AMQP over TCP) --
                // the emulator does not support AMQP WebSockets.
                var clientDescriptors = services
                    .Where(d => d.ServiceType == typeof(ServiceBusClient))
                    .ToList();
                foreach (var descriptor in clientDescriptors)
                    services.Remove(descriptor);

                services.AddSingleton(new ServiceBusClient(_fixture.ConnectionString));
            });
        });
    }

    public async ValueTask DisposeAsync()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();
            await db.Database.EnsureDeletedAsync();
        }

        await _factory.DisposeAsync();
    }

    private async Task<T> WithDbAsync<T>(Func<QuotesDbContext, Task<T>> read)
    {
        // A fresh scope (and therefore a fresh DbContext) per poll. Reusing one
        // context would answer from its change tracker and never observe the
        // row the worker committed on another connection.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();
        return await read(db);
    }

    private async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await condition()) return true;
            await Task.Delay(250);
        }

        return await condition();
    }

    private static ServiceBusMessage BuildMessage(QuoteChangedEvent evt) =>
        new(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(evt))
        {
            MessageId = evt.EventId,
            ContentType = "application/json",
            Subject = evt.EventType,
            ApplicationProperties =
            {
                ["eventType"] = evt.EventType,
                ["schemaVersion"] = evt.SchemaVersion
            }
        };

    [Fact]
    public async Task Published_event_is_consumed_and_audited()
    {
        // Boot the host (and therefore the worker) before publishing.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();
            await db.Database.MigrateAsync();
        }

        var publisher = _factory.Services.GetRequiredService<IQuoteEventPublisher>();
        var evt = QuoteChangedEvent.Created(
            999, "test-user", "Emulator test", "Text", DateTimeOffset.UtcNow);

        await publisher.PublishAsync(evt, CancellationToken.None);

        var audited = await WaitUntilAsync(
            () => WithDbAsync(db => db.QuoteAuditEntries.AnyAsync(a => a.EventId == evt.EventId)),
            TimeSpan.FromSeconds(30));

        audited.Should().BeTrue(
            "the audit worker should have consumed the event and written one audit row");
    }

    [Fact]
    public async Task Same_message_delivered_twice_produces_one_audit_row()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();
            await db.Database.MigrateAsync();
        }

        var evt = QuoteChangedEvent.Created(
            1001, "test-user", "Idempotency", "Delivered twice", DateTimeOffset.UtcNow);

        await using var client = new ServiceBusClient(_fixture.ConnectionString);
        await using var sender = client.CreateSender("quote-events");

        // The same MessageId twice. Duplicate detection is OFF on the topic, so
        // the broker delivers both and the consumer-side dedupe store is the
        // only thing standing between this and two audit rows.
        await sender.SendMessageAsync(BuildMessage(evt));
        await sender.SendMessageAsync(BuildMessage(evt));

        await WaitUntilAsync(
            () => WithDbAsync(db => db.QuoteAuditEntries.AnyAsync(a => a.EventId == evt.EventId)),
            TimeSpan.FromSeconds(30));

        // Give the second delivery time to be processed and deduped, so this
        // asserts "still one" rather than "not yet two".
        await Task.Delay(TimeSpan.FromSeconds(5));

        var rows = await WithDbAsync(db =>
            db.QuoteAuditEntries.CountAsync(a => a.EventId == evt.EventId));

        rows.Should().Be(1, "the ProcessedMessages dedupe row must suppress the second delivery");
    }

    [Fact]
    public async Task Search_index_subscription_filters_out_delete_events()
    {
        var created = QuoteChangedEvent.Created(
            2001, "test-user", "Filtered", "Created reaches search-index", DateTimeOffset.UtcNow);
        var deleted = QuoteChangedEvent.Deleted(2001, "test-user", DateTimeOffset.UtcNow);

        await using var client = new ServiceBusClient(_fixture.ConnectionString);
        await using var sender = client.CreateSender("quote-events");

        await sender.SendMessageAsync(BuildMessage(created));
        await sender.SendMessageAsync(BuildMessage(deleted));

        // Nothing in the app consumes search-index, so receive from it directly.
        await using var receiver = client.CreateReceiver("quote-events", "search-index");

        var received = await receiver.ReceiveMessagesAsync(
            maxMessages: 5, maxWaitTime: TimeSpan.FromSeconds(10));

        var eventTypes = received
            .Select(m => m.ApplicationProperties.TryGetValue("eventType", out var v) ? v as string : null)
            .ToList();

        eventTypes.Should().Contain("QuoteCreated");
        eventTypes.Should().NotContain(
            "QuoteDeleted",
            "the search-index SQL filter excludes deletes -- if this fails, the $Default TrueFilter is still in place");

        foreach (var message in received)
            await receiver.CompleteMessageAsync(message);
    }

    [Fact]
    public async Task Malformed_payload_is_dead_lettered_on_first_delivery()
    {
        await using var client = new ServiceBusClient(_fixture.ConnectionString);
        await using var sender = client.CreateSender("quote-events");

        var poison = new ServiceBusMessage(BinaryData.FromString("{ this is not json"))
        {
            MessageId = $"poison-{Guid.NewGuid():N}",
            ContentType = "application/json",
            Subject = "QuoteCreated",
            ApplicationProperties = { ["eventType"] = "QuoteCreated" }
        };

        await sender.SendMessageAsync(poison);

        await using var dlqReceiver = client.CreateReceiver(
            "quote-events", "audit",
            new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });

        ServiceBusReceivedMessage? dead = null;
        await WaitUntilAsync(async () =>
        {
            dead = await dlqReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
            return dead is not null && dead.MessageId == poison.MessageId;
        }, TimeSpan.FromSeconds(30));

        dead.Should().NotBeNull();
        dead!.MessageId.Should().Be(poison.MessageId);
        dead.DeadLetterReason.Should().Be(
            "InvalidPayload",
            "unparseable JSON is classified as poison and dead-lettered on the first delivery, "
            + "not retried until MaxDeliveryCount is exhausted");
        dead.DeliveryCount.Should().Be(1);

        await dlqReceiver.CompleteMessageAsync(dead);
    }
}
