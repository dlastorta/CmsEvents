namespace CmsEvents.Unit.Tests.Application.Features;

using System.Text.Json;
using CmsEvents.Application.Features.ProcessEventBatch;
using CmsEvents.Contracts.Events;
using FluentAssertions;
using Xunit;

/// <summary>
/// Covers <see cref="CmsEventValidator"/> per ADR-008 input validation and its "in-handler,
/// non-throwing" placement principle. See ADR-008 § Validation placement principle.
/// </summary>
public sealed class CmsEventValidatorTests
{
    private const string ValidTimestamp = "2026-08-01T10:00:00Z";
    private static readonly JsonElement EmptyPayload = JsonDocument.Parse("{}").RootElement;

    private readonly CmsEventValidator _sut = new();

    [Fact]
    public void Validate_PublishWithAllRequiredFields_IsValid()
    {
        var evt = new CmsEventEnvelope
        {
            Type = CmsEventType.Publish,
            Id = "id-1",
            Version = 1,
            Timestamp = ValidTimestamp,
            Payload = EmptyPayload,
        };

        var result = _sut.Validate(evt);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_DeleteWithoutPayloadOrVersion_IsValid()
    {
        var evt = new CmsEventEnvelope
        {
            Type = CmsEventType.Delete,
            Id = "id-1",
            Timestamp = ValidTimestamp,
        };

        var result = _sut.Validate(evt);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_PublishWithVersionZero_IsInvalid()
    {
        var evt = new CmsEventEnvelope
        {
            Type = CmsEventType.Publish,
            Id = "id-1",
            Version = 0,
            Timestamp = ValidTimestamp,
            Payload = EmptyPayload,
        };

        var result = _sut.Validate(evt);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CmsEventEnvelope.Version));
    }

    [Fact]
    public void Validate_PublishWithoutPayload_IsInvalid()
    {
        var evt = new CmsEventEnvelope
        {
            Type = CmsEventType.Publish,
            Id = "id-1",
            Version = 1,
            Timestamp = ValidTimestamp,
            Payload = null,
        };

        var result = _sut.Validate(evt);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_UnknownType_IsInvalid()
    {
        var evt = new CmsEventEnvelope
        {
            Type = "PUBLISH", // uppercase — case-sensitive per ADR-008
            Id = "id-1",
            Version = 1,
            Timestamp = ValidTimestamp,
            Payload = EmptyPayload,
        };

        var result = _sut.Validate(evt);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CmsEventEnvelope.Type));
    }

    [Fact]
    public void Validate_EmptyId_IsInvalid()
    {
        var evt = new CmsEventEnvelope
        {
            Type = CmsEventType.Delete,
            Id = string.Empty,
            Timestamp = ValidTimestamp,
        };

        var result = _sut.Validate(evt);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CmsEventEnvelope.Id));
    }

    [Fact]
    public void Validate_DeleteWithVersion_IsInvalid()
    {
        // Delete carries no version concept — a stray version field signals a malformed
        // producer contract and must be rejected rather than silently ignored.
        var evt = new CmsEventEnvelope
        {
            Type = CmsEventType.Delete,
            Id = "id-1",
            Version = 3,
            Timestamp = ValidTimestamp,
        };

        var result = _sut.Validate(evt);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(CmsEventEnvelope.Version) &&
            e.ErrorMessage.Contains("absent for delete", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_PublishWithPayloadAtSizeLimit_IsValid()
    {
        // Exact boundary — a payload whose UTF-8 byte length equals the cap must pass.
        var payload = BuildPayloadOfSize(CmsEventValidator.MaxPayloadBytes);

        var evt = new CmsEventEnvelope
        {
            Type = CmsEventType.Publish,
            Id = "id-1",
            Version = 1,
            Timestamp = ValidTimestamp,
            Payload = payload,
        };

        var result = _sut.Validate(evt);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_PublishWithOversizedPayload_IsInvalid()
    {
        // One byte over the cap → validation_error. Protects logs / storage / memory
        // from unbounded producer payloads per ADR-008 § payload sanitization.
        var payload = BuildPayloadOfSize(CmsEventValidator.MaxPayloadBytes + 1);

        var evt = new CmsEventEnvelope
        {
            Type = CmsEventType.Publish,
            Id = "id-1",
            Version = 1,
            Timestamp = ValidTimestamp,
            Payload = payload,
        };

        var result = _sut.Validate(evt);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(CmsEventEnvelope.Payload) &&
            e.ErrorMessage.Contains("maximum allowed size", System.StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds a JSON object <c>{"data":"aaaa…"}</c> whose serialized UTF-8 byte length equals
    /// the requested total. The wrapper <c>{"data":""}</c> is 11 bytes; the rest is filler.
    /// </summary>
    private static JsonElement BuildPayloadOfSize(int totalBytes)
    {
        const string prefix = "{\"data\":\"";
        const string suffix = "\"}";
        var fillerLength = totalBytes - prefix.Length - suffix.Length;
        if (fillerLength < 0)
        {
            throw new System.ArgumentOutOfRangeException(nameof(totalBytes),
                "Requested size is smaller than the JSON wrapper itself.");
        }

        var json = prefix + new string('a', fillerLength) + suffix;
        return JsonDocument.Parse(json).RootElement;
    }
}
