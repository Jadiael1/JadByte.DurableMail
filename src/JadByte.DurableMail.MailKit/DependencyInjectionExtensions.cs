using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace JadByte.DurableMail.MailKit;

/// <summary>Registers the MailKit SMTP adapter.</summary>
public static class DependencyInjectionExtensions
{
    /// <summary>Configures MailKit as the email transport and failure classifier.</summary>
    /// <param name="builder">The DurableMail registration builder.</param>
    /// <param name="configure">Configures SMTP delivery options.</param>
    /// <returns>The builder for further configuration.</returns>
    public static IDurableMailBuilder UseMailKit(this IDurableMailBuilder builder, Action<MailKitOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        builder.Services.AddOptions<MailKitOptions>().Configure(configure).ValidateOnStart();
        return AddAdapter(builder);
    }

    /// <summary>Configures MailKit using an application configuration section.</summary>
    /// <param name="builder">The DurableMail registration builder.</param>
    /// <param name="configuration">The SMTP options section.</param>
    /// <returns>The builder for further configuration.</returns>
    public static IDurableMailBuilder UseMailKit(this IDurableMailBuilder builder, IConfigurationSection configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);
        builder.Services.AddOptions<MailKitOptions>().Bind(configuration).ValidateOnStart();
        return AddAdapter(builder);
    }

    private static IDurableMailBuilder AddAdapter(IDurableMailBuilder builder)
    {
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<MailKitOptions>, MailKitOptionsValidator>());
        builder.Services.TryAddSingleton<ISmtpSessionFactory, SmtpSessionFactory>();
        builder.Services.Replace(ServiceDescriptor.Scoped<IEmailTransport, MailKitEmailTransport>());
        builder.Services.Replace(ServiceDescriptor.Singleton<IEmailFailureClassifier, MailKitFailureClassifier>());
        return builder;
    }
}
