using System.Diagnostics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Quotes.Tests.Unit.TestDoubles;
using QuotesApi.Data;
using QuotesApi.Models;
using QuotesApi.Observability;
using QuotesApi.Services;

namespace Quotes.Tests.Unit;

/// <summary>
/// Covers the custom span AuthService creates around password verification.
///
/// Spans are observed with an ActivityListener -- the in-process way to see
/// Activities without running a collector or an exporter. That matters: it
/// means these assertions hold in CI, where nothing is listening on an OTLP
/// port, and they test what the app actually produces rather than what some
/// dashboard happens to render.
/// </summary>
public class AuthServiceTracingTests
{
    /// <summary>
    /// Subscribes to this app's ActivitySource and records every span that
    /// finishes. Disposing the listener unsubscribes it, so spans from one
    /// test can never show up in another.
    /// </summary>
    private static (List<Activity> Spans, ActivityListener Listener) ListenForSpans()
    {
        var spans = new List<Activity>();

        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == QuotesActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add
        };

        ActivitySource.AddActivityListener(listener);

        return (spans, listener);
    }

    private static (AuthService Sut, QuotesDbContext Db) CreateSut()
    {
        var options = new DbContextOptionsBuilder<QuotesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new QuotesDbContext(options);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "unit-test-secret-that-is-long-enough-for-hmacsha256"
            })
            .Build();

        var sut = new AuthService(
            db,
            config,
            Substitute.For<IRefreshTokenService>(),
            Substitute.For<ILogger<AuthService>>(),
            new FakeClock(DateTimeOffset.UtcNow));

        return (sut, db);
    }

    private static async Task<User> SeedUserAsync(
        AuthService sut, QuotesDbContext db, string email, string password)
    {
        var user = new User
        {
            Email = email,
            PasswordHash = sut.HashPassword(password),
            CreatedAt = DateTime.UtcNow
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        return user;
    }

    [Fact]
    public async Task LoginAsync_WhenPasswordIsVerified_EmitsSpanTaggedWithTheUserIdAndNoEmail()
    {
        // Arrange
        var (spans, listener) = ListenForSpans();
        User user;
        using (listener)
        {
            var (sut, db) = CreateSut();
            user = await SeedUserAsync(sut, db, "tagged@example.com", "Correct-Horse-Battery-Staple-1");

            // Act
            await sut.LoginAsync("tagged@example.com", "Correct-Horse-Battery-Staple-1", CancellationToken.None);
        }

        // Assert -- the numeric id, and deliberately no email anywhere on
        // the span: traces are somewhere personal data accumulates quietly.
        var span = spans.Should().ContainSingle(s => s.OperationName == "verify-password").Subject;
        span.GetTagItem("user.id").Should().Be(user.Id);
        span.Tags.Should().NotContain(tag => tag.Value != null && tag.Value.Contains("@"));
    }

    [Fact]
    public async Task LoginAsync_WhenTheUserDoesNotExist_EmitsNoVerifyPasswordSpan()
    {
        // Arrange
        var (spans, listener) = ListenForSpans();
        using (listener)
        {
            var (sut, _) = CreateSut();

            // Act -- nobody seeded, so LoginAsync returns before hashing.
            await sut.LoginAsync("nobody@example.com", "irrelevant", CancellationToken.None);
        }

        // Assert -- no hashing happened, so there is nothing to time. A
        // span here would mean the expensive work runs even for unknown
        // emails, which would be both wasteful and a timing oracle telling
        // an attacker which addresses exist.
        spans.Should().NotContain(s => s.OperationName == "verify-password");
    }

    [Fact]
    public async Task LoginAsync_WithNobodyListening_StillCompletesNormally()
    {
        // No ActivityListener is registered at all here, so
        // StartActivity(...) returns null. Every use of that activity is
        // null-conditional; if someone ever drops the "?" this test fails
        // with a NullReferenceException rather than the API 500ing in an
        // environment that happens to have tracing switched off.
        var (sut, db) = CreateSut();
        await SeedUserAsync(sut, db, "nolistener@example.com", "Correct-Horse-Battery-Staple-1");

        var result = await sut.LoginAsync(
            "nolistener@example.com", "Correct-Horse-Battery-Staple-1", CancellationToken.None);

        result.Should().NotBeNull();
    }
}
