namespace CmsEvents.Application.Features.ListEntities;

using System.Text.Json;
using CmsEvents.Application.Common.Repositories;
using CmsEvents.Contracts.Responses;
using CmsEvents.Domain.Entities;
using CmsEvents.Domain.Enums;
using MediatR;

/// <summary>
/// Handles <see cref="ListEntitiesQuery"/>. Applies role-based filter per ADR-007 and role-aware
/// DTO projection (Status/IsDisabled fields present only for Admin) per responses.md.
/// </summary>
public sealed class ListEntitiesHandler : IRequestHandler<ListEntitiesQuery, ListEntitiesResponse>
{
    private readonly IEntityQueries _queries;

    public ListEntitiesHandler(IEntityQueries queries)
    {
        _queries = queries;
    }

    public async Task<ListEntitiesResponse> Handle(ListEntitiesQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var isAdmin = request.Role == UserRole.Admin;
        var entities = await _queries.ListAsync(
            includeHidden: isAdmin,
            limit: request.Limit,
            cancellationToken);

        var responses = entities.Select(entity => Map(entity, isAdmin)).ToList();

        return new ListEntitiesResponse
        {
            CorrelationId = request.CorrelationId,
            Count = responses.Count,
            Entities = responses,
        };
    }

    internal static EntityResponse Map(Entity entity, bool isAdmin) => new()
    {
        Id = entity.Id,
        Version = entity.LastProcessedVersion,
        Status = isAdmin ? entity.Status.ToString() : null,
        IsDisabled = isAdmin ? entity.IsDisabled : null,
        Timestamp = entity.LastProcessedTimestamp,
        Payload = JsonDocument.Parse(entity.Payload).RootElement.Clone(),
    };
}
