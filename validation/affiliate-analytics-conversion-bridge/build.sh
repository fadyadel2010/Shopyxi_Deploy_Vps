set -euo pipefail
export DOTNET_ROOT=/dotnet DOTNET_CLI_HOME=/tmp/analytics-dotnet NUGET_PACKAGES=/packages
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
cd /src
/dotnet/dotnet restore PointOfSale.Tests/PointOfSale.Tests.csproj --source /packages
/dotnet/dotnet build PointOfSale.Tests/PointOfSale.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false
