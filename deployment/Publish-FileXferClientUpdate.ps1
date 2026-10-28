#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [string]$ReleaseDirectory,

    [Parameter(Mandatory)]
    [string]$InstallDirectory,

    [Parameter(Mandatory)]
    [string]$ServiceName,

    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$ExpectedZipSha256,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$ExpectedExeSha256,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$ExpectedSettingsSha256,

    [Parameter(Mandatory)]
    [ValidatePattern('^https://')]
    [string]$HealthUri
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ReleaseDirectory = [IO.Path]::GetFullPath($ReleaseDirectory)
$InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory)
$ExpectedZipSha256 = $ExpectedZipSha256.ToLowerInvariant()
$ExpectedExeSha256 = $ExpectedExeSha256.ToLowerInvariant()
$ExpectedSettingsSha256 = $ExpectedSettingsSha256.ToLowerInvariant()

$zipName = "TransferClienteASE-$Version-win-x64.zip"
$exeName = "TransferClienteASE-$Version-standalone-win-x64.exe"
$sourceZip = Join-Path $ReleaseDirectory "client\$zipName"
$sourceExe = Join-Path $ReleaseDirectory "client\$exeName"
$sourceSettings = Join-Path $ReleaseDirectory 'client-update.settings.json'
$settingsPath = Join-Path $InstallDirectory 'client-update.settings.json'
$updatesDirectory = Join-Path $InstallDirectory 'wwwroot\updates'

function Assert-FileHash {
    param([string]$Path, [string]$ExpectedSha256)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "No existe el archivo requerido: $Path"
    }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $ExpectedSha256) {
        throw "SHA-256 inválido para $Path."
    }
}

Assert-FileHash $sourceZip $ExpectedZipSha256
Assert-FileHash $sourceExe $ExpectedExeSha256
Assert-FileHash $sourceSettings $ExpectedSettingsSha256

if (-not $PSCmdlet.ShouldProcess(
        $InstallDirectory,
        "Publicar cliente $Version, activar manifiesto y reiniciar $ServiceName")) {
    return
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupPath = Join-Path $InstallDirectory "client-update.settings.rollback.$timestamp.json"
$stagingDirectory = Join-Path $InstallDirectory (
    'client-update.staging.' + [Guid]::NewGuid().ToString('N'))
$stageZip = Join-Path $stagingDirectory $zipName
$stageExe = Join-Path $stagingDirectory $exeName
$stageSettings = Join-Path $stagingDirectory 'client-update.settings.json'
$publishedZip = Join-Path $updatesDirectory $zipName
$publishedExe = Join-Path $updatesDirectory $exeName

New-Item -ItemType Directory -Path $stagingDirectory | Out-Null
try {
    Copy-Item -LiteralPath $sourceZip -Destination $stageZip
    Copy-Item -LiteralPath $sourceExe -Destination $stageExe
    Copy-Item -LiteralPath $sourceSettings -Destination $stageSettings

    Assert-FileHash $stageZip $ExpectedZipSha256
    Assert-FileHash $stageExe $ExpectedExeSha256
    Assert-FileHash $stageSettings $ExpectedSettingsSha256

    if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
        Copy-Item -LiteralPath $settingsPath -Destination $backupPath
    }

    New-Item -ItemType Directory -Path $updatesDirectory -Force | Out-Null
    Copy-Item -LiteralPath $stageZip -Destination $publishedZip
    Copy-Item -LiteralPath $stageExe -Destination $publishedExe
    Copy-Item -LiteralPath $stageSettings -Destination $settingsPath -Force

    Restart-Service -Name $ServiceName -Force
    (Get-Service -Name $ServiceName).WaitForStatus(
        'Running',
        [TimeSpan]::FromSeconds(60))
    Start-Sleep -Seconds 5

    $health = Invoke-WebRequest -Uri $HealthUri -UseBasicParsing -TimeoutSec 20
    $baseUri = [Uri]$HealthUri
    $updateUri = [Uri]::new(
        $baseUri,
        "/admin/client-updates/transfer-cliente-ase?currentVersion=$Version")
    $update = Invoke-RestMethod -Uri $updateUri -TimeoutSec 20

    if ($health.StatusCode -ne 200 -or
        $update.latestVersion -ne $Version -or
        ([string]$update.sha256).ToLowerInvariant() -ne $ExpectedZipSha256) {
        throw 'La validación posterior no coincide con la actualización publicada.'
    }

    Assert-FileHash $publishedZip $ExpectedZipSha256
    Assert-FileHash $publishedExe $ExpectedExeSha256

    [pscustomobject]@{
        Status = 'PASS'
        Version = $Version
        Service = [string](Get-Service -Name $ServiceName).Status
        HealthStatus = $health.StatusCode
        DownloadUrl = $update.downloadUrl
        RollbackSettings = $backupPath
        PublishedZip = $publishedZip
        PublishedExe = $publishedExe
    }
}
catch {
    if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
        Copy-Item -LiteralPath $backupPath -Destination $settingsPath -Force
        Restart-Service -Name $ServiceName -Force
    }
    throw
}
finally {
    foreach ($stagedFile in @($stageZip, $stageExe, $stageSettings)) {
        if (Test-Path -LiteralPath $stagedFile -PathType Leaf) {
            Remove-Item -LiteralPath $stagedFile -Force
        }
    }
    if (Test-Path -LiteralPath $stagingDirectory -PathType Container) {
        Remove-Item -LiteralPath $stagingDirectory -Force
    }
}
