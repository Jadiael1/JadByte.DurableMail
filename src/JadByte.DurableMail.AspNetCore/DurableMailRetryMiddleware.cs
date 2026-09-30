using Microsoft.AspNetCore.Http;

namespace JadByte.DurableMail.AspNetCore;

internal sealed class DurableMailRetryMiddleware(
    RequestDelegate next,
    MiddlewareRetryGate gate,
    bool enabled,
    Action signalRetries)
{
    internal Task InvokeAsync(HttpContext context)
    {
        if (enabled && gate.TryEnter())
        {
            signalRetries();
        }

        return next(context);
    }
}
