[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ReleaseDirectory,

    [Parameter(Mandatory)]
    [string]$TrustedPublicKeyPath,

    [switch]$AllowNonProduction
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ReleaseDirectory = [IO.Path]::GetFullPath($ReleaseDirectory)
$TrustedPublicKeyPath = [IO.Path]::GetFullPath($TrustedPublicKeyPath)
if (-not (Test-Path -LiteralPath $ReleaseDirectory -PathType Container)) { throw 'No existe el directorio de liberación.' }
if (-not (Test-Path -LiteralPath $TrustedPublicKeyPath -PathType Leaf)) { throw 'No existe la llave pública confiable.' }

function Get-Sha256Hex {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Resolve-ArtifactPath {
    param([string]$RelativePath)

    if ([IO.Path]::IsPathRooted($RelativePath)) { throw "Ruta absoluta prohibida en manifiesto: $RelativePath" }
    $full = [IO.Path]::GetFullPath((Join-Path $ReleaseDirectory $RelativePath))
    $prefix = $ReleaseDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "El artefacto sale del directorio de liberación: $RelativePath"
    }
    return $full
}

$manifestPath = Join-Path $ReleaseDirectory 'release-manifest.json'
$manifestSignaturePath = Join-Path $ReleaseDirectory 'release-manifest.sig'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $manifestSignaturePath -PathType Leaf)) {
    throw 'Falta el manifiesto firmado de liberación.'
}

