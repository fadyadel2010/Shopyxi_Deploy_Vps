$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$jsonFiles = @(
    '06-storefront-deployment-patch.json',
    '07-api-edge-deployment-patch.json',
    '08-monitoring-deployment-patch.json',
    '08-rollback-monitoring-deployment-patch.json',
    '09-rollback-storefront-deployment-patch.json',
    '10-rollback-api-edge-deployment-patch.json',
    '11-retire-ahram-private-route-patch.json',
    '11-rollback-ahram-private-route-patch.json',
    '14-api-r3-deployment-patch.json',
    '15-rollback-api-r3-to-r2-patch.json',
    '16-assurance-no-store-patch.json',
    '17-rollback-assurance-no-store-patch.json',
    '18-close-registration-deployment-patch.json',
    '19-rollback-close-registration-deployment-patch.json'
)
foreach ($file in $jsonFiles) {
    Get-Content (Join-Path $root $file) -Raw | ConvertFrom-Json | Out-Null
}

$routes = Get-Content (Join-Path $root '05-traefik-wildcard-routes.yaml') -Raw
if ($routes -notmatch 'HostRegexp' -or
    $routes -notmatch 'priority: 900' -or
    $routes -notmatch 'priority: 200' -or
    $routes -notmatch 'priority: 1') {
    throw 'Wildcard routes must retain one-label admission and private-path precedence.'
}

$imageReference = (Get-Content (Join-Path $root 'successor-image-reference.txt') -Raw).Trim()
$apiPatch = Get-Content (Join-Path $root '14-api-r3-deployment-patch.json') -Raw
if ($imageReference -notmatch '^shopixy/api@sha256:[0-9a-f]{64}$' -or
    $apiPatch -notmatch [regex]::Escape($imageReference)) {
    throw 'The guarded API deployment patch does not reference the verified successor digest.'
}

$consumerPath = Join-Path (Split-Path -Parent (Split-Path -Parent $root)) 'PointOfSale.API\Consumers\TenantProvisioningConsumer.cs'
$consumer = Get-Content $consumerPath -Raw
if ($consumer -match 'Cloudflare|HttpClient|CertificateProvider|DnsClient') {
    throw 'Per-tenant provisioning must not depend on DNS, Cloudflare, certificates, or HttpClient.'
}

$migrationPath = Join-Path $root '20261111160000_AutomatedTenantProvisioning.sql'
$migrationExpected = ((Get-Content "$migrationPath.sha256" -Raw).Trim() -split '\s+')[0]
$migrationActual = (Get-FileHash $migrationPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($migrationActual -ne $migrationExpected) {
    throw 'The sealed production migration SQL checksum does not match.'
}
if ($routes -match 'Cloudflare' -or $routes -match 'certificates?\s*per.tenant') {
    throw 'Per-tenant edge provisioning is forbidden.'
}

$certificate = Get-Content (Join-Path $root '02-wildcard-certificate.yaml') -Raw
if ($certificate -notmatch '"\*\.shopyxi\.com"' -or $certificate -notmatch 'cloudflare-dns01-production') {
    throw 'Wildcard DNS-01 certificate contract is incomplete.'
}
$stagingCertificate = Get-Content (Join-Path $root '02a-wildcard-certificate-staging.yaml') -Raw
if ($stagingCertificate -notmatch 'shopixy-wildcard-origin-staging-r1-tls' -or
    $stagingCertificate -notmatch 'lets-encrypt-cloudflare-dns01-staging') {
    throw 'ACME staging acceptance certificate contract is incomplete.'
}

$storefront = Get-Content (Join-Path $root '03-storefront-nginx-configmap.yaml') -Raw
$api = Get-Content (Join-Path $root '04-api-edge-nginx-configmap.yaml') -Raw
foreach ($config in @($storefront, $api)) {
    if ($config -notmatch 'max-age=0' -or $config -notmatch '\.shopyxi\\\.com') {
        throw 'NGINX wildcard/no-store admission contract is incomplete.'
    }
}

$slugAuthorityPath = Join-Path (Split-Path -Parent (Split-Path -Parent $root)) 'PointOfSale.Core\Tenancy\StoreSlug.cs'
$slugAuthority = Get-Content $slugAuthorityPath -Raw
$registry = [regex]::Match(
    $slugAuthority,
    'ReservedValues\s*=.*?\{(?<values>.*?)\};',
    [Text.RegularExpressions.RegexOptions]::Singleline)
if (-not $registry.Success) {
    throw 'Unable to read the canonical StoreSlug reserved-label registry.'
}
$reservedLabels = [regex]::Matches($registry.Groups['values'].Value, '"(?<label>[a-z0-9-]+)"') |
    ForEach-Object { $_.Groups['label'].Value } |
    Sort-Object -Unique
foreach ($label in $reservedLabels) {
    $expected = if ($label -in @('app82-assurance-a', 'app82-assurance-b')) { 0 } else { 1 }
    if ($storefront -notmatch [regex]::Escape("$label.shopyxi.com $expected;")) {
        throw "Storefront edge reservation is missing canonical label '$label'."
    }
    if ($routes -notmatch '!HostRegexp' -or $routes -notmatch [regex]::Escape($label)) {
        throw "Traefik wildcard admission fails to exclude canonical reserved label '$label'."
    }
}

Write-Output 'automatic-subdomain static asset validation: PASS'
