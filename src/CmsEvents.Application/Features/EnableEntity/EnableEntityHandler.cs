namespace CmsEvents.Application.Features.EnableEntity;

using CmsEvents.Application.Common.Repositories;
using CmsEvents.Contracts.Responses;
using CmsEvents.Domain.Abstractions;
using MediatR;

/// <summary>
/// Handles <see cref="EnableEntityCommand"/>. Loads the entity via the writer repository,
/// clears the admin flag (idempotent) per ADR-007, and persists.
/// </summary>
public sealed class EnableEntityHandler : IRequestHandler<EnableEntityCommand, DisableEnableResponse?>
{
    private readonly IEntityRepository _repository;
    private readonly IClock _clock;

    public EnableEntityHandler(IEntityRepository repository, IClock clock)
    {
        _repository = repository;
        _clock = clock;
    }

    public async Task<DisableEnableResponse?> Handle(EnableEntityCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var entity = await _repository.FindByIdAsync(request.Id, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        entity.Enable(_clock.UtcNow);
        await _repository.SaveChangesAsync(cancellationToken);

        return new DisableEnableResponse
        {
            CorrelationId = request.CorrelationId,
            Id = entity.Id,
            IsDisabled = entity.IsDisabled,
        };
    }
}
