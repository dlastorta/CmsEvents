namespace CmsEvents.Application.Features.ProcessEventBatch;

using System.Globalization;
using System.Text;
using System.Text.Json;
using CmsEvents.Contracts.Events;
using FluentValidation;

/// <summary>
/// Per-event input validation per ADR-008 § Input validation. Called by <see cref="ProcessEventBatchHandler"/>
/// for each event individually — validation failures produce permanent-failure outcomes (reason
/// <c>validation_error</c>) and do not throw, so a single malformed event does not block the rest of the batch.
///
/// This validator is NOT registered in MediatR's ValidationBehavior pipeline (which would throw on failure) —
/// the handler calls <c>Validate(evt)</c> explicitly per event.
/// </summary>
public sealed class CmsEventValidator : AbstractValidator<CmsEventEnvelope>
{
    /// <summary>
    /// Hard cap on the UTF-8 byte length of the opaque JSON payload. Chosen to be an order of
    /// magnitude above any realistic CMS entity body while capping DoS-adjacent scenarios where
    /// a producer submits multi-MB payloads that would inflate log lines, DB rows, and memory
    /// pressure. Adjust via a future config knob if the CMS actually emits larger legitimate
    /// bodies. See ADR-008 § Input validation (payload sanitization).
    /// </summary>
    public const int MaxPayloadBytes = 64 * 1024; // 64 KiB

    /// <summary>
    /// Hard cap on the length of an event Id, matching the <c>Entity.Id</c> column length
    /// (see <c>EntityConfiguration.HasMaxLength(256)</c>). Without this rule an oversized id
    /// would pass validation and fail at persistence as a SQL truncation error (surface as
    /// <c>persistence_error</c>), which is misleading — the fault is bad input, not the DB.
    /// </summary>
    public const int MaxIdLength = 256;

    public CmsEventValidator()
    {
        RuleFor(e => e.Type)
            .NotEmpty()
            .Must(CmsEventType.IsValid)
            .WithMessage("Type must be one of: publish, unPublish, delete (case-sensitive).");

        RuleFor(e => e.Id)
            .NotEmpty()
            .WithMessage("Id must be non-empty.")
            .MaximumLength(MaxIdLength)
            .WithMessage($"Id must not exceed {MaxIdLength} characters.");

        RuleFor(e => e.Timestamp)
            .NotEmpty()
            .Must(BeValidIso8601)
            .WithMessage("Timestamp must be a valid ISO 8601 date/time (e.g., \"2026-08-01T10:00:00Z\" or \"2026-08-01T12:00:00+02:00\"). Values with an explicit offset are normalized to UTC before comparison.");

        // Version required for publish/unPublish; must be >= 1.
        RuleFor(e => e.Version)
            .NotNull()
            .GreaterThanOrEqualTo(1)
            .When(e => e.Type is CmsEventType.Publish or CmsEventType.UnPublish)
            .WithMessage("Version must be >= 1 for publish/unPublish events.");

        // Version must be ABSENT for delete — delete has no version concept and a stray
        // version field signals a malformed producer contract. Reject rather than silently
        // ignore so the producer sees the discrepancy.
        RuleFor(e => e.Version)
            .Null()
            .When(e => e.Type == CmsEventType.Delete)
            .WithMessage("Version must be absent for delete events.");

        // Payload required for publish/unPublish; may be absent for delete (opaque per spec).
        RuleFor(e => e.Payload)
            .NotNull()
            .When(e => e.Type is CmsEventType.Publish or CmsEventType.UnPublish)
            .WithMessage("Payload must be a non-null JSON object for publish/unPublish events.");

        // Payload sanitization: enforce a size cap per spec item 2 ("validated and sanitized").
        // JSON structure is already sanitized by the deserializer (malformed JSON never gets
        // this far); the payload is opaque per spec so we do not police its schema, but we do
        // cap its size to protect logs, storage, and memory.
        RuleFor(e => e.Payload)
            .Must(BeWithinPayloadSizeLimit)
            .When(e => e.Payload.HasValue)
            .WithMessage($"Payload exceeds the maximum allowed size of {MaxPayloadBytes} bytes.");
    }

    /// <summary>
    /// Accepts any ISO 8601 date/time. Values with an explicit offset (e.g. <c>+02:00</c>) are
    /// normalized to UTC by <c>AdjustToUniversal</c>; values with no offset are treated as UTC
    /// by <c>AssumeUniversal</c>. The Trade-off is documented in ADR-005: we accept-and-normalize
    /// rather than reject non-UTC because typo tolerance beats strictness at a webhook boundary,
    /// and the normalization is deterministic.
    /// </summary>
    private static bool BeValidIso8601(string? value) =>
        !string.IsNullOrEmpty(value) &&
        DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out _);

    private static bool BeWithinPayloadSizeLimit(JsonElement? payload)
    {
        if (!payload.HasValue)
        {
            return true;
        }

        // GetRawText returns the JSON as it appears on the wire; UTF-8 byte count matches the
        // bytes the deserializer consumed and the storage we will use.
        var rawText = payload.Value.GetRawText();
        return Encoding.UTF8.GetByteCount(rawText) <= MaxPayloadBytes;
    }
}
