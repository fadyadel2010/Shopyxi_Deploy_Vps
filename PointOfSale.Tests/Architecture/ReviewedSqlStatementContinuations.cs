using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PointOfSale.Tests.Architecture;

// Exact statement-level continuation of unchanged historical reviews only.
// The context seal alone cannot approve any statement. New Generation SQL is NOT included.
internal static class ReviewedSqlStatementContinuations
{
    internal const string ReviewedContext = "715ac764dc1f86828793376696a73aa325c46a8d375452602b4a1b9ec40b9ae4";
    internal static readonly IReadOnlyDictionary<string, string[]> Entries = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["PointOfSale.API/Controllers/AffiliateAuthController.cs"] =
        [
            "Login|ExecuteUpdateAsync|d76452576f78f28dc9d90b14fd8687a3762520944e08db939104b91f858b7eec",
            "RefreshToken|ExecuteUpdateAsync|999487d9d9a68bda742fef1e2e3ae925f0e5d56c400a690bb3550dbb1431fdde",
            "VerifyEmail|ExecuteUpdateAsync|a47de37ff9c5d0ec82f76c026ccc4dbf2665da6b512591e3618f8724756d5493",
            "VerifyEmail|ExecuteUpdateAsync|039e654eedd2eb405f13121c2d2a2c7eb445d0aadc2e6e956bc04214b716ad2b",
            "ResetPassword|ExecuteUpdateAsync|57dc8bb548b3798ab4cf0e29db3e72c0ef99661cfb96bf48751bc9c4ccce568d",
            "ResetPassword|ExecuteUpdateAsync|46090e7bda49eb497878d5fb6720d363208bb1e3fcf7acf3952fc514d993819f",
            "ResetPassword|ExecuteUpdateAsync|0c4ba7dc576861de6717f6367533ef00ca030dd12599f5c98813021dd92109ac",
            "HoldAffiliateTokenAsync|ExecuteSqlInterpolatedAsync|e6c9382cbbe48a11292f51d38f05d400ccb013fda17aaa115f0dd24d4f92f52c",
            "HoldAffiliateTokenAsync|ExecuteSqlInterpolatedAsync|b9116d30e097defec186af25b8c706304f0122211a2ed638287483be204fc016",
            "ReadAffiliateAuthorityClockAsync|SqlQuery|f724dc265de6543890fd2d3340b74a2d86a4e147c47d5e638c68584fa483ba4a",
            "ReloadLockedAffiliateAccountAsync|ExecuteSqlInterpolatedAsync|d290d94aae774f5611afb049dccf246d0cedc9259c97f6d4927448957d29a559",
            "ReplaceVerificationTokenAsync|ExecuteUpdateAsync|2f82f0d3347763ccba1dd77407c5499e9eb3f83ca026f098ca66bfb68c428e65",
            "ReplaceVerificationTokenAsync|ExecuteDeleteAsync|1825d2552ce5512576689eb0b1d7e15a65ffa506124c721e6935d15a0038e192",
            "IssuePasswordResetEmailAsync|ExecuteUpdateAsync|866157790b5d9d6affbb6488b949a3adc6d30785c4fb836a4abba288c19cc3d9",
            "IssuePasswordResetEmailAsync|ExecuteUpdateAsync|50be24e26a1bd6f2168ff854221b472d57c5674e27a3387ec6bfd089165e20a7",
            "TryRevokeReplayedAffiliateRefreshTokenAsync|ExecuteUpdateAsync|a9bcb8309d221db8e34f346efcfcf761d6216add561b687c7a9a361027a880c4",
        ],
        ["PointOfSale.API/Controllers/CashboxController.cs"] =
        [
            "CloseCashbox|ExecuteSqlInterpolatedAsync|fa2cc77014c52f9290cfeb54976b5f18fa38a8345e83067d2d5c9e9f954d5d8d",
            "CloseCashbox|ExecuteUpdateAsync|62afac89c52a278a2befa9a9e4fb72045db89d283d849499d0ab2c9e6ed25cce",
            "HoldCashboxWarehouseAsync|ExecuteSqlInterpolatedAsync|62c4e289f9482fd6f337c19ce5b08a48ce273c39dfd3d99aa1b86047f1a7183e",
            "HoldCashboxSubjectAsync|ExecuteSqlInterpolatedAsync|e6713c4033bbd39ff7e46d5832032f505c492063cf202aa441d33a2288e85139",
            "HoldCurrentCashboxActorAsync|ExecuteSqlInterpolatedAsync|fa2cc77014c52f9290cfeb54976b5f18fa38a8345e83067d2d5c9e9f954d5d8d",
        ],
        ["PointOfSale.API/Infrastructure/Idempotency/ProductMutationIdempotencyService.cs"] =
        [
            "TryClaimAsync|ExecuteSqlInterpolatedAsync|2a2ad15b4e15339fdef88a0c96c7fbf74a2a6a4ef660a6c7d382773362808638",
            "TryClaimAsync|ExecuteSqlInterpolatedAsync|8088bdb64aa104ba77b380a0a0dab03820dbe2cc003521673e1cdb127ffcd0af",
            "TryClaimAsync|ExecuteSqlInterpolatedAsync|f003d76b6856563f6ac2ba60f80781674404557232a2385a673cc0251d2e2b48",
            "TryRecoverAbandonedClaimAsync|ExecuteUpdateAsync|0f0b64e103a7f0de6a1c0d2b1599a19d6b883e2999d2626dce5ec9f183b2687e",
            "TryRenewClaimLeaseAsync|ExecuteUpdateAsync|cf0b48dd3d82279e729be58485f43bb018329a998f9d382a752e0b0e0ea81c01",
            "TryWriteIncompleteClaimStateAsync|ExecuteUpdateAsync|20c028a3c0c2be098fe5f2d72bd61010f16acfa849314bb3a96d643efb7f3d26",
            "TryCompleteDurablyAsync|ExecuteUpdateAsync|b59884b17b684bee7dcec155c0a3cdc757b51e404fa42fe43de8894a5077cfb8",
        ],
        ["PointOfSale.API/Jobs/CheckoutAttachmentCleanupJob.cs"] =
        [
            "TryClaimForDeletionAsync|ExecuteUpdateAsync|7f692a1f5399ea584a213aaa85f31c84ee7c8453160e3fca479b36491389f87b",
            "MarkExpiredAsync|ExecuteUpdateAsync|725714099397052310d70c7658b1c7875111c5592476af477a79a07fdea8d970",
        ],
        ["PointOfSale.API/Services/Admin/GeographicAdministrationService.CatalogWorkflow.cs"] =
        [
            "BeginTenantGeographicTransactionAsync|ExecuteSqlInterpolatedAsync|471f69294c6d1d5007f47cb8bb4af7b4924cfcf111238b81f5eb662b9049f976",
            "BeginTenantGeographicTransactionAsync|ExecuteSqlInterpolatedAsync|edc85c443257c97933043de6b41697b4f84b84463e9e9940a448a315a7e60dbd",
            "BeginGlobalCatalogTransactionAsync|FromSqlInterpolated|e28fa474b477cfde7dbc65f1ec7090bb885578a8bace1bcdafe9cfeaf50d5c83",
            "BeginGlobalCatalogTransactionAsync|FromSqlInterpolated|bf41ce1b248b3a83f97de05ea6e4bd846bfea70edf61a5a7c847615ea742e0ef",
            "BeginCatalogTransactionAsync|ExecuteSqlInterpolatedAsync|05105026fb2c9fbe6fe89c4e7ee7c2b146e3e639f18a68152536f908eccc64f6",
            "LoadCatalogReleaseAsync|FromSqlInterpolated|f0270473d86278a6345e4c80fc343c5883ad76757b8d2c3f91b1285c7c0e2e28",
        ],
        ["PointOfSale.API/Services/AffiliateQaPayoutSimulation.cs"] =
        [
            "ExecuteAsync|ExecuteSqlInterpolatedAsync|3fbce152d389bfdd93a0506affc604175525dc2e653c9288ac0347d1234269b8",
        ],
        ["PointOfSale.API/Services/Checkout/CheckoutAttachmentService.cs"] =
        [
            "RemovePendingAttachmentAsync|ExecuteUpdateAsync|63ecf672f41b9983ce66b00a0414ba17946cb89dc32e5d859e66fcb65998c3d7",
            "RemovePendingAttachmentAsync|ExecuteUpdateAsync|62460805e06bc8d5a21ccdb68a5eecc1c81b6e4767166742386d285c0982a3ac",
            "ValidateAndAttachAsync|SqlQuery|874be0650a5c0f208a766eaeb7f8bed1696e9c0afb8bb8936f12424720197715",
            "ValidateAndAttachAsync|ExecuteSqlInterpolatedAsync|6736783bcaf88fd2f192c5e32ca35e29d8dd6357931c666578c8824462e5e7c4",
            "ValidateAndAttachAsync|ExecuteUpdateAsync|602ddf79f4d6fef9eec190814b0175a9b4b60a7a3579c7c9c6123fbe03080686",
            "ConfirmAttachmentObjectAsync|ExecuteUpdateAsync|de329846eaae63b8fd3966347ae96d6a9a7dbb0f7544877e602cca2062cb1eb2",
            "PersistAttachmentStateAsync|ExecuteUpdateAsync|b16fe88b3a69740a68b23594eed18868e4e7b56bd4b87ff54d78064289a7a81f",
        ],
        ["PointOfSale.API/Services/CommerceHub/Finance/CommerceHubFinancialSafetyGuardService.cs"] =
        [
            "AcquireLocksAsync|ExecuteSqlInterpolatedAsync|855702903d5e15d4f39111a99185d4b71652834334f4f07f9d53b22943898490",
            "AcquireLocksAsync|ExecuteSqlInterpolatedAsync|554d559ba72533c43923d7c9692e8b3cd830f9b7561da3874e5bf74f1a343bf9",
            "AcquireLocksAsync|FromSqlInterpolated|9a93485b3298652b790e58c31e66b94cd549858e9e45145581ba0e51f336e877",
            "AcquireLocksAsync|FromSqlInterpolated|b204eac1d81f1069857561ddcf394b64055d5bd65a913178607f36d12e3a79f7",
        ],
        ["PointOfSale.API/Services/Users/PosUserManagementService.cs"] =
        [
            "LockTenantUsersAsync|ExecuteSqlAsync|5606a25b58367b45a1e6aeaada5676f0fb4632fc00c243d1ff991720d94c5783",
            "LockTenantUsersAsync|ExecuteSqlAsync|8d7b3d28c21b507a946ae30e39f8343b73d50e6a2d38879b6488a10a16fadc67",
        ],
        ["PointOfSale.Infrastructure/Analytics/Delivery/ProviderDeliveryProcessor.cs"] =
        [
            "ClaimAsync|ExecuteUpdateAsync|d67c2da98b4e2569a0064cdc2c33cdeb62fec0acd995395b8647d80879a48823",
            "SaveOwnedOutcomeAsync|ExecuteUpdateAsync|4987791a6415a67e5b02db6cc3295a3350624ecf51f3ca329f1e7f1588981c51",
            "ReadPostLockClockAsync|ExecuteSqlInterpolatedAsync|1537e543ee7c443431cf9707467f167f7e5b64e6c5025a707a4c7750011835f0",
            "ReadPostLockClockAsync|SqlQuery|a1856c924069231346b41f2fb0ae8373b1b8d4a27f5f28f4e224a74c30dcc1e0",
        ],
        ["PointOfSale.Infrastructure/Payments/PaymentLifecycleService.CarrierRemittance.cs"] =
        [
            "RecordCarrierRemittanceAsync|ExecuteSqlInterpolatedAsync|8e34d9e1f57307d3552641989f1cd9f078c065051944fe3ca678f7b9c4c38387",
            "RecordCarrierRemittanceAsync|ExecuteSqlInterpolatedAsync|e8f1b33d9d07a55aa13eed00b782752ab92061bdfe4db4c310b6c27c4b320a95",
            "RecordCarrierRemittanceAsync|FromSqlInterpolated|52c474c29d272d148d86a10edf0e128088b031998a434d7e81c4b8cc885bdfca",
            "RecordCarrierRemittanceAsync|ExecuteSqlInterpolatedAsync|1034b79a2f813c546121a47f415d755a49b76d7aa15e817152bb915930761cd7",
            "RecordCarrierRemittanceAsync|ExecuteSqlInterpolatedAsync|eacdc99785728fbab86837e4fba80317d49915cf8adbd4b867c816bd50ee9828",
        ],
        ["PointOfSale.Infrastructure/Services/Affiliate/AffiliateLandingPageDesignStore.cs"] =
        [
            "SaveAsync|ExecuteSqlInterpolatedAsync|d44aba2e7ae90d4026d139479c59f6b6a11c24fd58af4318fa54efcc01613f3c",
            "SaveAsync|ExecuteSqlInterpolatedAsync|8de021cf6a3e8c8ca34bf3107ece6968ac45f6747dbfe7ec05b552d9eb4318f0",
            "SaveAsync|ExecuteSqlInterpolatedAsync|817a4324c5fc2b501685ab77b114d652210ce0d514ae1cd63e0ed4b9c834de7d",
        ],
        ["PointOfSale.Infrastructure/Services/Affiliate/AffiliateSettlementOrchestrator.Manual.cs"] =
        [
            "ValidateManualActorAsync|ExecuteSqlInterpolatedAsync|51506f974179b67a310b539f9e280214a386bf8329adaeb8c54b9c1139128bcc",
            "LoadManualFundingAsync|ExecuteSqlInterpolatedAsync|07ba46e4fcd14f48d6c9079aa177a43dee27d5f1480738c63f7b624f183dc4df",
            "LoadManualFundingAsync|ExecuteSqlInterpolatedAsync|1696eb2437702f713f5ab5d3e5aacc783c3fa219d40b207563156d09a7b89f1e",
        ],
        ["PointOfSale.Infrastructure/Services/NativeAbandonedCartRecoveryScheduler.cs"] =
        [
            "ClaimAndDispatchSequenceAsync|ExecuteUpdateAsync|d938bad2eb01090cf57153abf19f180104e88e66441b9a43997f1e1fca3d28e0",
            "ClaimAndDispatchSequenceAsync|ExecuteUpdateAsync|5641018864327283243cf90b68e92f84195fd5d5e2fb73ec280a8ffc794c68d6",
            "DispatchClaimedAsync|FromSqlInterpolated|7778c269006b38669ed9f01a8a648325d7639a34d4cfadccc46c3e9420cc448a",
            "DispatchClaimedAsync|ExecuteSqlInterpolatedAsync|ea1c65cca64f80274b08ccd5197851c665fa4932ff7b23629a2cba41ef44b35c",
            "CompleteSentAsync|ExecuteUpdateAsync|74d39af97bea52013b8644ec1973ab0d62d4fa76bc3ba9920d8c5b84d2ff3924",
            "CompleteSentAsync|ExecuteUpdateAsync|c9bdc69f97dc2608b040077cef95c35a2b63e6632796ceeb497c92780015d0f3",
            "SaveDispatchLogIdAsync|ExecuteUpdateAsync|631d958cf42ac0d25a94ea21451b2faa0750dad38a46abc1b2e9eea6bc1a89e2",
            "SaveDispatchLogIdAsync|ExecuteUpdateAsync|82869c836d11be678b1a6c53ea675e0e1a26b3f2b9a971a80e287b1ca7c894aa",
            "CompleteTerminalAsync|ExecuteUpdateAsync|60368ae5c2e405502c2deac44b2bc97d368240fe93637955ddd5f06554a1a089",
            "CompleteTerminalAsync|ExecuteUpdateAsync|c147f08e8a45312dcc17c04d5cff1bc8bd44d8126a3fbb35dd1cc204eec6b298",
            "RescheduleAsync|ExecuteUpdateAsync|d324b419ff0612d9685e2252595ad1c1ea5ad5d5b2776f82dee98e3ee57adf9f",
            "RescheduleAsync|ExecuteUpdateAsync|44d0a2a8cb814c063c5125df8eda06c349ba19c914c939f9111a7f31a0f3b2d3",
        ],
        ["PointOfSale.Infrastructure/Shipping/ShippingWebhookClaimProtocol.cs"] =
        [
            "LockAsync|FromSqlInterpolated|764567b1ee5e37f4178b59d91d2f3233d14c8c28729d324ff4be2f2bed268972",
            "CreateFirstAsync|ExecuteSqlInterpolatedAsync|62f919d7c94e88bf5a87cc81b35e7d8005e263808b25da2ddeda5f91215a7859",
            "AcquireAsync|ExecuteSqlInterpolatedAsync|6dc0831e52908aebbea9f363c67e2f04e9183e6437e0b03131ac595d745366ce",
            "AcquireAsync|ExecuteUpdateAsync|a5e61bbc743948eb63e1a7959f7fbea365b877c6cc4ca3691184d7c8d0139a26",
            "RenewAsync|ExecuteSqlInterpolatedAsync|67f7350e871b3f5741ad5a9469c65ce58d0f3ea6e8417d859a4cab7dc505f148",
            "FinishAsync|ExecuteSqlInterpolatedAsync|402b6d848c4bed59129bc1e99202ee2dd78145250e1464aa34764e40630689f6",
            "FailAsync|ExecuteUpdateAsync|a198898248156a1d6bf8fd2473ead80c9fa74d847d2bc6710dde5243cbbbc63e",
        ],
        ["PointOfSale.API/Security/PosSessionAuthority.cs"] =
        [
            "HoldTenantGovernanceAsync|ExecuteSqlInterpolatedAsync|e2cf9ca9924cbccabfa8af8769ce11674b94064be98b817fa42ce56c3f8eb862",
            "HoldTenantGovernanceAsync|ExecuteSqlInterpolatedAsync|7250a3d611f89c15ea76cfe6925c2c760511cfbbb6b5e125eabed330519fa948",
            "HoldTenantGovernanceAsync|ExecuteSqlInterpolatedAsync|9bf6fd4a58882b5e91cb883f65569e581fbbcaf3bedc76aaf8c05976aa550b26",
            "ValidateAccessTokenCoreAsync|SqlQuery|f724dc265de6543890fd2d3340b74a2d86a4e147c47d5e638c68584fa483ba4a",
        ],
    };

    internal static bool Exact(string path, string source, string productionContext)
    {
        if (productionContext != ReviewedContext) return false;
        if (!Entries.TryGetValue(path, out var approved)) return false;
        var syntax = CSharpSyntaxTree.ParseText(ReviewedSqlGovernanceBoundaries.Normalize(source), new CSharpParseOptions(preprocessorSymbols: ["DEBUG"])).GetRoot();
        if (syntax.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)) return false;
        var actual = syntax.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(ReviewedSqlGovernanceBoundaries.IsSqlPrimitive)
            .Select(call => (call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "") + "|" +
                ((MemberAccessExpressionSyntax)call.Expression).Name.Identifier.ValueText + "|" +
                ReviewedSqlGovernanceBoundaries.Hash(call.WithoutTrivia().NormalizeWhitespace().ToFullString()));
        return actual.SequenceEqual(approved, StringComparer.Ordinal);
    }
}
