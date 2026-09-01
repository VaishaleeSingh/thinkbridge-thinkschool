using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuotesApi.Messaging;
using FluentAssertions;

namespace Quotes.Tests.Unit.Messaging;

/// <summary>
/// Unit tests for <see cref="MessageFailureClassifier"/>.
/// No broker, no DI, no I/O — pure logic tests.
/// </summary>
public class MessageFailureClassifierTests
{
    [Fact]
    public void JsonException_IsPoison()
    {
        var result = MessageFailureClassifier.IsPoison(new JsonException("bad json"));
        result.Should().BeTrue();
    }

    [Fact]
    public void UnknownSchemaVersionException_IsPoison()
    {
        var result = MessageFailureClassifier.IsPoison(new UnknownSchemaVersionException("2.0"));
        result.Should().BeTrue();
    }

    [Fact]
    public void FormatException_IsPoison()
    {
        var result = MessageFailureClassifier.IsPoison(new FormatException("bad format"));
        result.Should().BeTrue();
    }

    [Fact]
    public void DbUpdateException_IsNotPoison()
    {
        var result = MessageFailureClassifier.IsPoison(new DbUpdateException("db error"));
        result.Should().BeFalse();
    }

    [Fact]
    public void OperationCanceledException_IsNotPoison()
    {
        var result = MessageFailureClassifier.IsPoison(new OperationCanceledException());
        result.Should().BeFalse();
    }

    [Fact]
    public void RandomException_IsNotPoison_TreatedAsTransient()
    {
        var result = MessageFailureClassifier.IsPoison(new InvalidOperationException("unexpected"));
        result.Should().BeFalse();
    }

    [Fact]
    public void JsonException_PoisonReason_IsInvalidPayload()
    {
        var reason = MessageFailureClassifier.PoisonReason(new JsonException("bad json"));
        reason.Should().Be("InvalidPayload");
    }

    [Fact]
    public void UnknownSchemaVersionException_PoisonReason_IsUnknownSchemaVersion()
    {
        var reason = MessageFailureClassifier.PoisonReason(new UnknownSchemaVersionException("99.0"));
        reason.Should().Be("UnknownSchemaVersion");
    }

    [Fact]
    public void PoisonDescription_ContainsExceptionMessage()
    {
        var ex = new JsonException("the payload was mangled");
        var desc = MessageFailureClassifier.PoisonDescription(ex);
        desc.Should().Contain("the payload was mangled");
    }
}