$publicRsa = [Security.Cryptography.RSA]::Create()
try {
    $publicRsa.ImportFromPem([IO.File]::ReadAllText($TrustedPublicKeyPath))
    if ($publicRsa.KeySize -lt 3072) { throw 'La llave pública debe ser RSA de al menos 3072 bits.' }

    try { $manifestSignature = [Convert]::FromBase64String([IO.File]::ReadAllText($manifestSignaturePath).Trim()) }
    catch [FormatException] { throw 'La firma del manifiesto no es Base64 válida.' }

    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    if (-not $publicRsa.VerifyData(
            $manifestBytes,
            $manifestSignature,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pss)) {
        throw 'La firma del manifiesto de liberación no es válida.'
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.SchemaVersion -ne 1 -or $manifest.Product -ne 'Transfer ASE 2027') {
        throw 'El manifiesto no corresponde al producto o esquema esperado.'
    }
    if (-not $manifest.ProductionReady -and -not $AllowNonProduction) {
        throw 'La liberación está marcada como no apta para producción.'
    }

    foreach ($artifact in $manifest.Artifacts) {
        $path = Resolve-ArtifactPath $artifact.Path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Falta el artefacto $($artifact.Path)." }
        $file = Get-Item -LiteralPath $path
        if ($file.Length -ne [long]$artifact.Length) { throw "Longitud inválida para $($artifact.Path)." }
        if ((Get-Sha256Hex $path) -ne ([string]$artifact.Sha256).ToLowerInvariant()) {
            throw "SHA-256 inválido para $($artifact.Path)."
        }
        if ($artifact.Path -match '(?i)(\.pfx$|\.p12$|\.key$|(^|/)(private|private[-_.]key|signing[-_.]key)(\.pem)?$)') {
            throw "El artefacto parece contener material privado: $($artifact.Path)"
        }
    }

    $update = $manifest.UpdatePackage
    if ($update.ClientId -ne 'transfer-cliente-ase' -or $update.SignatureAlgorithm -ne 'RSA-PSS-SHA256') {
        throw 'El contrato de actualización del manifiesto no es válido.'
    }
    $clientZip = Resolve-ArtifactPath $update.Path
    $clientHash = Get-Sha256Hex $clientZip
    if ($clientHash -ne ([string]$update.Sha256).ToLowerInvariant()) { throw 'El hash del paquete cliente no coincide.' }

    $standaloneRelative = "client/TransferClienteASE-$($manifest.Version)-standalone-win-x64.exe"
    $standalonePath = Resolve-ArtifactPath $standaloneRelative
    if (-not (Test-Path -LiteralPath $standalonePath -PathType Leaf)) {
        throw 'Falta el ejecutable autónomo del cliente.'
    }
    $standaloneHeader = [IO.File]::ReadAllBytes($standalonePath)[0..1]
    if ($standaloneHeader[0] -ne 0x4D -or $standaloneHeader[1] -ne 0x5A) {
        throw 'El ejecutable autónomo no tiene una cabecera PE válida.'
    }

    try { $updateSignature = [Convert]::FromBase64String([string]$update.SignatureBase64) }
    catch [FormatException] { throw 'La firma del paquete cliente no es Base64 válida.' }
    $canonical = "FILEXFER-UPDATE-V1`n$($update.ClientId)`n$($manifest.Version)`n$clientHash"
    if (-not $publicRsa.VerifyData(
            [Text.Encoding]::UTF8.GetBytes($canonical),
            $updateSignature,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pss)) {
        throw 'La firma canónica del paquete cliente no es válida.'
    }

    $requiredEntries = @(
        'TransferClienteASE.exe',
        'TransferClienteASE.Updater.exe',
        'update-public.pem'
    )
    $archive = [IO.Compression.ZipFile]::OpenRead($clientZip)
    try {
        if ($archive.Entries.Count -gt 20000) { throw 'El paquete cliente contiene demasiadas entradas.' }
        [long]$expanded = 0
        $entryNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            if ($entry.Length -lt 0 -or $expanded -gt (1GB - $entry.Length)) { throw 'El paquete cliente excede 1 GB expandido.' }
            $expanded += $entry.Length
            $normalized = $entry.FullName.Replace('\', '/')
            if ([string]::IsNullOrWhiteSpace($normalized) -or $normalized.StartsWith('/') -or
                [IO.Path]::IsPathRooted($normalized) -or $normalized.Split('/') -contains '..') {
                throw "Entrada ZIP insegura: $normalized"
            }
            if (-not $entryNames.Add($normalized)) { throw "Entrada ZIP duplicada: $normalized" }
            if ($normalized -match '(?i)(\.pfx$|\.p12$|\.key$|(^|/)(private|private[-_.]key|signing[-_.]key)(\.pem)?$)') {
                throw "El ZIP contiene material privado: $normalized"
            }
        }
        foreach ($required in $requiredEntries) {
            if (-not $entryNames.Contains($required)) { throw "El ZIP no contiene $required." }
        }

        $keyEntry = $archive.GetEntry('update-public.pem')
        $reader = [IO.StreamReader]::new($keyEntry.Open(), [Text.Encoding]::UTF8, $true)
        try { $packagedPem = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $packagedRsa = [Security.Cryptography.RSA]::Create()
        try {
            $packagedRsa.ImportFromPem($packagedPem)
            if ([Convert]::ToBase64String($publicRsa.ExportSubjectPublicKeyInfo()) -ne
                [Convert]::ToBase64String($packagedRsa.ExportSubjectPublicKeyInfo())) {
                throw 'La llave pública incluida en el ZIP no coincide con la raíz confiable.'
            }
        }
        finally { $packagedRsa.Dispose() }
    }
    finally { $archive.Dispose() }

    $updater = Resolve-ArtifactPath 'tools/TransferClienteASE.Updater.exe'
    & $updater $clientZip $ReleaseDirectory 'TransferClienteASE.exe' ([string]$manifest.Version) $clientHash ([string]$update.SignatureBase64) $TrustedPublicKeyPath '--verify-only'
    if ($LASTEXITCODE -ne 0) { throw "El updater rechazó el paquete con código $LASTEXITCODE." }

    $serverArtifact = @($manifest.Artifacts | Where-Object { $_.Path -match '^server/.+\.zip$' })
    if ($serverArtifact.Count -ne 1) { throw 'Debe existir exactamente un ZIP de servidor.' }
    $serverZip = Resolve-ArtifactPath $serverArtifact[0].Path
    $serverArchive = [IO.Compression.ZipFile]::OpenRead($serverZip)
    try {
        $serverNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $serverArchive.Entries) {
            $serverNames.Add($entry.FullName.Replace('\', '/')) | Out-Null
            if ($entry.FullName -match '(?i)(^|/)appsettings\.Development\.json$' -or
                $entry.FullName -match '(?i)\.pdb$') {
                throw "El paquete de servidor contiene un archivo no productivo: $($entry.FullName)"
            }
        }

        if (-not $serverNames.Contains('client-update.settings.json')) {
            throw 'El paquete de servidor no contiene client-update.settings.json.'
        }
        $clientPackageName = [IO.Path]::GetFileName($clientZip)
        $embeddedClientEntry = $serverArchive.GetEntry("wwwroot/updates/$clientPackageName")
        if ($null -eq $embeddedClientEntry) {
            throw 'El paquete de servidor no contiene el ZIP cliente que debe publicar.'
        }
        $embeddedHash = [Security.Cryptography.SHA256]::HashData($embeddedClientEntry.Open())
        if ([Convert]::ToHexString($embeddedHash).ToLowerInvariant() -ne $clientHash) {
            throw 'El ZIP cliente incluido en el servidor no coincide con el paquete firmado.'
        }
    }
    finally { $serverArchive.Dispose() }

    Write-Host "PASS release=$($manifest.Version) signed-manifest artifacts=$(@($manifest.Artifacts).Count) updater-verify-only"
}
finally {
    $publicRsa.Dispose()
}
