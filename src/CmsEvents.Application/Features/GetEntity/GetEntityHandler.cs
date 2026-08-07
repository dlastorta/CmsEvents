namespace CmsEvents.Application.Features.GetEntity;

using CmsEvents.Application.Common.Repositories;
using CmsEvents.Application.Features.ListEntities;
using CmsEvents.Contracts.Responses;
using CmsEvents.Domain.Enums;
using MediatR;

/// <summary>
/// Handles <see cref="GetEntityQuery"/>. Uses <see cref="IEntityQueries"/> with role-based
/// visibility filter per ADR-007. Returns null if the entity does not exist or is filtered
/// out — the API layer maps null to a 404 response.
/// </summary>
public sealed class GetEntityHandler : IRequestHandler<GetEntityQuery, EntityResponse?>
{
    private readonly IEntityQueries _queries;

    public GetEntityHandler(IEntityQueries queries)
    {
        _queries = queries;
    }

    public async Task<EntityResponse?> Handle(GetEntityQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var isAdmin = request.Role == UserRole.Admin;
        var entity = await _queries.FindByIdAsync(request.Id, includeHidden: isAdmin, cancellationToken);

        return entity is null ? null : ListEntitiesHandler.Map(entity, isAdmin);
    }
}
