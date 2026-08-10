namespace CmsEvents.Unit.Tests.Infrastructure.Persistence;

using CmsEvents.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Verifies the transient / permanent taxonomy used to decide whether a persistence failure
/// should be Polly-retried. Focuses on the <see cref="SqlExceptionClassifier.IsTransient(int)"/>
/// overload because <c>SqlException</c> has internal constructors and cannot be instantiated
/// directly in tests. Integration behavior (SqlException chain walking) is exercised via the
/// integration tests.
/// </summary>
public sealed class SqlExceptionClassifierTests
{
    [Theory]
    [InlineData(1205)]  // Deadlock victim
    [InlineData(-2)]    // Command timeout
    [InlineData(233)]   // Connection init error
    [InlineData(10053)] // Transport-level error
    [InlineData(10054)] // Connection reset
    [InlineData(10060)] // Network error (timeout)
    [InlineData(40197)] // Azure SQL service busy
    [InlineData(40501)] // Azure SQL service busy
    [InlineData(40613)] // Database unavailable
    [InlineData(10928)] // Resource limit reached
    public void IsTransient_KnownTransientErrorNumber_ReturnsTrue(int sqlErrorNumber)
    {
        SqlExceptionClassifier.IsTransient(sqlErrorNumber).Should().BeTrue(
            "SQL error number {0} is on the Microsoft transient-fault list and should be retried",
            sqlErrorNumber);
    }

    [Theory]
    [InlineData(2627)] // Primary-key / unique-constraint violation
    [InlineData(2601)] // Unique index violation
    [InlineData(547)]  // Foreign-key / CHECK constraint failure
    [InlineData(515)]  // NULL insert into non-null column
    [InlineData(8152)] // String truncation
    [InlineData(8114)] // Conversion error
    [InlineData(245)]  // Conversion error
    [InlineData(3906)] // Cannot modify read-only database
    public void IsTransient_KnownPermanentErrorNumber_ReturnsFalse(int sqlErrorNumber)
    {
        SqlExceptionClassifier.IsTransient(sqlErrorNumber).Should().BeFalse(
            "SQL error number {0} is a constraint / data integrity failure — retrying just delays the same error",
            sqlErrorNumber);
    }

    [Fact]
    public void IsTransient_UnknownErrorNumber_ReturnsFalse()
    {
        // Fail-safe default: unknown errors are treated as permanent so real bugs surface with
        // their own reason instead of being masked as timeouts.
        SqlExceptionClassifier.IsTransient(999999).Should().BeFalse();
    }

    [Fact]
    public void IsTransient_DbUpdateConcurrencyException_ReturnsFalse()
    {
        // Optimistic concurrency conflicts are NEVER transient — retrying without re-reading
        // would silently overwrite the change that raced ahead of us.
        var concurrency = new DbUpdateConcurrencyException("test");
        SqlExceptionClassifier.IsTransient(concurrency).Should().BeFalse();
    }

    [Fact]
    public void IsTransient_DbUpdateException_NoSqlExceptionInChain_ReturnsFalse()
    {
        // Fail-safe: if we can't find a SqlException to classify, treat as permanent.
        var dbEx = new DbUpdateException("test", new InvalidOperationException("unrelated cause"));
        SqlExceptionClassifier.IsTransient(dbEx).Should().BeFalse();
    }

    [Fact]
    public void ExtractSqlErrorNumber_NoSqlExceptionInChain_ReturnsNull()
    {
        var dbEx = new DbUpdateException("test", new InvalidOperationException("unrelated"));
        SqlExceptionClassifier.ExtractSqlErrorNumber(dbEx).Should().BeNull();
    }
}
