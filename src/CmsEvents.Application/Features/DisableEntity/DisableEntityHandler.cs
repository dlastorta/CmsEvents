namespace CmsEvents.Application.Features.DisableEntity;

using CmsEvents.Application.Common.Repositories;
using CmsEvents.Contracts.Responses;
using CmsEvents.Domain.Abstractions;
using MediatR;

/// <summary>
/// Handles <see cref="DisableEntityCommand"/>. Loads the entity via the writer repository,
/// applies the sticky admin flag (idempotent) per ADR-007, and persists.
/// </summary>
public sealed class DisableEntityHandler : IRequestHandler<DisableEntityCommand, DisableEnableResponse?>
{
    private readonly IEntityRepository _repository;
    private readonly IClock _clock;

    public DisableEntityHandler(IEntityRepository repository, IClock clock)
    {
        _repository = repository;
        _clock = clock;
    }

    public async Task<DisableEnableResponse?> Handle(DisableEntityCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var entity = await _repository.FindByIdAsync(request.Id, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        entity.Disable(_clock.UtcNow);
        await _repository.SaveChangesAsync(cancellationToken);

        return new DisableEnableResponse
        {
            CorrelationId = request.CorrelationId,
            Id = entity.Id,
            IsDisabled = entity.IsDisabled,
        };
    }
}
