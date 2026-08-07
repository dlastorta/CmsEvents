namespace CmsEvents.Unit.Tests.Domain;

using CmsEvents.Domain.Entities;
using CmsEvents.Domain.Enums;
using FluentAssertions;
using Xunit;

/// <summary>
/// Covers <see cref="User"/> — factory validation and password rotation per ADR-011.
/// </summary>
public sealed class UserTests
{
    private static readonly DateTime Now = new(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("cms-webhook-user")]     // 16 chars
    [InlineData("readonly-user")]        // 13 chars
    [InlineData("admin-user")]           // 10 chars — boundary
    [InlineData("aaaaaaaaaaaaaaaaaaaa")] // 20 chars — boundary
    public void Create_UsernameWithinSpecBounds_Succeeds(string username)
    {
        var user = User.Create(username, passwordHash: "$2a$11$hash", role: UserRole.User, now: Now);

        user.Username.Should().Be(username);
        user.Role.Should().Be(UserRole.User);
        user.CreatedAt.Should().Be(Now);
        user.Id.Should().NotBe(Guid.Empty);
    }

    [Theory]
    [InlineData("nine-char")]                  // 9 chars — below minimum per spec item 1
    [InlineData("twentyonecharacterss!")]      // 21 chars — above max per spec item 1
    public void Create_UsernameOutsideBounds_Throws(string username)
    {
        var act = () => User.Create(username, passwordHash: "$2a$11$hash", role: UserRole.User, now: Now);

        act.Should().Throw<ArgumentException>().WithMessage("*10-20 characters*");
    }

    [Fact]
    public void Create_NullOrEmptyUsername_Throws()
    {
        var act = () => User.Create(username: string.Empty, passwordHash: "$2a$11$hash", role: UserRole.User, now: Now);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_NullOrEmptyPasswordHash_Throws()
    {
        var act = () => User.Create(username: "cms-webhook-user", passwordHash: string.Empty, role: UserRole.User, now: Now);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UpdatePasswordHash_ReplacesHash()
    {
        var user = User.Create("readonly-user", passwordHash: "$2a$11$oldhash", role: UserRole.User, now: Now);

        user.UpdatePasswordHash("$2a$11$newhash");

        user.PasswordHash.Should().Be("$2a$11$newhash");
    }

    [Fact]
    public void UpdatePasswordHash_EmptyValue_Throws()
    {
        var user = User.Create("readonly-user", passwordHash: "$2a$11$oldhash", role: UserRole.User, now: Now);

        var act = () => user.UpdatePasswordHash(string.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void HasRole_IsExactMatch()
    {
        var user = User.Create("admin-user", passwordHash: "$2a$11$hash", role: UserRole.Admin, now: Now);

        user.HasRole(UserRole.Admin).Should().BeTrue();
        user.HasRole(UserRole.User).Should().BeFalse();
        user.HasRole(UserRole.Organization).Should().BeFalse();
    }
}
