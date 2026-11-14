#if DEBUG
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PointOfSale.API.Development;
using PointOfSale.API.Services.Auth;

namespace PointOfSale.Tests.Affiliate;

public class AffiliateP53dMailSinkTests
{
    [Theory]
    [InlineData("Production", true, AffiliateP53dMailSink.Recipient, "Verify your affiliate account", false)]
    [InlineData("Staging", true, AffiliateP53dMailSink.Recipient, "Verify your affiliate account", false)]
    [InlineData("Development", false, AffiliateP53dMailSink.Recipient, "Verify your affiliate account", false)]
    [InlineData("Development", true, "unrelated@example.test", "Verify your affiliate account", false)]
    [InlineData("Development", true, AffiliateP53dMailSink.Recipient, "Reset password", false)]
    [InlineData("Development", true, AffiliateP53dMailSink.Recipient, "Verify your affiliate account", true)]
    [InlineData("Development", true, AffiliateP53dMailSink.ShopperRecipient, "Verify your email address", true)]
    public async Task MailboxRequiresExactDevelopmentOptIn(string environment, bool enabled,
        string recipient, string subject, bool accepted)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp", "opencode", "p53d-mail-test-" + Guid.NewGuid().ToString("N"));
        var env = new TestEnvironment { EnvironmentName = environment, ContentRootPath = Path.Combine(root, "API") };
        var directory = AffiliateP53dMailSink.DirectoryPath(env.ContentRootPath);
        Directory.CreateDirectory(directory);
        try
        {
            if (enabled) await File.WriteAllTextAsync(Path.Combine(directory, "mail.enabled"), "explicit opt-in");
            var smtp = new SmtpEmailSender(Options.Create(new SmtpSettings { Enabled = false }), NullLogger<SmtpEmailSender>.Instance);
            var sink = new AffiliateP53dMailSink(env, smtp);
            if (accepted)
            {
                await sink.SendEmailAsync(recipient, subject, "html", "credential-bearing-test-body");
                Assert.Equal("credential-bearing-test-body", await File.ReadAllTextAsync(Path.Combine(directory,
                    recipient == AffiliateP53dMailSink.Recipient ? "verification-mail.txt" : "shopper-verification-mail.txt")));
            }
            else
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => sink.SendEmailAsync(recipient, subject, "html", "test"));
                Assert.False(File.Exists(Path.Combine(directory, "verification-mail.txt")));
            }
        }
        finally
        {
            // Only this test's newly allocated directory; never a workspace/runtime path.
            Directory.Delete(root, true);
        }
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "PointOfSale.Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
#endif
