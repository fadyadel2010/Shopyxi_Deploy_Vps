using PointOfSale.Core.Entities;

namespace PointOfSale.API.Services.Checkout;

/// <summary>Persisted identity binding shared by attachment commands and cleanup.</summary>
internal static class CheckoutAttachmentAuthority
{
    internal static IQueryable<CheckoutAttachment> BindIdentity(
        IQueryable<CheckoutAttachment> query, CheckoutAttachment admitted) =>
        query.Where(item => item.Id == admitted.Id &&
                            item.TenantId == admitted.TenantId &&
                            item.CartId == admitted.CartId &&
                            item.CheckoutFieldDefinitionId == admitted.CheckoutFieldDefinitionId &&
                            item.OwnerId == admitted.OwnerId && item.OwnerType == admitted.OwnerType &&
                            item.SessionId == admitted.SessionId && item.OnlineCustomerId == admitted.OnlineCustomerId &&
                            item.ObjectKey == admitted.ObjectKey);

    internal static void RequireOwnedObjectKeys(CheckoutAttachment attachment)
    {
        var scope = $"{attachment.CartId:N}/fields/{attachment.CheckoutFieldDefinitionId:N}/attachments/";
        if (attachment.TenantId == Guid.Empty || attachment.CartId == Guid.Empty ||
            attachment.CheckoutFieldDefinitionId == Guid.Empty ||
            !OwnedKey(attachment.ObjectKey, $"tenants/{attachment.TenantId:N}/checkout/{scope}") ||
            (attachment.FinalObjectKey != null &&
             !OwnedKey(attachment.FinalObjectKey, $"tenants/{attachment.TenantId:N}/checkout-final/{scope}")))
        {
            throw new InvalidOperationException("Checkout attachment object keys do not match the persisted ownership scope.");
        }
    }

    private static bool OwnedKey(string key, string prefix) =>
        key.StartsWith(prefix, StringComparison.Ordinal) && key.Length > prefix.Length &&
        !key.Contains('\\') && !key.Contains('%') && !key.Contains('?') && !key.Contains('#') &&
        !key.Split('/').Any(segment => segment is "" or "." or "..");
}
