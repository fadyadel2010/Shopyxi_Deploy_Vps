START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261111160000_AutomatedTenantProvisioning') THEN
    DO $$
    BEGIN
        IF EXISTS (
            SELECT 1
            FROM "Tenants"
            WHERE "Slug" IS NOT NULL
            GROUP BY LOWER("Slug")
            HAVING COUNT(*) > 1
        ) THEN
            RAISE EXCEPTION USING
                ERRCODE = '23505',
                MESSAGE = 'TENANT_SLUG_CASE_INSENSITIVE_DUPLICATES',
                HINT = 'Resolve duplicate Tenant.Slug values that differ only by case before retrying this migration.';
        END IF;
    END
    $$;

    CREATE UNIQUE INDEX "UX_Tenants_Slug_CaseInsensitive"
    ON "Tenants" (LOWER("Slug"))
    WHERE "Slug" IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261111160000_AutomatedTenantProvisioning') THEN
    CREATE TABLE "TenantProvisioningOperations" (
        "Id" uuid NOT NULL,
        "TenantId" uuid NOT NULL,
        "OwnerUserId" uuid NOT NULL,
        "StoreConfigId" uuid NOT NULL,
        "IdempotencyKeyHash" character varying(64) NOT NULL,
        "RequestFingerprint" character varying(64) NOT NULL,
        "CanonicalHostname" character varying(75) NOT NULL,
        "State" integer NOT NULL,
        "AttemptCount" integer NOT NULL,
        "NextAttemptAtUtc" timestamp with time zone,
        "LeaseId" uuid,
        "LeaseExpiresAtUtc" timestamp with time zone,
        "LastErrorCategory" character varying(64),
        "LastErrorCode" character varying(128),
        "AccountsSeededAtUtc" timestamp with time zone,
        "StorefrontProjectionQueuedAtUtc" timestamp with time zone,
        "VerificationDispatchedAtUtc" timestamp with time zone,
        "CreatedAtUtc" timestamp with time zone NOT NULL,
        "UpdatedAtUtc" timestamp with time zone NOT NULL,
        "CompletedAtUtc" timestamp with time zone,
        CONSTRAINT "PK_TenantProvisioningOperations" PRIMARY KEY ("Id"),
        CONSTRAINT "CK_TenantProvisioningOperations_AttemptCount" CHECK ("AttemptCount" >= 0),
        CONSTRAINT "CK_TenantProvisioningOperations_Hostname" CHECK ("CanonicalHostname" = LOWER("CanonicalHostname") AND "CanonicalHostname" ~ '^[a-z0-9]([a-z0-9-]*[a-z0-9])?\.shopyxi\.com$'),
        CONSTRAINT "FK_TenantProvisioningOperations_StoreConfigs_StoreConfigId" FOREIGN KEY ("StoreConfigId") REFERENCES "StoreConfigs" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_TenantProvisioningOperations_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_TenantProvisioningOperations_Users_OwnerUserId" FOREIGN KEY ("OwnerUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261111160000_AutomatedTenantProvisioning') THEN
    CREATE INDEX "IX_TenantProvisioningOperations_LeaseExpiry" ON "TenantProvisioningOperations" ("LeaseExpiresAtUtc");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261111160000_AutomatedTenantProvisioning') THEN
    CREATE INDEX "IX_TenantProvisioningOperations_OwnerUserId" ON "TenantProvisioningOperations" ("OwnerUserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261111160000_AutomatedTenantProvisioning') THEN
    CREATE INDEX "IX_TenantProvisioningOperations_Recovery" ON "TenantProvisioningOperations" ("State", "NextAttemptAtUtc");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261111160000_AutomatedTenantProvisioning') THEN
    CREATE INDEX "IX_TenantProvisioningOperations_StoreConfigId" ON "TenantProvisioningOperations" ("StoreConfigId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261111160000_AutomatedTenantProvisioning') THEN
    CREATE UNIQUE INDEX "UQ_TenantProvisioningOperations_IdempotencyKeyHash" ON "TenantProvisioningOperations" ("IdempotencyKeyHash");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261111160000_AutomatedTenantProvisioning') THEN
    CREATE UNIQUE INDEX "UQ_TenantProvisioningOperations_TenantId" ON "TenantProvisioningOperations" ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261111160000_AutomatedTenantProvisioning') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261111160000_AutomatedTenantProvisioning', '10.0.15');
    END IF;
END $EF$;
COMMIT;

