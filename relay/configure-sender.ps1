param([Parameter(Mandatory = $true)][Uri]$Origin, [switch]$RotateKey, [string]$ExportPath)
$ErrorActionPreference = 'Stop'
if ($Origin.Scheme -ne 'https' -or $Origin.AbsolutePath -ne '/' -or $Origin.Query -or $Origin.Fragment -or $Origin.UserInfo) { throw 'Use the HTTPS root address of your own relay.' }
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
$relayOrigin = $Origin.GetLeftPart([UriPartial]::Authority)
$relayProfilePath = Join-Path $env:LOCALAPPDATA 'VoiceBridge/relay-profile.json'
$relayEntropy = [Text.Encoding]::UTF8.GetBytes('VoiceBridge.Relay.v1|' + $relayOrigin)
if ((Test-Path -LiteralPath $relayProfilePath) -and !$RotateKey) {
    $relayPrevious = Get-Content -LiteralPath $relayProfilePath -Raw | ConvertFrom-Json
    if ($relayPrevious.Version -ne 1) { throw 'Existing profile version is unsupported; preserving it.' }
    $relayOldOrigin = ([Uri]$relayPrevious.Origin).GetLeftPart([UriPartial]::Authority)
    $relayOldEntropy = [Text.Encoding]::UTF8.GetBytes('VoiceBridge.Relay.v1|' + $relayOldOrigin)
    $relayKeyBytes = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($relayPrevious.ProtectedPublishKey), $relayOldEntropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
} else {
    $relayRandom = [byte[]]::new(32)
    [Security.Cryptography.RandomNumberGenerator]::Fill($relayRandom)
    $relayKeyHex = [Convert]::ToHexString($relayRandom).ToLowerInvariant()
    $relayKeyBytes = [Text.Encoding]::ASCII.GetBytes($relayKeyHex)
}
try {
    $relayProtected = [Security.Cryptography.ProtectedData]::Protect($relayKeyBytes, $relayEntropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    New-Item -ItemType Directory -Path (Split-Path $relayProfilePath -Parent) -Force | Out-Null
    if (Test-Path -LiteralPath $relayProfilePath) { Copy-Item -LiteralPath $relayProfilePath -Destination ($relayProfilePath + '.previous') -Force }
    $relayProfile = @{Version=1; Origin=$relayOrigin; ProtectedPublishKey=[Convert]::ToBase64String($relayProtected)}
    [IO.File]::WriteAllText($relayProfilePath, ($relayProfile | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    if ($ExportPath) {
        $relayExportPath = [IO.Path]::GetFullPath($ExportPath)
        if ($relayExportPath -ne [IO.Path]::GetFullPath($relayProfilePath)) {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($relayExportPath)) | Out-Null
            if (Test-Path -LiteralPath $relayExportPath) { Copy-Item -LiteralPath $relayExportPath -Destination ($relayExportPath + '.previous') -Force }
            [IO.File]::WriteAllText($relayExportPath, ($relayProfile | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        }
    }
    [pscustomobject]@{Origin=$relayOrigin; PublishKeyHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($relayKeyBytes)).ToLowerInvariant(); ProfilePath=$relayProfilePath}
} finally { [Security.Cryptography.CryptographicOperations]::ZeroMemory($relayKeyBytes) }
