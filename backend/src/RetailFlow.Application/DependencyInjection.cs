using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Platform.UseCases;

namespace RetailFlow.Application;

/// <summary>
/// Registration for the application layer. Use cases are registered one by one rather than by
/// convention scanning, so the decorator chain around each one is visible in source (ADR-0015).
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddValidatorsFromAssembly(ApplicationAssemblyMarker.Assembly, includeInternalTypes: true);
        services.TryAddSingleton(new IdempotencyOptions());
        services.TryAddSingleton(new OfflineLimitOptions());

        return services;
    }

    /// <summary>
    /// Registers a use case and wraps it in the standard pipeline. Order, from the outside in:
    /// logging, validation, then either idempotency (which owns its transaction) or a plain
    /// transaction, then the handler. A request that implements neither
    /// <see cref="IIdempotentRequest"/> nor <see cref="ITransactionalRequest"/> gets no transaction,
    /// which is what read-only queries want.
    /// </summary>
    public static IServiceCollection AddUseCase<TRequest, TResponse, THandler>(this IServiceCollection services)
        where TRequest : notnull
        where THandler : class, IUseCase<TRequest, TResponse>
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<THandler>();
        services.AddScoped<IUseCase<TRequest, TResponse>>(provider =>
        {
            IUseCase<TRequest, TResponse> pipeline = provider.GetRequiredService<THandler>();

            if (typeof(IIdempotentRequest).IsAssignableFrom(typeof(TRequest)))
            {
                var decoratorType = typeof(IdempotentUseCase<,>).MakeGenericType(typeof(TRequest), typeof(TResponse));
                pipeline = (IUseCase<TRequest, TResponse>)ActivatorUtilities.CreateInstance(
                    provider,
                    decoratorType,
                    pipeline);
            }
            else if (typeof(ITransactionalRequest).IsAssignableFrom(typeof(TRequest)))
            {
                pipeline = new TransactionalUseCase<TRequest, TResponse>(
                    pipeline,
                    provider.GetRequiredService<IUnitOfWork>());
            }

            pipeline = new ValidatingUseCase<TRequest, TResponse>(
                pipeline,
                provider.GetServices<IValidator<TRequest>>());

            pipeline = new LoggingUseCase<TRequest, TResponse>(
                pipeline,
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LoggingUseCase<TRequest, TResponse>>>(),
                provider.GetRequiredService<ITenantContext>(),
                provider.GetRequiredService<ICurrentUser>(),
                provider.GetRequiredService<ICorrelationContext>());

            return pipeline;
        });

        return services;
    }
}
