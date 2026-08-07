namespace CmsEvents.Unit.Tests.Contracts;

using CmsEvents.Contracts.Events;
using FluentAssertions;
using Xunit;

/// <summary>
/// Covers <see cref="CmsEventType"/> — case-sensitive validation per ADR-008 sample.
/// </summary>
public sealed class CmsEventTypeTests
{
    [Theory]
    [InlineData("publish")]
    [InlineData("unPublish")]
    [InlineData("delete")]
    public void IsValid_KnownLowerAndCamelCase_ReturnsTrue(string type)
    {
        CmsEventType.IsValid(type).Should().BeTrue();
    }

    [Theory]
    [InlineData("Publish")]      // PascalCase not allowed
    [InlineData("PUBLISH")]      // Uppercase not allowed
    [InlineData("unpublish")]    // Missing camelCase P
    [InlineData("DELETE")]
    [InlineData("archive")]      // Not in the enum
    [InlineData("")]
    public void IsValid_UnknownOrWrongCase_ReturnsFalse(string type)
    {
        CmsEventType.IsValid(type).Should().BeFalse();
    }

    [Fact]
    public void All_ExposesTheThreeSupportedValues()
    {
        CmsEventType.All.Should().BeEquivalentTo(new[]
        {
            CmsEventType.Publish,
            CmsEventType.UnPublish,
            CmsEventType.Delete,
        });
    }
}
