START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115112011_AffiliateManualPayoutReturn') THEN
    CREATE TABLE "AffiliateManualPayoutReturns" (
        "Id" uuid NOT NULL,
        "StoreTenantId" uuid NOT NULL,
        "AffiliateAccountId" uuid NOT NULL,
        "RequestId" uuid NOT NULL,
        "ManualPayoutId" uuid NOT NULL,
        "OriginalJournalId" uuid NOT NULL,
        "BalanceLedgerId" uuid NOT NULL,
        "AdministratorId" uuid NOT NULL,
        "Amount" numeric(18,4) NOT NULL,
        "Currency" character varying(3) NOT NULL,
        "RecordedAtUtc" timestamp with time zone NOT NULL,
        "Attestation" character varying(200) NOT NULL,
        "Reason" character varying(1000) NOT NULL,
        "ReceiptReference" character varying(200) NOT NULL,
        "CommandFingerprint" character varying(64) NOT NULL,
        CONSTRAINT "PK_AffiliateManualPayoutReturns" PRIMARY KEY ("Id"),
        CONSTRAINT "CK_AffiliatePayoutReturn_Amount" CHECK ("Amount" > 0),
        CONSTRAINT "FK_AffiliateManualPayoutReturns_AffiliateAccounts_AffiliateAcc~" FOREIGN KEY ("AffiliateAccountId") REFERENCES "AffiliateAccounts" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_AffiliateManualPayoutReturns_AffiliateBalanceLedger_Balance~" FOREIGN KEY ("BalanceLedgerId") REFERENCES "AffiliateBalanceLedger" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_AffiliateManualPayoutReturns_AffiliateManualPayouts_ManualP~" FOREIGN KEY ("ManualPayoutId") REFERENCES "AffiliateManualPayouts" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_AffiliateManualPayoutReturns_AffiliatePayoutRequests_Reques~" FOREIGN KEY ("RequestId") REFERENCES "AffiliatePayoutRequests" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_AffiliateManualPayoutReturns_LedgerJournalEntries_OriginalJ~" FOREIGN KEY ("OriginalJournalId") REFERENCES "LedgerJournalEntries" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_AffiliateManualPayoutReturns_Users_AdministratorId" FOREIGN KEY ("AdministratorId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115112011_AffiliateManualPayoutReturn') THEN
    CREATE TABLE "AffiliateManualPayoutReturnAllocations" (
        "Id" uuid NOT NULL,
        "ReturnId" uuid NOT NULL,
        "PaidEntryId" uuid NOT NULL,
        "Amount" numeric(18,4) NOT NULL,
        CONSTRAINT "PK_AffiliateManualPayoutReturnAllocations" PRIMARY KEY ("Id"),
        CONSTRAINT "CK_AffiliatePayoutReturnAllocation_Amount" CHECK ("Amount" > 0),
        CONSTRAINT "FK_AffiliateManualPayoutReturnAllocations_AffiliateCommissionL~" FOREIGN KEY ("PaidEntryId") REFERENCES "AffiliateCommissionLedger" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_AffiliateManualPayoutReturnAllocations_AffiliateManualPayou~" FOREIGN KEY ("ReturnId") REFERENCES "AffiliateManualPayoutReturns" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115112011_AffiliateManualPayoutReturn') THEN
    CREATE UNIQUE INDEX "IX_AffiliateManualPayoutReturnAllocations_PaidEntryId" ON "AffiliateManualPayoutReturnAllocations" ("PaidEntryId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115112011_AffiliateManualPayoutReturn') THEN
    CREATE INDEX "IX_AffiliateManualPayoutReturnAllocations_ReturnId" ON "AffiliateManualPayoutReturnAllocations" ("ReturnId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115112011_AffiliateManualPayoutReturn') THEN
    CREATE INDEX "IX_AffiliateManualPayoutReturns_AdministratorId" ON "AffiliateManualPayoutReturns" ("AdministratorId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115112011_AffiliateManualPayoutReturn') THEN
    CREATE INDEX "IX_AffiliateManualPayoutReturns_AffiliateAccountId" ON "AffiliateManualPayoutReturns" ("AffiliateAccountId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115112011_AffiliateManualPayoutReturn') THEN
    CREATE UNIQUE INDEX "IX_AffiliateManualPayoutReturns_BalanceLedgerId" ON "AffiliateManualPayoutReturns" ("BalanceLedgerId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115112011_AffiliateManualPayoutReturn') THEN
    CREATE UNIQUE INDEX "IX_AffiliateManualPayoutReturns_ManualPayoutId" ON "AffiliateManualPayoutReturns" ("ManualPayoutId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115112011_AffiliateManualPayoutReturn') THEN
    CREATE UNIQUE INDEX "IX_AffiliateManualPayoutReturns_OriginalJournalId" ON "AffiliateManualPayoutReturns" ("OriginalJournalId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115112011_AffiliateManualPayoutReturn') THEN
    CREATE UNIQUE INDEX "IX_AffiliateManualPayoutReturns_RequestId" ON "AffiliateManualPayoutReturns" ("RequestId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115112011_AffiliateManualPayoutReturn') THEN
    CREATE INDEX "IX_AffiliateManualPayoutReturns_StoreTenantId_AffiliateAccount~" ON "AffiliateManualPayoutReturns" ("StoreTenantId", "AffiliateAccountId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115112011_AffiliateManualPayoutReturn') THEN
    CREATE FUNCTION affiliate_manual_return_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
    BEGIN RAISE EXCEPTION 'Affiliate manual payout return history is append-only'; END $$;
    CREATE TRIGGER affiliate_manual_return_immutable
      BEFORE UPDATE OR DELETE ON "AffiliateManualPayoutReturns"
      FOR EACH ROW EXECUTE FUNCTION affiliate_manual_return_immutable();
    CREATE TRIGGER affiliate_manual_return_no_truncate
      BEFORE TRUNCATE ON "AffiliateManualPayoutReturns"
      FOR EACH STATEMENT EXECUTE FUNCTION affiliate_manual_return_immutable();
    CREATE TRIGGER affiliate_manual_return_allocation_immutable
      BEFORE UPDATE OR DELETE ON "AffiliateManualPayoutReturnAllocations"
      FOR EACH ROW EXECUTE FUNCTION affiliate_manual_return_immutable();
    CREATE TRIGGER affiliate_manual_return_allocation_no_truncate
      BEFORE TRUNCATE ON "AffiliateManualPayoutReturnAllocations"
      FOR EACH STATEMENT EXECUTE FUNCTION affiliate_manual_return_immutable();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115112011_AffiliateManualPayoutReturn') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261115112011_AffiliateManualPayoutReturn', '10.0.15');
    END IF;
END $EF$;
COMMIT;
