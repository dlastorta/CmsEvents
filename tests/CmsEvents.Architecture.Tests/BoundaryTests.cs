namespace CmsEvents.Architecture.Tests;

using System.Reflection;
using CmsEvents.Api;
using CmsEvents.Application.DependencyInjection;
using CmsEvents.Contracts.Events;
using CmsEvents.Domain.Entities;
using CmsEvents.Infrastructure.DependencyInjection;
using FluentAssertions;
using MediatR;
using NetArchTest.Rules;
using Xunit;

/// <summary>
/// Boundary rules encoded per ADR-002 (and updated per ADR-010 revision).
/// Each fact fails the build if a rule is violated. Errors include the offending types
/// via FluentAssertions' <c>because</c> for actionable failure messages.
/// </summary>
public sealed class BoundaryTests
{
    // Anchor types for each assembly under test.
    private static readonly Assembly DomainAssembly = typeof(Entity).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(ApplicationServiceCollectionExtensions).Assembly;
    private static readonly Assembly ContractsAssembly = typeof(CmsEventEnvelope).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(InfrastructureServiceCollectionExtensions).Assembly;
    private static readonly Assembly ApiAssembly = typeof(Program).Assembly;

    [Fact]
    public void Rule1_Domain_DoesNotDependOnAnyProjectAssembly()
    {
        var result = Types.InAssembly(DomainAssembly)
            .Should()
            .NotHaveDependencyOnAny(
                ApplicationAssembly.GetName().Name,
                InfrastructureAssembly.GetName().Name,
                ApiAssembly.GetName().Name,
                ContractsAssembly.GetName().Name)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(FormatFailure(result));
    }

    [Fact]
    public void Rule2_Application_DoesNotDependOnApi_Or_Infrastructure()
    {
        var result = Types.InAssembly(ApplicationAssembly)
            .Should()
            .NotHaveDependencyOnAny(
                ApiAssembly.GetName().Name,
                InfrastructureAssembly.GetName().Name)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(FormatFailure(result));
    }

    [Fact]
    public void Rule3_Application_DoesNotDependOnAspNetCoreTransportNamespaces()
    {
        var result = Types.InAssembly(ApplicationAssembly)
            .Should()
            .NotHaveDependencyOnAny(
                "Microsoft.AspNetCore.Http",
                "Microsoft.AspNetCore.Mvc",
                "Microsoft.AspNetCore.Routing")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(FormatFailure(result));
    }

    [Fact]
    public void Rule4_Infrastructure_DoesNotDependOnApi()
    {
        var result = Types.InAssembly(InfrastructureAssembly)
            .Should()
            .NotHaveDependencyOn(ApiAssembly.GetName().Name)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(FormatFailure(result));
    }

    [Fact]
    public void Rule5_MediatR_Handlers_LiveOnlyInApplication()
    {
        var handlerInterfaces = new[]
        {
            typeof(IRequestHandler<>).FullName!,
            typeof(IRequestHandler<,>).FullName!,
        };

        // Any type implementing IRequestHandler outside Application → violation.
        foreach (var assembly in new[] { DomainAssembly, InfrastructureAssembly, ApiAssembly, ContractsAssembly })
        {
            var offenders = assembly.GetTypes()
                .Where(t => t.GetInterfaces().Any(i =>
                    i.IsGenericType &&
                    handlerInterfaces.Contains(i.GetGenericTypeDefinition().FullName)))
                .ToList();

            offenders.Should().BeEmpty(
                $"MediatR handlers must reside only in Application, but {assembly.GetName().Name} contains: " +
                string.Join(", ", offenders.Select(t => t.FullName)));
        }
    }

    [Fact]
    public void Rule6_ContractsTypes_ArePublicAndSealed()
    {
        var offenders = ContractsAssembly.GetTypes()
            .Where(t => t.IsClass && t.IsPublic && !t.IsSealed && !t.IsAbstract)
            .Where(t => t.Namespace?.StartsWith("CmsEvents.Contracts", StringComparison.Ordinal) == true)
            .ToList();

        offenders.Should().BeEmpty(
            "public classes in Contracts should be sealed to prevent external inheritance; offenders: " +
            string.Join(", ", offenders.Select(t => t.FullName)));
    }

    [Fact]
    public void Rule7_Application_DoesNotDependOnEntityFrameworkCore()
    {
        var result = Types.InAssembly(ApplicationAssembly)
            .Should()
            .NotHaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Application must be ORM-agnostic per ADR-010 revision. " + FormatFailure(result));
    }

    private static string FormatFailure(TestResult result) =>
        result.FailingTypeNames is null
            ? "No offending types reported."
            : "Offending types: " + string.Join(", ", result.FailingTypeNames);
}
