-- P5.4: additive presentation preferences; no existing rows or financial tables are modified.
BEGIN;
CREATE TABLE IF NOT EXISTS "AffiliateLandingPageDesigns" (
    "AffiliateAccountId" uuid NOT NULL REFERENCES "AffiliateAccounts" ("Id") ON DELETE RESTRICT,
    "TenantId" uuid NOT NULL REFERENCES "Tenants" ("Id") ON DELETE RESTRICT,
    "ProductId" uuid NOT NULL REFERENCES "Products" ("Id") ON DELETE RESTRICT,
    "TemplateType" varchar(64) NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_AffiliateLandingPageDesigns" PRIMARY KEY ("AffiliateAccountId", "TenantId", "ProductId"),
    CONSTRAINT "CK_AffiliateLandingPageDesigns_Template" CHECK ("TemplateType" IN ('Classic:v1', 'Storytelling:v1', 'MinimalFastCheckout:v1'))
);
-- Match the EF model's foreign-key lookup indexes as well as the composite PK.
-- IF NOT EXISTS also makes this additive step safe for stores that installed
-- the original presentation table before these index declarations were present.
CREATE INDEX IF NOT EXISTS "IX_AffiliateLandingPageDesigns_ProductId" ON "AffiliateLandingPageDesigns" ("ProductId");
CREATE INDEX IF NOT EXISTS "IX_AffiliateLandingPageDesigns_TenantId" ON "AffiliateLandingPageDesigns" ("TenantId");
COMMIT;
