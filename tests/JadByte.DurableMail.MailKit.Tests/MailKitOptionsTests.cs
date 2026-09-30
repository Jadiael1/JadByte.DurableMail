using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace JadByte.DurableMail.MailKit.Tests;

public sealed class MailKitOptionsTests
{
    [Fact]
    public void DefaultsRequireTlsAndUseThirtySecondTimeout()
    {
        var options = new MailKitOptions();
        Assert.Equal(587, options.Port);
        Assert.Equal(MailKitSecurityMode.StartTls, options.SecurityMode);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", "", true)]
    [InlineData("account", "secret", true)]
    [InlineData("account", null, false)]
    [InlineData(null, "secret", false)]
    [InlineData(" ", "secret", false)]
    public void CredentialsMustBeOmittedOrProvidedTogether(string? username, string? password, bool valid)
    {
        var options = ValidOptions();
        options.Username = username;
        options.Password = password;
        Assert.Equal(valid, new MailKitOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void InvalidPortIsRejected(int port)
    {
        var options = ValidOptions();
        options.Port = port;
        Assert.True(new MailKitOptionsValidator().Validate(null, options).Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2147483648)]
    public void InvalidTimeoutIsRejected(double milliseconds)
    {
        var options = ValidOptions();
        options.Timeout = TimeSpan.FromMilliseconds(milliseconds);
        Assert.True(new MailKitOptionsValidator().Validate(null, options).Failed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://smtp.example.com")]
    [InlineData("smtp example.com")]
    public void InvalidHostIsRejected(string host)
    {
        var options = ValidOptions();
        options.Host = host;
        Assert.True(new MailKitOptionsValidator().Validate(null, options).Failed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a valid <")]
    [InlineData("sender@example.com\r\nBcc:someone@example.com")]
    public void InvalidSenderIsRejected(string sender)
    {
        var options = ValidOptions();
        options.FromEmail = sender;
        Assert.True(new MailKitOptionsValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void UnknownSecurityModeIsRejected()
    {
        var options = ValidOptions();
        options.SecurityMode = (MailKitSecurityMode)999;
        Assert.True(new MailKitOptionsValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void AdapterRegistersStartupValidationWithoutExposingCredentials()
    {
        var services = new ServiceCollection();
        var builder = new TestBuilder(services);
        builder.UseMailKit(options =>
        {
            options.Host = "smtp.example.com";
            options.FromEmail = "sender@example.com";
            options.Password = "do-not-expose-this-password";
        });
        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
        Assert.DoesNotContain("do-not-expose-this-password", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AdapterRegistersItsTransportAndClassifier()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        var builder = new TestBuilder(services);
        Assert.Same(builder, builder.UseMailKit(options =>
        {
            options.Host = "smtp.example.com";
            options.FromEmail = "sender@example.com";
        }));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        Assert.IsType<MailKitEmailTransport>(scope.ServiceProvider.GetRequiredService<IEmailTransport>());
        Assert.IsType<MailKitFailureClassifier>(scope.ServiceProvider.GetRequiredService<IEmailFailureClassifier>());
        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    internal static MailKitOptions ValidOptions() => new()
    {
        Host = "smtp.example.com",
        FromEmail = "sender@example.com",
    };

    private sealed record TestBuilder(IServiceCollection Services) : IDurableMailBuilder;
}
