using System.Text.RegularExpressions;

namespace PointOfSale.API.Services;

/// <summary>Public URLs contain product/media identity only, never storage paths or tenant authority.</summary>
internal static class AffiliatePublicProductMedia
{
    internal static bool IsImageFileName(string fileName) => Regex.IsMatch(
        fileName, @"\A[0-9a-fA-F]{32}\.(jpg|jpeg|png|webp)\z", RegexOptions.IgnoreCase);

    internal static string ResolveUrl(Guid productId, string image)
    {
        var prefix = $"/api/upload/product-media/{productId:D}/";
        if (image.StartsWith(prefix, StringComparison.Ordinal) && IsImageFileName(image[prefix.Length..]))
            return $"/api/affiliate/discover/public-products/{productId:D}/media/{image[prefix.Length..]}";

        // Existing public object-storage/CDN URLs stay on their original media system.
        return Uri.TryCreate(image, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? image : string.Empty;
    }
}
