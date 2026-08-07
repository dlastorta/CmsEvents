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
    private static readonly DateTime ValidTimestamp = new(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
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
}
