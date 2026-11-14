using System.Xml.Linq;

namespace PointOfSale.Tests.Security;

public sealed class ReleaseArtifactBoundaryTests
{
    [Theory]
    [InlineData("appsettings.*.local.json")]
    [InlineData("appsettings.*.local.*")]
    [InlineData("wwwroot/upload/certification/**")]
    public void PrivateLocalOverridesAndHistoricalCertificationAreNeverPublished(string pattern)
    {
        var root = FindRoot();
        var project = XDocument.Load(Path.Combine(root, "PointOfSale.API", "PointOfSale.API.csproj"));
        Assert.Contains(project.Descendants("Content"), item =>
            ((string?)item.Attribute("Update"))?.Split(';').Contains(pattern) == true &&
            (string?)item.Attribute("CopyToPublishDirectory") == "Never");
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "PointOfSale.API", "PointOfSale.API.csproj")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
