namespace CmsEvents.Application.Features.ProcessEventBatch;

using System.Globalization;
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
    public CmsEventValidator()
    {
        RuleFor(e => e.Type)
            .NotEmpty()
            .Must(CmsEventType.IsValid)
            .WithMessage("Type must be one of: publish, unPublish, delete (case-sensitive).");

        RuleFor(e => e.Id)
            .NotEmpty()
            .WithMessage("Id must be non-empty.");

        RuleFor(e => e.Timestamp)
            .NotEmpty()
            .Must(BeValidIso8601Utc)
            .WithMessage("Timestamp must be a valid ISO 8601 UTC value (e.g., \"2026-08-01T10:00:00Z\").");

        // Version required for publish/unPublish; must be absent for delete.
        RuleFor(e => e.Version)
            .NotNull()
            .GreaterThanOrEqualTo(1)
            .When(e => e.Type is CmsEventType.Publish or CmsEventType.UnPublish)
            .WithMessage("Version must be >= 1 for publish/unPublish events.");

        // Payload required for publish/unPublish; may be absent for delete (opaque per spec).
        RuleFor(e => e.Payload)
            .NotNull()
            .When(e => e.Type is CmsEventType.Publish or CmsEventType.UnPublish)
            .WithMessage("Payload must be a non-null JSON object for publish/unPublish events.");
    }

    private static bool BeValidIso8601Utc(string? value) =>
        !string.IsNullOrEmpty(value) &&
        DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out _);
}
