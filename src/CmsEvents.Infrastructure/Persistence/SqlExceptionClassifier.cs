namespace CmsEvents.Infrastructure.Persistence;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Classifies <see cref="DbUpdateException"/> as transient (retry-worthy) or permanent (do not
/// retry — retrying just delays the same error). Uses SQL Server error numbers per Microsoft's
/// transient-fault guidance. Anything unknown defaults to <em>permanent</em> (fail-safe: better
/// to surface a real bug than to mask it as a spurious timeout).
///
/// References:
/// <list type="bullet">
///   <item><description>Azure SQL transient error codes: https://learn.microsoft.com/azure/azure-sql/database/troubleshoot-common-connectivity-issues</description></item>
///   <item><description>Constraint / integrity violations: https://learn.microsoft.com/sql/relational-databases/errors-events/database-engine-events-and-errors</description></item>
/// </list>
/// </summary>
internal static class SqlExceptionClassifier
{
    /// <summary>
    /// SQL Server error numbers considered transient by Microsoft. All are network-, resource-,
    /// or contention-related and typically resolve within one or two retries. Anything NOT in
    /// this set is treated as permanent.
    /// </summary>
    private static readonly HashSet<int> TransientErrorNumbers = new()
    {
        -2,     // Command timeout
        20,     // Instance failure (transient during failover)
        64,     // Connection failed
        233,    // Connection init error (transient during failover)
        1205,   // Deadlock victim — SQL Server chose this session to abort
        4060,   // Cannot open database (transient during Azure failover; also can be config)
        10053,  // Transport-level error — established connection was aborted by the software in your host machine
        10054,  // Transport-level error — connection reset by peer
        10060,  // Network error (timeout waiting for response)
        10928,  // Resource limit reached (concurrent request count)
        10929,  // Resource limit reached (minimum guarantee not met)
        40197,  // Service busy — retry
        40501,  // Service busy
        40613,  // Database unavailable (transient during Azure failover)
    };

    /// <summary>
    /// Returns <c>true</c> if the given <see cref="DbUpdateException"/> represents a transient
    /// failure that could reasonably succeed on retry. Returns <c>false</c> for permanent failures
    /// (constraint violations, optimistic-concurrency conflicts, unknown errors — fail-safe).
    /// </summary>
    public static bool IsTransient(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Optimistic-concurrency conflicts are NEVER transient — the data changed underneath us
        // and retrying without re-reading will just conflict again (or worse, silently overwrite).
        if (exception is DbUpdateConcurrencyException)
        {
            return false;
        }

        var sqlErrorNumber = ExtractSqlErrorNumber(exception);
        return sqlErrorNumber.HasValue && IsTransient(sqlErrorNumber.Value);
    }

    /// <summary>
    /// Direct check by SQL Server error number. Exposed for unit tests that don't need to
    /// construct a full <see cref="SqlException"/> (its constructors are internal).
    /// </summary>
    public static bool IsTransient(int sqlErrorNumber) => TransientErrorNumbers.Contains(sqlErrorNumber);

    /// <summary>
    /// Walks the exception chain looking for a <see cref="SqlException"/> and returns its Number,
    /// or <c>null</c> if none is found. EF Core typically wraps <c>SqlException</c> inside
    /// <c>DbUpdateException.InnerException</c>, but the chain can be deeper if middleware wraps.
    /// </summary>
    public static int? ExtractSqlErrorNumber(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlEx)
            {
                return sqlEx.Number;
            }
        }

        return null;
    }
}
