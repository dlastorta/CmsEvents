namespace CmsEvents.Contracts.Events;

/// <summary>
/// Wire-format event type strings per spec sample. Case-sensitive constants used for
/// validation and internal Strategy dispatch (ADR-004).
/// </summary>
public static class CmsEventType
{
    public const string Publish = "publish";
    public const string UnPublish = "unPublish";
    public const string Delete = "delete";

    /// <summary>
    /// All valid wire-format values. Used by input validation (ADR-008).
    /// </summary>
    public static IReadOnlyCollection<string> All { get; } = new[]
    {
        Publish,
        UnPublish,
        Delete,
    };

    public static bool IsValid(string type) => All.Contains(type, StringComparer.Ordinal);
}
