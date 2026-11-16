using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace PointOfSale.Infrastructure.MongoDB.Documents.Affiliate;

/// <summary>Analytics-only committed-order projection. One immutable document per SQL order.
/// No payment, fulfillment or financial entitlement is implied by this document.</summary>
public sealed class AffiliateOrderConversionDocument
{
    [BsonId, BsonRepresentation(BsonType.String)]
    public Guid OrderId { get; set; }
    [BsonElement("tenantId"), BsonRepresentation(BsonType.String)]
    public Guid TenantId { get; set; }
    [BsonElement("affiliateAccountId"), BsonRepresentation(BsonType.String)]
    public Guid AffiliateAccountId { get; set; }
    [BsonElement("attributionId"), BsonRepresentation(BsonType.String)]
    public Guid AttributionId { get; set; }
    [BsonElement("clickId"), BsonRepresentation(BsonType.String)]
    public Guid? ClickId { get; set; }
    [BsonElement("productId"), BsonRepresentation(BsonType.String)]
    public Guid? ProductId { get; set; }
    [BsonElement("convertedAtUtc"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ConvertedAtUtc { get; set; }
}
