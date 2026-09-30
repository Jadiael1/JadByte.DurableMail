using JadByte.DurableMail.Processing;
using JadByte.DurableMail.Retries;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace JadByte.DurableMail;

/// <summary>Registers durable email processing and its replaceable defaults.</summary>
public static class DependencyInjectionExtensions
{
    /// <summary>Adds durable email delivery. Register a storage adapter and a transport separately.</summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="configure">Optional dispatch and retry configuration.</param>
    /// <returns>A builder for storage and transport adapters.</returns>
    public static IDurableMailBuilder AddDurableMail(this IServiceCollection services, Action<DurableMailOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<DurableMailOptions>().ValidateOnStart();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DurableMailOptions>, DurableMailOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRetryDelayCalculator, ExponentialRetryDelayCalculator>();
        services.TryAddSingleton<RetryScheduleCoordinator>();
        services.TryAddSingleton<EmailDispatchRunner>();
        services.TryAddSingleton<EmailDispatchCoordinator>();
        services.TryAddTransient<EmailDeliveryExecutor>();
        services.TryAddTransient<IEmailQueue, EmailQueue>();
        services.TryAddTransient<IEmailRetryDispatcher, EmailRetryDispatcher>();
        services.TryAddTransient<IEmailDeliveryMaintenance, EmailDeliveryMaintenance>();
        services.TryAddSingleton<IEmailFailureClassifier, DefaultEmailFailureClassifier>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DurableMailLifecycleService>());
        return new DurableMailBuilder(services);
    }

    /// <summary>Adds durable email delivery using configuration binding.</summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="configuration">The DurableMail configuration section.</param>
    /// <returns>A builder for storage and transport adapters.</returns>
    public static IDurableMailBuilder AddDurableMail(this IServiceCollection services, IConfigurationSection configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var builder = services.AddDurableMail();
        services.AddOptions<DurableMailOptions>().Bind(configuration);
        return builder;
    }

    private sealed class DurableMailBuilder(IServiceCollection services) : IDurableMailBuilder
    {
        public IServiceCollection Services { get; } = services;
    }

    private sealed class DefaultEmailFailureClassifier : IEmailFailureClassifier
    {
        public EmailFailureKind Classify(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            return EmailFailureKind.Transient;
        }
    }
}
