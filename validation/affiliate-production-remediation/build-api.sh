set -euo pipefail
export PATH=/home/fadya/.dotnet10-10.0.404:/opt/shopixy-toolchain/bin:$PATH
export DOTNET_CLI_HOME=/home/fadya/shopixy-subdomains-audit-r2/dotnet-home
export NUGET_PACKAGES=/home/fadya/.nuget/packages DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
base=/mnt/c/Users/fadya/AppData/Local/Temp/opencode/shopixy-affiliate-remediation-20261115
cd "$base/source"
dotnet restore PointOfSale.API/PointOfSale.API.csproj --source /home/fadya/.nuget/packages > "$base/api-restore.log" 2>&1
dotnet build PointOfSale.API/PointOfSale.API.csproj --no-restore --no-incremental -m:1 -p:UseSharedCompilation=false > "$base/api-build.log" 2>&1
printf '%s\n' 'Isolated API build passed.'
