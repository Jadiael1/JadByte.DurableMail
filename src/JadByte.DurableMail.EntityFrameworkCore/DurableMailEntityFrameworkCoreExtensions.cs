using JadByte.DurableMail.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JadByte.DurableMail.EntityFrameworkCore;

/// <summary>Registers the EF Core persistence adapter.</summary>
public static class DurableMailEntityFrameworkCoreExtensions
{
    /// <summary>Uses the application's registered context in scopes owned by DurableMail.</summary>
    /// <typeparam name="TContext">The context whose model calls <see cref="DurableMailModelBuilderExtensions.AddDurableMail"/>.</typeparam>
    /// <param name="builder">The DurableMail registration builder.</param>
    /// <returns>The builder for further adapter configuration.</returns>
    public static IDurableMailBuilder UseEntityFrameworkCore<TContext>(this IDurableMailBuilder builder)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Scoped<IEmailDeliveryStore, EfEmailDeliveryStore<TContext>>());
        return builder;
    }
}
