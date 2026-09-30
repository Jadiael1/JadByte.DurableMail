using Microsoft.Extensions.DependencyInjection;

namespace JadByte.DurableMail;

/// <summary>Provides access to services when configuring DurableMail adapters.</summary>
public interface IDurableMailBuilder
{
    /// <summary>Gets the service collection being configured.</summary>
    IServiceCollection Services { get; }
}
