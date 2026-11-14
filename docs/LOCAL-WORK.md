# Preserved Windows working-copy differences

The committed source uses certified accepted-release bytes. **No original D: application file was edited, restored, reset or removed.** The original directory is intentionally not a clean checkout. Do not use `git add .`, reset/restore or cleanup to erase this distinction.

The user explicitly authorized read-only access to manifest-listed inputs in the two named certified C: forks. All 15 alternate files matched their recorded SHA-256. Eight existing files differ and seven files were already absent locally. Their release versions exist in Git even when missing from the original working directory.

## Four specifically protected API differences

These were documented as existing unrelated working-copy differences excluded from the latest API release. Their original working hashes were independently rechecked and preserved. Intent is not inferred from folder age or VPS absence. They are not asserted to be accepted Production changes.

- `PointOfSale.API/Controllers/PlatformController.cs`
  - retained working SHA-256: `7a3ee72a3e33307d7b25650518380809d2fd1ea564a29242ac955ea6aa15b3ab`
  - accepted release SHA-256: `825affc110f80cc7fb778bdc07fa46b3f7c00e1d239229e8d0f6ae4e5c485d75`
- `PointOfSale.API/DTOs/ExpenseDTOs.cs`
  - retained working SHA-256: `882736672abe4e4e9cbc5fbbd1d2bed183b3fb7a21ba5203c118dc51b3425727`
  - accepted release SHA-256: `dcd1b6da7c193f21ed01bfc1a2423c416100accabe4461689a21079520528685`
- `PointOfSale.API/Program.cs`
  - retained working SHA-256: `3fe7c78929f0dabdd087f3623885282ba5dd0a9c0f78f06f9a639da934814a57`
  - accepted release SHA-256: `bc082d72c065f3e674e0be90beb11b10bf98986653428d92b1e5b9503cef79b7`
- `PointOfSale.API/Services/Platform/RateLimitService.cs`
  - retained working SHA-256: `6928e4ac108db5cbbe5e0cc87ebc1786b303334f7850b8956ded455590b00302`
  - accepted release SHA-256: `86040277c30e4710c1343e5cdfdb8857d37ebf6a4cf2424f1672753b5b61d73c`

## Complete difference inventory

| File | Original working state |
|---|---|
| `PointOfSale.API/Controllers/PlatformController.cs` | pre-existing different bytes retained |
| `PointOfSale.API/DTOs/ExpenseDTOs.cs` | pre-existing different bytes retained |
| `PointOfSale.API/Middleware/OwnerEmailVerificationMiddleware.cs` | pre-existing absence retained |
| `PointOfSale.API/Program.cs` | pre-existing different bytes retained |
| `PointOfSale.API/Services/Platform/RateLimitService.cs` | pre-existing different bytes retained |
| `validation/storefront-20261114/StorefrontRegression.csproj` | pre-existing absence retained |
| `PointOfSale.Web/src/components/EmailVerificationRequired.d.ts` | pre-existing absence retained |
| `PointOfSale.Web/src/components/EmailVerificationRequired.jsx` | pre-existing absence retained |
| `PointOfSale.Web/src/components/EmailVerificationRequired.test.tsx` | pre-existing absence retained |
| `PointOfSale.Web/src/components/ProtectedRoute.jsx` | pre-existing different bytes retained |
| `PointOfSale.Web/src/components/ProtectedRoute.verification.test.tsx` | pre-existing absence retained |
| `PointOfSale.Web/src/marketplace/queryKeys.ts` | pre-existing different bytes retained |
| `PointOfSale.Web/src/pages/Categories.jsx` | pre-existing different bytes retained |
| `PointOfSale.Web/src/pages/Register.admission.test.tsx` | pre-existing absence retained |
| `PointOfSale.Web/src/pages/Register.jsx` | pre-existing different bytes retained |

`local-work/source-versions.json` records exact accepted/working hashes. `local-work/preserved-working-differences.patch` separately preserves the eight existing versions as a textual recovery reference; it is **not part of deployed runtime source** and is not to be automatically applied. The full original files remain in place. Seven absent files are not silently recreated in that working copy.

The two original final reports are also retained locally, while the Git index stores clearly labeled sanitized recovery editions at the same repository paths. Their resulting local modifications are deliberate evidence preservation, not an instruction to overwrite the originals.

Main workspace: `D:\Shopixy-Automatic-Subdomains-Audit-R2-20261111\source`. `D:\Shopixy-R11-LoadTest-20261111` is separately protected. The explicitly protected `D:\PointOfSale` path was absent at inventory time; it was not deleted, recreated or treated as a duplicate. Active dependency junctions and their targets were untouched.

Source and data on C: were not cleaned or changed. All cleanup remained narrowly scoped to verified D: Shopixy generated duplicates.
