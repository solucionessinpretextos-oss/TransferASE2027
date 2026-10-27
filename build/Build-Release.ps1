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
$expectedDownloadFileName = "TransferClienteASE-$Version-win-x64.zip"
if (-not [IO.Path]::GetFileName($downloadUri.AbsolutePath).Equals(
        $expectedDownloadFileName,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "DownloadUrl debe terminar exactamente en /$expectedDownloadFileName."
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
    $qpdfBundle = Join-Path $workRoot 'qpdf-win-x64.zip'

    foreach ($project in @(
        'FileXfer.Server.Full/FileXfer.Server.csproj',
        'TransferClienteASE/TransferClienteASE.csproj',
        'TransferClienteASEUpdater/TransferClienteASE.Updater.csproj'
    )) {
        Invoke-Checked dotnet @('restore', $project)
        Assert-NoVulnerableNuGetPackages $project
    }

    $dashboardRoot = Join-Path $repoRoot 'FileXfer.AdminDashboard'
    Invoke-Checked npm @('ci', '--ignore-scripts') $dashboardRoot
    Invoke-Checked npm @('audit', '--audit-level=high') $dashboardRoot

    # El cargador endurecido de SWC rechaza caches cuyo ancestro pueda ser
    # reemplazado por otra identidad. ProgramData\ASE se crea con ACL explícita
    # para que la compilación no dependa de un cache inseguro en AppData.
    $swcAclRoot = Join-Path $env:ProgramData 'ASE\TransferBuildCache'
    $swcCache = Join-Path $swcAclRoot 'swc'
    New-Item -ItemType Directory -Path $swcCache -Force | Out-Null
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $icaclsArguments = @(
        $swcAclRoot,
        '/inheritance:r',
        '/grant:r',
        "*$($currentSid):(OI)(CI)(F)",
        '*S-1-5-18:(OI)(CI)(F)',
        '*S-1-5-32-544:(OI)(CI)(F)'
    )
    & icacls.exe @icaclsArguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo proteger el cache nativo de SWC.' }
    $previousSwcCache = $env:SWC_NATIVE_BINDING_CACHE
    try {
        $env:SWC_NATIVE_BINDING_CACHE = $swcCache
        Invoke-Checked npm @('run', 'build') $dashboardRoot
    }
    finally {
        $env:SWC_NATIVE_BINDING_CACHE = $previousSwcCache
    }

    $versionProperties = @(
        "-p:Version=$Version",
        "-p:AssemblyVersion=$Version",
        "-p:FileVersion=$Version",
        '-p:ContinuousIntegrationBuild=true'
    )

    Invoke-Checked dotnet (@('publish', 'TransferClienteASEUpdater/TransferClienteASE.Updater.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $updaterPublish) + $versionProperties)

    [IO.Compression.ZipFile]::CreateFromDirectory(
        (Join-Path $repoRoot 'tools/qpdf/win-x64'),
        $qpdfBundle,
        [IO.Compression.CompressionLevel]::Optimal,
        $false)

    # WPF genera un proyecto temporal para XAML. Compilar primero por RID evita
    # que esa fase pierda el código gRPC generado; publish --no-build se limita
    # después a producir el bundle único con los recursos ya incrustados.
    $updaterExe = Join-Path $updaterPublish 'TransferClienteASE.Updater.exe'
    if (-not (Test-Path -LiteralPath $updaterExe -PathType Leaf)) { throw "No se publicó $updaterExe" }
    $certificate = $null
    if (-not [string]::IsNullOrWhiteSpace($CodeSigningCertificateThumbprint)) {
        # El updater debe firmarse antes de incrustarlo en el EXE autónomo.
        $certificate = Get-CodeSigningCertificate $CodeSigningCertificateThumbprint
        $updaterSignature = Set-AuthenticodeSignature -LiteralPath $updaterExe -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer $TimestampServer
        if ($updaterSignature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
            throw "Firma Authenticode inválida para ${updaterExe}: $($updaterSignature.StatusMessage)"
        }
    }
    $embeddedAssetProperties = @(
        "-p:UpdaterExecutablePath=$updaterExe",
        "-p:UpdatePublicKeyPath=$TrustedPublicKeyPath",
        "-p:QpdfBundlePath=$qpdfBundle"
    )
    Invoke-Checked dotnet (@('build', 'TransferClienteASE/TransferClienteASE.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false') + $versionProperties + $embeddedAssetProperties)
    Invoke-Checked dotnet (@('publish', 'TransferClienteASE/TransferClienteASE.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-build', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:IncludeAllContentForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $clientPublish) + $versionProperties + $embeddedAssetProperties)
    Invoke-Checked dotnet (@('publish', 'FileXfer.Server.Full/FileXfer.Server.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '-o', $serverPublish) + $versionProperties)

    $satCertificateSource = Join-Path $repoRoot 'deployment\certificates\sat'
    if (-not (Test-Path -LiteralPath $satCertificateSource -PathType Container)) {
        throw 'Falta el paquete local de autoridades SAT AC5/AC6/AC7.'
    }
    $satCertificateDestination = Join-Path $serverPublish 'certificates\sat'
    New-Item -ItemType Directory -Path $satCertificateDestination -Force | Out-Null
    Get-ChildItem -LiteralPath $satCertificateSource -File |
        Copy-Item -Destination $satCertificateDestination -Force

    $clientExe = Join-Path $clientPublish 'TransferClienteASE.exe'
    $serverExe = Join-Path $serverPublish 'FileXfer.Server.exe'
    foreach ($required in @($updaterExe, $clientExe, $serverExe)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "No se publicó $required" }
    }

    $standaloneSelfTest = Start-Process -FilePath $clientExe -ArgumentList '--self-test' -Wait -PassThru
    if ($standaloneSelfTest.ExitCode -ne 0) {
        throw "El EXE autónomo no pudo materializar o ejecutar sus recursos integrados (código $($standaloneSelfTest.ExitCode))."
    }

    Copy-Item -LiteralPath $updaterExe -Destination (Join-Path $clientPublish 'TransferClienteASE.Updater.exe')
    Copy-Item -LiteralPath $TrustedPublicKeyPath -Destination (Join-Path $clientPublish 'update-public.pem')

    Invoke-Checked dotnet @('run', '--project', 'FileXfer.Security.SmokeTests/FileXfer.Security.SmokeTests.csproj', '-c', 'Release', '--', $updaterExe)
    Invoke-Checked dotnet @('run', '--project', 'FileXfer.FileSafety.SmokeTests/FileXfer.FileSafety.SmokeTests.csproj', '-c', 'Release')
    Invoke-Checked dotnet @('run', '--project', 'FileXfer.Pdf.SmokeTests/FileXfer.Pdf.SmokeTests.csproj', '-c', 'Release', '--', (Join-Path $repoRoot 'tools/qpdf/win-x64/bin/qpdf.exe'))
    Invoke-Checked dotnet @('run', '--project', 'TransferClienteASE.Resume.SmokeTests/TransferClienteASE.Resume.SmokeTests.csproj', '-c', 'Release')
    Invoke-Checked dotnet @('run', '--project', 'FileXfer.Resume.SmokeTests/FileXfer.Resume.SmokeTests.csproj', '-c', 'Release')
    $powerShellExecutable = (Get-Process -Id $PID).Path
    Invoke-Checked $powerShellExecutable @('-NoProfile', '-NonInteractive', '-File', 'deployment/Test-StagingReadiness.Smoke.ps1')

    $productionReady = -not $isDirty
    if (-not [string]::IsNullOrWhiteSpace($CodeSigningCertificateThumbprint)) {
        foreach ($binary in @($clientExe, $serverExe)) {
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
    $standaloneExe = Join-Path $clientDirectory "TransferClienteASE-$Version-standalone-win-x64.exe"
    $serverZip = Join-Path $serverDirectory "FileXfer.Server-$Version-win-x64.zip"
    $deploymentZip = Join-Path $deploymentDirectory "FileXfer.Deployment-$Version.zip"
    $symbolsZip = Join-Path $toolsDirectory "FileXfer.Symbols-$Version.zip"
    Copy-Item -LiteralPath $clientExe -Destination $standaloneExe
    [IO.Compression.ZipFile]::CreateFromDirectory($clientPublish, $clientZip, [IO.Compression.CompressionLevel]::Optimal, $false)
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

    # El ZIP del servidor ya lleva el paquete cliente y el manifiesto operativo.
    # Tras instalarlo, UseStaticFiles expone /updates/<paquete> y el endpoint de
    # metadatos lee client-update.settings.json con recarga en caliente.
    $serverUpdatesDirectory = Join-Path $serverPublish 'wwwroot\updates'
    New-Item -ItemType Directory -Path $serverUpdatesDirectory -Force | Out-Null
    Copy-Item -LiteralPath $clientZip -Destination (Join-Path $serverUpdatesDirectory ([IO.Path]::GetFileName($clientZip)))
    Copy-Item -LiteralPath $settingsPath -Destination (Join-Path $serverPublish 'client-update.settings.json')

    [IO.Compression.ZipFile]::CreateFromDirectory($serverPublish, $serverZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    [IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $repoRoot 'deployment'), $deploymentZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    [IO.Compression.ZipFile]::CreateFromDirectory($symbolsRoot, $symbolsZip, [IO.Compression.CompressionLevel]::Optimal, $false)

    $artifactPaths = @(
        $standaloneExe,
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
