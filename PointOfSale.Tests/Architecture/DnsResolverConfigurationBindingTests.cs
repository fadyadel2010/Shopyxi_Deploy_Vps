using Microsoft.Extensions.Configuration;
using PointOfSale.Infrastructure.Configuration;

namespace PointOfSale.Tests.Architecture;

public sealed class DnsResolverConfigurationBindingTests
{
    [Fact]
    public void ExplicitNameserversReplacePublicDefaultsAndRepeatedBindingDoesNotDuplicate()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["DnsVerification:NameServers:0"] = "10.43.0.10",
            ["DnsVerification:Timeout"] = "3",
            ["DnsVerification:RetryAttempts"] = "1"
        }).Build();
        var options = new DnsResolverOptions();
        options.BindConfiguration(configuration.GetSection(DnsResolverOptions.SectionName));
        options.BindConfiguration(configuration.GetSection(DnsResolverOptions.SectionName));
        Assert.Equal(new[] { "10.43.0.10" }, options.NameServers);
        Assert.Equal(3, options.Timeout);
        Assert.Equal(1, options.RetryAttempts);
        options.Validate();
    }

    [Fact]
    public void UnspecifiedNameserversRetainCompatibleDefaults()
    {
        var options = new DnsResolverOptions();
        var expected = options.NameServers.ToArray();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["DnsVerification:Timeout"] = "5"
        }).Build();
        options.BindConfiguration(configuration.GetSection(DnsResolverOptions.SectionName));
        Assert.Equal(expected, options.NameServers);
        Assert.Equal(5, options.Timeout);
        options.Validate();
    }
}
