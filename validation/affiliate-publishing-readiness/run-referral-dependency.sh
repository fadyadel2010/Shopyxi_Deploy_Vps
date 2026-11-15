set -euo pipefail
export DOTNET_ROOT=/dotnet DOTNET_CLI_HOME=/tmp/readiness-dotnet-home NUGET_PACKAGES=/packages
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
export Testing__TempRoot=/tmp/readiness-tests DataProtection__KeysPath=/tmp/readiness-tests/keys
cd /src
/dotnet/dotnet test PointOfSale.Tests/PointOfSale.Tests.csproj --configuration Release --no-restore -m:1 -p:UseSharedCompilation=false \
  --filter 'FullyQualifiedName~AffiliateReferralJourneyTests&FullyQualifiedName!~OrderSnapshotAndCanonicalAttribution' \
  --logger 'trx;LogFileName=referral-dependency.trx' --results-directory /src/validation/affiliate-publishing-readiness/results
