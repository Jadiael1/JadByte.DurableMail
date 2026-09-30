using JadByte.DurableMail.Processing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace JadByte.DurableMail.AspNetCore;

/// <summary>Integrates opportunistic email retry signaling into the HTTP pipeline.</summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>Adds the optional retry trigger, throttled once per configured interval in this application instance.</summary>
    /// <remarks>
    /// Place after exception handling and before endpoint execution. Authentication is not required.
    /// Requests only signal the dispatcher; they never await email delivery or access its storage.
    /// The trigger remains inactive unless <see cref="MiddlewareRetryTriggerOptions.Enabled"/> is enabled.
    /// </remarks>
    /// <param name="app">The application pipeline builder.</param>
    /// <returns>The application builder.</returns>
    public static IApplicationBuilder UseDurableMailRetryTrigger(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var services = app.ApplicationServices;
        var options = services.GetRequiredService<IOptions<DurableMailOptions>>().Value.Retries.Middleware;
        var gate = new MiddlewareRetryGate(services.GetRequiredService<TimeProvider>(), options.Interval);
        var coordinator = services.GetRequiredService<EmailDispatchCoordinator>();
        return app.Use(next => new DurableMailRetryMiddleware(next, gate, options.Enabled, coordinator.SignalRetries).InvokeAsync);
    }
}
