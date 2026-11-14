#if DEBUG
using PointOfSale.API.Services.Auth;

namespace PointOfSale.API.Development;

/// <summary>Local opt-in mailbox for one development identity; never activates an identity.</summary>
public sealed class AffiliateP53dMailSink(
    IWebHostEnvironment environment, SmtpEmailSender smtp) : IEmailSender
{
    public const string Recipient = "affiliate.p53d.20261023@example.test";
    public const string ShopperRecipient = "shopper.p53d.20261023@example.test";
    public static string DirectoryPath(string contentRoot) => Path.GetFullPath(Path.Combine(
        contentRoot, "..", ".artifacts", "affiliate-p53d-private"));

    public async Task SendEmailAsync(string to, string subject, string htmlBody, string textBody)
    {
        var directory = DirectoryPath(environment.ContentRootPath);
        var isAffiliateMail = to == Recipient && subject == "Verify your affiliate account";
        var isShopperMail = to == ShopperRecipient && subject == "Verify your email address";
        if (environment.IsDevelopment() && (isAffiliateMail || isShopperMail) &&
            File.Exists(Path.Combine(directory, "mail.enabled")))
        {
            // Credential-bearing content stays outside reports/logs and is consumed by the browser.
            await File.WriteAllTextAsync(Path.Combine(directory,
                isAffiliateMail ? "verification-mail.txt" : "shopper-verification-mail.txt"), textBody);
            return;
        }
        await smtp.SendEmailAsync(to, subject, htmlBody, textBody);
    }
}
#endif
