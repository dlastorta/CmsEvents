namespace CmsEvents.Domain.Enums;

/// <summary>
/// Publication state of a CMS entity, driven by CMS webhook events per ADR-005.
/// </summary>
public enum EntityStatus
{
    Published = 1,
    Unpublished = 2,
}
