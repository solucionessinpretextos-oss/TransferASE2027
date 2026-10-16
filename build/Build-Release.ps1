[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [ValidatePattern('^https://')]
    [string]$DownloadUrl,

    [Parameter(Mandatory)]
    [string]$SigningKeyPath,

    [Parameter(Mandatory)]
    [string]$TrustedPublicKeyPath,

    [string]$MinSupportedVersion = $Version,
    [string]$ReleaseNotes = 'Actualización de seguridad y estabilidad.',
    [bool]$IsCritical = $false,
    [string]$SigningKeyPasswordEnvironmentVariable = 'TRANSFER_RELEASE_SIGNING_KEY_PASSWORD',
    [string]$CodeSigningCertificateThumbprint,
    [string]$TimestampServer,
    [string]$OutputRoot,
    [switch]$AllowUnsignedBinaries,
    [switch]$AllowDirtyWorkingTree
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'artifacts\releases'
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$releaseDirectory = [IO.Path]::GetFullPath((Join-Path $OutputRoot $Version))
$workRoot = [IO.Path]::GetFullPath((Join-Path $OutputRoot ('.work-' + [Guid]::NewGuid().ToString('N'))))

function Invoke-Checked {
    param([string]$FileName, [string[]]$Arguments, [string]$WorkingDirectory = $repoRoot)

    Push-Location $WorkingDirectory
    try {
        & $FileName @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Falló '$FileName' con código $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

function Assert-NoVulnerableNuGetPackages {
    param([string]$ProjectPath)

    Push-Location $repoRoot
    try {
        $json = (& dotnet list $ProjectPath package --vulnerable --include-transitive --format json 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 0) {
            throw "No fue posible auditar dependencias NuGet de $ProjectPath."
        }

        try {
            $report = $json | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            throw "La auditoría NuGet de $ProjectPath no devolvió JSON válido."
        }

        $vulnerable = [Collections.Generic.List[string]]::new()
        foreach ($project in @($report.projects)) {
            $frameworksProperty = $project.PSObject.Properties['frameworks']
            if ($null -eq $frameworksProperty) { continue }

            foreach ($framework in @($frameworksProperty.Value)) {
                foreach ($propertyName in @('topLevelPackages', 'transitivePackages')) {
                    $packagesProperty = $framework.PSObject.Properties[$propertyName]
                    if ($null -eq $packagesProperty) { continue }

                    foreach ($package in @($packagesProperty.Value)) {
                        $advisories = $package.PSObject.Properties['vulnerabilities']
                        if ($null -ne $advisories -and @($advisories.Value).Count -gt 0) {
                            $vulnerable.Add("$($package.id) $($package.resolvedVersion)")
                        }
                    }
                }
            }
        }

        if ($vulnerable.Count -gt 0) {
            throw "La liberación contiene paquetes NuGet vulnerables: $($vulnerable -join ', ')."
        }
    }
    finally {
        Pop-Location
    }
}

function Write-Utf8NoBom {
    param([string]$Path, [string]$Content)
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Get-Sha256Hex {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Import-ReleasePrivateKey {
    param([string]$Path)

    $pem = [IO.File]::ReadAllText($Path)
    $rsa = [Security.Cryptography.RSA]::Create()
    if ($pem.Contains('BEGIN ENCRYPTED PRIVATE KEY', [StringComparison]::Ordinal)) {
        $password = [Environment]::GetEnvironmentVariable($SigningKeyPasswordEnvironmentVariable)
        if ([string]::IsNullOrEmpty($password)) {
            $rsa.Dispose()
            throw "La llave privada está cifrada y falta la variable $SigningKeyPasswordEnvironmentVariable."
        }
        $rsa.ImportFromEncryptedPem($pem, $password)
    }
    else {
        $rsa.ImportFromPem($pem)
    }
    return $rsa
}

function Get-CodeSigningCertificate {
    param([string]$Thumbprint)

    $normalized = $Thumbprint.Replace(' ', '').ToUpperInvariant()
    foreach ($storePath in @('Cert:\CurrentUser\My', 'Cert:\LocalMachine\My')) {
        $certificate = Get-ChildItem -Path $storePath -CodeSigningCert -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $normalized } |
            Select-Object -First 1
        if ($null -ne $certificate) { return $certificate }
    }
    throw 'No se encontró el certificado de firma de código solicitado o no tiene llave privada.'
}

$parsedVersion = [Version]::new()
if (-not [Version]::TryParse($Version, [ref]$parsedVersion)) {
    throw 'Version no tiene un formato válido.'
}
$parsedMinimum = [Version]::new()
if (-not [Version]::TryParse($MinSupportedVersion, [ref]$parsedMinimum)) {
    throw 'MinSupportedVersion no tiene un formato válido.'
}
if ($parsedMinimum -gt $parsedVersion) {
    throw 'MinSupportedVersion no puede ser mayor que Version.'
}

$downloadUri = [Uri]$DownloadUrl
if ($downloadUri.Scheme -ne [Uri]::UriSchemeHttps -or
    -not $downloadUri.AbsolutePath.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'DownloadUrl debe ser HTTPS y terminar en .zip.'
}

$SigningKeyPath = [IO.Path]::GetFullPath($SigningKeyPath)
$TrustedPublicKeyPath = [IO.Path]::GetFullPath($TrustedPublicKeyPath)
if (-not (Test-Path -LiteralPath $SigningKeyPath -PathType Leaf)) { throw 'No existe la llave privada de firma.' }
if (-not (Test-Path -LiteralPath $TrustedPublicKeyPath -PathType Leaf)) { throw 'No existe la llave pública confiable.' }
if ((Get-Item -LiteralPath $SigningKeyPath).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -or
    (Get-Item -LiteralPath $TrustedPublicKeyPath).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
    throw 'Las llaves de liberación no pueden ser enlaces o reparse points.'
}

$gitStatus = (& git -C $repoRoot status --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'No se pudo consultar el estado Git.' }
$isDirty = @($gitStatus).Count -gt 0
if ($isDirty -and -not $AllowDirtyWorkingTree) {
    throw 'El árbol Git tiene cambios. Confirme o revierta antes de producir una liberación.'
}

if ([string]::IsNullOrWhiteSpace($CodeSigningCertificateThumbprint) -and -not $AllowUnsignedBinaries) {
    throw 'La liberación productiva exige certificado Authenticode. Use -AllowUnsignedBinaries solo para staging.'
}
if (-not [string]::IsNullOrWhiteSpace($CodeSigningCertificateThumbprint) -and
    [string]::IsNullOrWhiteSpace($TimestampServer)) {
    throw 'La firma Authenticode exige TimestampServer.'
}
if (Test-Path -LiteralPath $releaseDirectory) {
    throw "La liberación ya existe y no será sobrescrita: $releaseDirectory"
}

$privateRsa = $null
$publicRsa = $null
$privateRsa = Import-ReleasePrivateKey $SigningKeyPath
$publicRsa = [Security.Cryptography.RSA]::Create()
try {
    $publicRsa.ImportFromPem([IO.File]::ReadAllText($TrustedPublicKeyPath))
    if ($privateRsa.KeySize -lt 3072 -or $publicRsa.KeySize -lt 3072) {
        throw 'Las llaves de actualización deben ser RSA de al menos 3072 bits.'
    }
    if ([Convert]::ToBase64String($privateRsa.ExportSubjectPublicKeyInfo()) -ne
        [Convert]::ToBase64String($publicRsa.ExportSubjectPublicKeyInfo())) {
        throw 'La llave privada no corresponde con la llave pública confiable.'
    }

    New-Item -ItemType Directory -Path $releaseDirectory | Out-Null
    New-Item -ItemType Directory -Path $workRoot | Out-Null

    $clientPublish = Join-Path $workRoot 'client'
    $serverPublish = Join-Path $workRoot 'server'
    $updaterPublish = Join-Path $workRoot 'updater'
    New-Item -ItemType Directory -Path $clientPublish, $serverPublish, $updaterPublish | Out-Null

    foreach ($project in @(
        'FileXfer.Server.Full/FileXfer.Server.csproj',
        'TransferClienteASE/TransferClienteASE.csproj',
        'TransferClienteASEUpdater/TransferClienteASE.Updater.csproj'
    )) {
        Invoke-Checked dotnet @('restore', $project)
        Assert-NoVulnerableNuGetPackages $project
    }

    Invoke-Checked npm @('ci', '--ignore-scripts') (Join-Path $repoRoot 'FileXfer.AdminDashboard')
    Invoke-Checked npm @('audit', '--audit-level=high') (Join-Path $repoRoot 'FileXfer.AdminDashboard')
    Invoke-Checked npm @('run', 'build') (Join-Path $repoRoot 'FileXfer.AdminDashboard')

    $versionProperties = @(
        "-p:Version=$Version",
        "-p:AssemblyVersion=$Version",
        "-p:FileVersion=$Version",
        '-p:ContinuousIntegrationBuild=true'
    )

    Invoke-Checked dotnet (@('publish', 'TransferClienteASEUpdater/TransferClienteASE.Updater.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-o', $updaterPublish) + $versionProperties)
    Invoke-Checked dotnet (@('publish', 'TransferClienteASE/TransferClienteASE.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '-o', $clientPublish) + $versionProperties)
    Invoke-Checked dotnet (@('publish', 'FileXfer.Server.Full/FileXfer.Server.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-o', $serverPublish) + $versionProperties)

    $updaterExe = Join-Path $updaterPublish 'TransferClienteASE.Updater.exe'
    $clientExe = Join-Path $clientPublish 'TransferClienteASE.exe'
    $serverExe = Join-Path $serverPublish 'FileXfer.Server.exe'
    foreach ($required in @($updaterExe, $clientExe, $serverExe)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "No se publicó $required" }
    }

    Copy-Item -LiteralPath $updaterExe -Destination (Join-Path $clientPublish 'TransferClienteASE.Updater.exe')
    Copy-Item -LiteralPath $TrustedPublicKeyPath -Destination (Join-Path $clientPublish 'update-public.pem')

    Invoke-Checked dotnet @('run', '--project', 'FileXfer.Security.SmokeTests/FileXfer.Security.SmokeTests.csproj', '-c', 'Release', '--', $updaterExe)
    Invoke-Checked dotnet @('run', '--project', 'FileXfer.FileSafety.SmokeTests/FileXfer.FileSafety.SmokeTests.csproj', '-c', 'Release')
    Invoke-Checked dotnet @('run', '--project', 'FileXfer.Pdf.SmokeTests/FileXfer.Pdf.SmokeTests.csproj', '-c', 'Release', '--', (Join-Path $repoRoot 'tools/qpdf/win-x64/bin/qpdf.exe'))
    $powerShellExecutable = (Get-Process -Id $PID).Path
    Invoke-Checked $powerShellExecutable @('-NoProfile', '-NonInteractive', '-File', 'deployment/Test-StagingReadiness.Smoke.ps1')

    $productionReady = -not $isDirty
    if (-not [string]::IsNullOrWhiteSpace($CodeSigningCertificateThumbprint)) {
        $certificate = Get-CodeSigningCertificate $CodeSigningCertificateThumbprint
        foreach ($binary in @($clientExe, $updaterExe, (Join-Path $clientPublish 'TransferClienteASE.Updater.exe'), $serverExe)) {
            $result = Set-AuthenticodeSignature -LiteralPath $binary -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer $TimestampServer
            if ($result.Status -ne [Management.Automation.SignatureStatus]::Valid) {
                throw "Firma Authenticode inválida para ${binary}: $($result.StatusMessage)"
            }
        }
    }
    else {
        $productionReady = $false
    }

    $symbolsRoot = Join-Path $workRoot 'symbols'
    foreach ($component in @(
        @{ Name = 'client'; Path = $clientPublish },
        @{ Name = 'server'; Path = $serverPublish },
        @{ Name = 'updater'; Path = $updaterPublish }
    )) {
        foreach ($symbol in Get-ChildItem -LiteralPath $component.Path -Filter '*.pdb' -File -Recurse) {
            $relativeSymbol = [IO.Path]::GetRelativePath($component.Path, $symbol.FullName)
            $symbolDestination = Join-Path (Join-Path $symbolsRoot $component.Name) $relativeSymbol
            New-Item -ItemType Directory -Path (Split-Path -Parent $symbolDestination) -Force | Out-Null
            Copy-Item -LiteralPath $symbol.FullName -Destination $symbolDestination
            Remove-Item -LiteralPath $symbol.FullName -Force
        }
    }

    foreach ($developmentConfig in @(
        (Join-Path $serverPublish 'appsettings.Development.json'),
        (Join-Path $serverPublish 'appsettings.ClientUpdates.template.Development.json'),
        (Join-Path $serverPublish 'appsettings.ClientUpdates.template.json')
    )) {
        if (Test-Path -LiteralPath $developmentConfig) {
            Remove-Item -LiteralPath $developmentConfig -Force
        }
    }

    $clientDirectory = Join-Path $releaseDirectory 'client'
    $serverDirectory = Join-Path $releaseDirectory 'server'
    $deploymentDirectory = Join-Path $releaseDirectory 'deployment'
    $toolsDirectory = Join-Path $releaseDirectory 'tools'
    New-Item -ItemType Directory -Path $clientDirectory, $serverDirectory, $deploymentDirectory, $toolsDirectory | Out-Null

    $clientZip = Join-Path $clientDirectory "TransferClienteASE-$Version-win-x64.zip"
    $serverZip = Join-Path $serverDirectory "FileXfer.Server-$Version-win-x64.zip"
    $deploymentZip = Join-Path $deploymentDirectory "FileXfer.Deployment-$Version.zip"
    $symbolsZip = Join-Path $toolsDirectory "FileXfer.Symbols-$Version.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($clientPublish, $clientZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    [IO.Compression.ZipFile]::CreateFromDirectory($serverPublish, $serverZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    [IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $repoRoot 'deployment'), $deploymentZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    [IO.Compression.ZipFile]::CreateFromDirectory($symbolsRoot, $symbolsZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    Copy-Item -LiteralPath $updaterExe -Destination (Join-Path $toolsDirectory 'TransferClienteASE.Updater.exe')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Verify-Release.ps1') -Destination (Join-Path $toolsDirectory 'Verify-Release.ps1')
    Copy-Item -LiteralPath $TrustedPublicKeyPath -Destination (Join-Path $releaseDirectory 'update-public.pem')

    $clientHash = Get-Sha256Hex $clientZip
    $canonical = "FILEXFER-UPDATE-V1`ntransfer-cliente-ase`n$Version`n$clientHash"
    $updateSignature = [Convert]::ToBase64String(
        $privateRsa.SignData(
            [Text.Encoding]::UTF8.GetBytes($canonical),
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pss))

    $updateSettings = [ordered]@{
        ClientUpdates = [ordered]@{
            Clients = [ordered]@{
                'transfer-cliente-ase' = [ordered]@{
                    LatestVersion = $Version
                    MinSupportedVersion = $MinSupportedVersion
                    DownloadUrl = $DownloadUrl
                    Sha256 = $clientHash
                    SignatureBase64 = $updateSignature
                    SignatureAlgorithm = 'RSA-PSS-SHA256'
                    ReleaseNotes = $ReleaseNotes
                    IsCritical = $IsCritical
                }
            }
        }
    }
    $settingsPath = Join-Path $releaseDirectory 'client-update.settings.json'
    Write-Utf8NoBom $settingsPath (($updateSettings | ConvertTo-Json -Depth 8) + "`n")

    $artifactPaths = @(
        $clientZip,
        $serverZip,
        $deploymentZip,
        $symbolsZip,
        (Join-Path $toolsDirectory 'TransferClienteASE.Updater.exe'),
        (Join-Path $toolsDirectory 'Verify-Release.ps1'),
        (Join-Path $releaseDirectory 'update-public.pem'),
        $settingsPath
    )
    $artifacts = foreach ($path in $artifactPaths) {
        $relative = [IO.Path]::GetRelativePath($releaseDirectory, $path).Replace('\', '/')
        [ordered]@{
            Path = $relative
            Length = (Get-Item -LiteralPath $path).Length
            Sha256 = Get-Sha256Hex $path
        }
    }

    $commit = (& git -C $repoRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo obtener el commit Git.' }
    $manifest = [ordered]@{
        SchemaVersion = 1
        Product = 'Transfer ASE 2027'
        Version = $Version
        Runtime = 'win-x64'
        CreatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        GitCommit = $commit
        GitDirty = $isDirty
        ProductionReady = $productionReady
        UpdatePackage = [ordered]@{
            ClientId = 'transfer-cliente-ase'
            Path = [IO.Path]::GetRelativePath($releaseDirectory, $clientZip).Replace('\', '/')
            Sha256 = $clientHash
            SignatureBase64 = $updateSignature
            SignatureAlgorithm = 'RSA-PSS-SHA256'
        }
        Artifacts = @($artifacts)
    }
    $manifestPath = Join-Path $releaseDirectory 'release-manifest.json'
    Write-Utf8NoBom $manifestPath (($manifest | ConvertTo-Json -Depth 8) + "`n")

    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    $manifestSignature = [Convert]::ToBase64String(
        $privateRsa.SignData(
            $manifestBytes,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pss))
    Write-Utf8NoBom (Join-Path $releaseDirectory 'release-manifest.sig') ($manifestSignature + "`n")

    $checksumLines = @($artifacts | Sort-Object Path | ForEach-Object { "$($_.Sha256) *$($_.Path)" })
    $checksumLines += "$(Get-Sha256Hex $manifestPath) *release-manifest.json"
    Write-Utf8NoBom (Join-Path $releaseDirectory 'checksums.sha256') (($checksumLines -join "`n") + "`n")

    $verifyArguments = @(
        '-NoProfile', '-File', (Join-Path $PSScriptRoot 'Verify-Release.ps1'),
        '-ReleaseDirectory', $releaseDirectory,
        '-TrustedPublicKeyPath', $TrustedPublicKeyPath
    )
    if (-not $productionReady) { $verifyArguments += '-AllowNonProduction' }
    Invoke-Checked pwsh $verifyArguments

    Write-Host "Liberación verificada: $releaseDirectory"
    Write-Host "ProductionReady=$productionReady"
}
finally {
    if ($null -ne $privateRsa) { $privateRsa.Dispose() }
    if ($null -ne $publicRsa) { $publicRsa.Dispose() }
    if (Test-Path -LiteralPath $workRoot) {
        $resolvedWork = [IO.Path]::GetFullPath($workRoot)
        $outputPrefix = $OutputRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not ($resolvedWork + [IO.Path]::DirectorySeparatorChar).StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'La carpeta temporal salió del directorio de artefactos; no se elimina.'
        }
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force
    }
}
