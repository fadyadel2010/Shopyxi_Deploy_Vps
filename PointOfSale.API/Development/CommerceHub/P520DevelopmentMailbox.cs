using PointOfSale.API.Services.Auth;

namespace PointOfSale.API.Development.CommerceHub;

/// <summary>Opt-in, exact-run mailbox. Never changes verification or identity authority.</summary>
public sealed class P520DevelopmentMailbox(
    IWebHostEnvironment environment,
    IConfiguration configuration,
    IEmailSender fallback) : IEmailSender
{
    public static string DirectoryPath => Path.Combine(Path.GetTempPath(), "opencode", "p520d-mail");

    public static bool CanCapture(string environmentName, bool enabled, string recipient) =>
        string.Equals(environmentName, Environments.Development, StringComparison.Ordinal) && enabled &&
        recipient.StartsWith("p520d-20261028-e2e01-", StringComparison.OrdinalIgnoreCase) &&
        recipient.EndsWith("@example.test", StringComparison.OrdinalIgnoreCase) &&
        recipient.Count(character => character == '@') == 1 &&
        recipient.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '@');

    public async Task SendEmailAsync(string to, string subject, string htmlBody, string textBody)
    {
        if (CanCapture(environment.EnvironmentName,
                configuration.GetValue<bool>("CommerceHub:DevelopmentAcceptance:CaptureMail"), to))
        {
            Directory.CreateDirectory(DirectoryPath);
            await File.WriteAllTextAsync(Path.Combine(DirectoryPath, to.ToLowerInvariant() + ".txt"), textBody);
            return;
        }

        // Reserved test recipients must never escape to an external delivery transport.
        if (to.EndsWith("@example.test", StringComparison.OrdinalIgnoreCase) &&
            to.StartsWith("p520d-", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Development acceptance mailbox is not enabled for this identity.");
        }

        await fallback.SendEmailAsync(to, subject, htmlBody, textBody);
    }
}
