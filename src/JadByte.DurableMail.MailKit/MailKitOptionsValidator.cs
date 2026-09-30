using Microsoft.Extensions.Options;
using MimeKit;

namespace JadByte.DurableMail.MailKit;

internal sealed class MailKitOptionsValidator : IValidateOptions<MailKitOptions>
{
    public ValidateOptionsResult Validate(string? name, MailKitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Host) || Uri.CheckHostName(options.Host) == UriHostNameType.Unknown)
        {
            errors.Add("MailKit Host must be a hostname or IP address.");
        }

        if (options.Port is < 1 or > 65535)
        {
            errors.Add("MailKit Port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(options.FromEmail) ||
            options.FromEmail.Contains('\r', StringComparison.Ordinal) ||
            options.FromEmail.Contains('\n', StringComparison.Ordinal) ||
            !MailboxAddress.TryParse(options.FromEmail, out _))
        {
            errors.Add("MailKit FromEmail must be a valid mailbox address.");
        }

        if ((!string.IsNullOrEmpty(options.Username) && string.IsNullOrWhiteSpace(options.Username)) ||
            string.IsNullOrEmpty(options.Username) != string.IsNullOrEmpty(options.Password))
        {
            errors.Add("MailKit Username and Password must both be supplied or both be omitted.");
        }

        if (!Enum.IsDefined(options.SecurityMode))
        {
            errors.Add("MailKit SecurityMode must be a defined value.");
        }

        if (options.Timeout < TimeSpan.FromMilliseconds(1) || options.Timeout.TotalMilliseconds > int.MaxValue)
        {
            errors.Add("MailKit Timeout must be between one millisecond and 2147483647 milliseconds.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
