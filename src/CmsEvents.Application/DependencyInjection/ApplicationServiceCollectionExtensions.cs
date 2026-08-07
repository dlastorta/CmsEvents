namespace CmsEvents.Application.DependencyInjection;

using System.Reflection;
using CmsEvents.Application.EventProcessing;
using CmsEvents.Application.EventProcessing.Handlers;
using CmsEvents.Application.Features.ProcessEventBatch;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Wires up MediatR handlers, FluentValidation validators, and the internal event Strategy
/// dispatch (ADR-004) from the Application assembly. Called by the Api composition root
/// (per ADR-001).
/// </summary>
public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var applicationAssembly = Assembly.GetExecutingAssembly();

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(applicationAssembly));
        services.AddValidatorsFromAssembly(applicationAssembly);

        AddEventProcessing(services);

        return services;
    }

    private static void AddEventProcessing(IServiceCollection services)
    {
        // Per-event validator (not part of MediatR pipeline — called explicitly by
        // ProcessEventBatchHandler so validation failures become permanent-failure outcomes
        // instead of throwing, per ADR-008).
        services.AddScoped<CmsEventValidator>();

        // Strategy handlers per event type (ADR-004). Explicit registration preferred over
        // assembly scanning to catch missing handlers at composition time rather than dispatch time.
        services.AddScoped<IEventHandler, PublishEventHandler>();
        services.AddScoped<IEventHandler, UnPublishEventHandler>();
        services.AddScoped<IEventHandler, DeleteEventHandler>();

        // Dispatcher resolves all IEventHandler registrations into a type-indexed map.
        services.AddScoped<EventDispatcher>();
    }
}
