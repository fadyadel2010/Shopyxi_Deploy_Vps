SELECT
    (SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115213516_AffiliateAnalyticsConversionBridge') = 1
    AND (SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116100000_CommerceHubSellerApplications') = 0
    AND to_regclass('public."CommerceHubSellerApplications"') IS NULL
    AND (SELECT COUNT(*) FROM "SystemSchemaVersions" WHERE "Component" = 'postgres' AND NOT "IsDeleted" AND "Version" = '20261115213516_AffiliateAnalyticsConversionBridge') = 1
    AND (SELECT COUNT(*) FROM "SuperAdmins" WHERE "IsSystem" = TRUE) = 1;
