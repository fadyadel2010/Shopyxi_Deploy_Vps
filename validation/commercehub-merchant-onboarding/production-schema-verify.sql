SELECT
    (SELECT COUNT(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116100000_CommerceHubSellerApplications') = 1
    AND to_regclass('public."CommerceHubSellerApplications"') IS NOT NULL
    AND to_regclass('public."UX_CommerceHubSellerApplications_OnePendingPerTenant"') IS NOT NULL
    AND to_regclass('public."IX_CommerceHubSellerApplications_TenantId_Revision"') IS NOT NULL
    AND (SELECT COUNT(*) FROM "CommerceHubSellerApplications") = 0
    AND (SELECT pg_get_userbyid(c.relowner) FROM pg_class c WHERE c.oid = 'public."CommerceHubSellerApplications"'::regclass) = 'app'
    AND has_table_privilege('shopixy_api_runtime', 'public."CommerceHubSellerApplications"', 'SELECT,INSERT,UPDATE')
    AND NOT has_table_privilege('shopixy_api_runtime', 'public."CommerceHubSellerApplications"', 'DELETE')
    AND (SELECT COUNT(*) FROM "SystemSchemaVersions" WHERE "Component" = 'postgres' AND NOT "IsDeleted" AND "Version" = '20261116100000_CommerceHubSellerApplications') = 1
    AND (SELECT COUNT(*) FROM "SuperAdmins" WHERE "IsSystem" = TRUE) = 1;
