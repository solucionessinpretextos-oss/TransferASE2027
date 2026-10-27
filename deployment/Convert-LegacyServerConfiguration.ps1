#requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$LegacyConfigPath,

    [Parameter(Mandatory)]
    [string]$OutputPath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string[]]$AllowedHosts,

    [string[]]$SatTrustedRootCertificatePaths = @(),

    [string[]]$SatIntermediateCertificatePaths = @(),

    [string[]]$SatAllowedRevocationHosts = @(),

    [string]$ReceiptPrivateKeyPath = 'C:\ProgramData\ASE\FileXfer\secrets\receipt-signing-private.pem',

    [string]$ServiceAccount = 'NT AUTHORITY\NetworkService',

    [string]$TlsCertificateSubject,

    [string]$PathEncryptionKeyEnvironmentVariable = 'Security__PathEncryptionKey',

    [string]$LegacyPathEncryptionKeyEnvironmentVariable = 'Security__LegacyPathEncryptionKey',

    [switch]$GeneratePathEncryptionKeyForEmptyDatabase,

    [switch]$AllowLegacyDevelopmentSqlTransport,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Ensure-Object {
    param([pscustomobject]$Parent, [string]$Name)

    $property = $Parent.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value -or
        $property.Value -is [string] -or $property.Value -is [ValueType]) {
        $value = [pscustomobject][ordered]@{}
        $Parent | Add-Member -MemberType NoteProperty -Name $Name -Value $value -Force
        return $value
    }
    return [pscustomobject]$property.Value
}

function Set-Value {
    param([pscustomobject]$Parent, [string]$Name, $Value)
    $Parent | Add-Member -MemberType NoteProperty -Name $Name -Value $Value -Force
}

function Set-Default {
    param([pscustomobject]$Parent, [string]$Name, $Value)
    $property = $Parent.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value -or
        ($property.Value -is [string] -and [string]::IsNullOrWhiteSpace($property.Value))) {
        Set-Value $Parent $Name $Value
    }
}

function Get-PropertyValue {
    param($Parent, [string]$Name)

    if ($null -eq $Parent) { return $null }
    $property = $Parent.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Assert-Base64Key32 {
    param([string]$Value, [string]$Description)
    try { $bytes = [Convert]::FromBase64String($Value) }
    catch { throw "$Description no contiene Base64 válido." }
    if ($bytes.Length -ne 32) { throw "$Description debe decodificar exactamente 32 bytes." }
}

function Write-Utf8NoBomAtomic {
    param([string]$Path, [string]$Content)
    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $temp = Join-Path $directory ('.' + [IO.Path]::GetFileName($Path) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($temp, $Content, [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temp, $Path, $true)
    }
    finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
    }
}

function New-ReceiptSigningKey {
    param([string]$Path, [string]$Account)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (Test-Path -LiteralPath $fullPath -PathType Leaf) { return $fullPath }

    $directory = Split-Path -Parent $fullPath
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $rsa = [Security.Cryptography.RSA]::Create(3072)
    try {
        Write-Utf8NoBomAtomic $fullPath $rsa.ExportPkcs8PrivateKeyPem()
    }
    finally { $rsa.Dispose() }

    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    try {
        $serviceSid = ([Security.Principal.NTAccount]::new($Account)).Translate([Security.Principal.SecurityIdentifier]).Value
    }
    catch {
        throw "No se pudo resolver la cuenta de servicio '$Account' para proteger la llave de recibos."
    }
    $aclArguments = @(
        $fullPath,
        '/inheritance:r',
        '/grant:r',
        "*$($currentSid):(F)",
        '*S-1-5-18:(F)',
        '*S-1-5-32-544:(F)',
        "*$($serviceSid):(R)"
    )
    & icacls.exe @aclArguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'No se pudo restringir la ACL de la llave privada de recibos.'
    }
    return $fullPath
}

$legacyPath = [IO.Path]::GetFullPath($LegacyConfigPath)
$destination = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $legacyPath -PathType Leaf)) {
    throw "No existe la configuración legada: $legacyPath"
}
if (Test-Path -LiteralPath $destination -PathType Leaf -ErrorAction SilentlyContinue) {
    if (-not $Force) { throw "El archivo destino ya existe y no será sobrescrito: $destination" }
}

try { $config = Get-Content -LiteralPath $legacyPath -Raw | ConvertFrom-Json -ErrorAction Stop }
catch { throw 'La configuración legada no contiene JSON válido.' }

$kestrel = Ensure-Object $config 'Kestrel'
$endpoints = Ensure-Object $kestrel 'Endpoints'
$https = Ensure-Object $endpoints 'Https'
Set-Default $https 'Url' 'https://0.0.0.0:51136'
Set-Default $https 'Protocols' 'Http1AndHttp2'
if (-not [string]::IsNullOrWhiteSpace($TlsCertificateSubject)) {
    Set-Value $https 'Certificate' ([pscustomobject][ordered]@{
        Subject = $TlsCertificateSubject.Trim()
        Store = 'My'
        Location = 'LocalMachine'
        AllowInvalid = $false
    })
}

$connections = Ensure-Object $config 'ConnectionStrings'
foreach ($name in @('FileXferDb', 'BudiDev')) {
    if ([string]::IsNullOrWhiteSpace([string](Get-PropertyValue $connections $name))) {
        $environmentName = "ConnectionStrings__$name"
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($environmentName))) {
            throw "Falta ConnectionStrings:$name tanto en JSON como en $environmentName."
        }
    }
}

$security = Ensure-Object $config 'Security'
$jwt = Ensure-Object $security 'Jwt'
Set-Default $jwt 'Issuer' 'FileXferIssuer'
Set-Default $jwt 'Audience' 'FileXferClients'
Set-Default $jwt 'AccessTokenMinutes' 15
$jwtKey = [string](Get-PropertyValue $jwt 'SigningKey')
if ([string]::IsNullOrWhiteSpace($jwtKey)) {
    $jwtKey = [Environment]::GetEnvironmentVariable('Security__Jwt__SigningKey')
}
if ([string]::IsNullOrWhiteSpace($jwtKey) -or $jwtKey.Length -lt 32) {
    $jwtBytes = [byte[]]::new(48)
    [Security.Cryptography.RandomNumberGenerator]::Fill($jwtBytes)
    $jwtKey = [Convert]::ToBase64String($jwtBytes)
    Set-Value $jwt 'SigningKey' $jwtKey
    Write-Warning 'La llave JWT legada era inexistente o débil y fue rotada. Las sesiones activas deberán autenticarse nuevamente.'
}
Set-Value $jwt 'SigningKey' $jwtKey

$pathKey = [string](Get-PropertyValue $security 'PathEncryptionKey')
if ([string]::IsNullOrWhiteSpace($pathKey)) {
    $pathKey = [Environment]::GetEnvironmentVariable($PathEncryptionKeyEnvironmentVariable)
}
if ([string]::IsNullOrWhiteSpace($pathKey)) {
    if ($GeneratePathEncryptionKeyForEmptyDatabase) {
        $keyBytes = [byte[]]::new(32)
        [Security.Cryptography.RandomNumberGenerator]::Fill($keyBytes)
        $pathKey = [Convert]::ToBase64String($keyBytes)
    }
}
if (-not [string]::IsNullOrWhiteSpace($pathKey)) {
    Assert-Base64Key32 $pathKey 'Security:PathEncryptionKey'
}
Set-Value $security 'PathEncryptionKey' $pathKey
$legacyPathKey = [string](Get-PropertyValue $security 'LegacyPathEncryptionKey')
if ([string]::IsNullOrWhiteSpace($legacyPathKey)) {
    $legacyPathKey = [Environment]::GetEnvironmentVariable($LegacyPathEncryptionKeyEnvironmentVariable)
}
if ([string]::IsNullOrWhiteSpace($legacyPathKey)) {
    throw 'No se encontró Security:LegacyPathEncryptionKey. La migración exige la llave compartida con BuDi para leer y escribir las rutas históricas.'
}
elseif ($legacyPathKey.Length -lt 8) {
    throw 'Security:LegacyPathEncryptionKey debe contener al menos 8 caracteres.'
}
Set-Value $security 'LegacyPathEncryptionKey' $legacyPathKey
Set-Value $security 'PathWriteFormat' 'LegacyDes'
$sqlTransport = Ensure-Object $security 'SqlTransport'
Set-Value $sqlTransport 'AllowLegacyDevelopmentSqlTransport' ([bool]$AllowLegacyDevelopmentSqlTransport)
if ($AllowLegacyDevelopmentSqlTransport) {
    Write-Warning 'Se conserva temporalmente el transporte SQL legado solo para FileXferDev/Budi_Dev en red privada. No es apto para producción.'
}

$receipts = Ensure-Object $security 'Receipts'
$receiptPem = [string](Get-PropertyValue $receipts 'PrivateKeyPem')
$receiptPath = [string](Get-PropertyValue $receipts 'PrivateKeyPemPath')
if ([string]::IsNullOrWhiteSpace($receiptPem) -and [string]::IsNullOrWhiteSpace($receiptPath)) {
    $receiptPath = New-ReceiptSigningKey $ReceiptPrivateKeyPath $ServiceAccount
    Set-Value $receipts 'PrivateKeyPemPath' $receiptPath
}
Set-Default $receipts 'PrivateKeyPem' ''
Set-Default $receipts 'PrivateKeyPassword' ''
Set-Value $receipts 'AllowEphemeralDevelopmentKey' $false

$sat = Ensure-Object $security 'SAT'
Set-Value $sat 'AllowDevelopmentBypass' $false
Set-Value $sat 'AllowDevelopmentUntrustedChain' $false
Set-Value $sat 'RequireOcsp' ($SatAllowedRevocationHosts.Count -gt 0)
Set-Default $sat 'AcceptClockSkewMinutes' 10
Set-Default $sat 'FailOnUnknownRevocation' $true
Set-Default $sat 'OnlineTimeoutMs' 4000
if ($SatTrustedRootCertificatePaths.Count -gt 0) {
    Set-Value $sat 'TrustedRootCertificatePaths' @($SatTrustedRootCertificatePaths)
}
elseif (@(@(Get-PropertyValue $sat 'TrustedRootCertificatePaths') |
    Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }).Count -eq 0) {
    Set-Value $sat 'TrustedRootCertificatePaths' @(
        'certificates/sat/ARC5_IES.cer',
        'certificates/sat/ARC6_IES.cer')
}
if ($SatIntermediateCertificatePaths.Count -gt 0) {
    Set-Value $sat 'IntermediateCertificatePaths' @($SatIntermediateCertificatePaths)
}
elseif (@(@(Get-PropertyValue $sat 'IntermediateCertificatePaths') |
    Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }).Count -eq 0) {
    Set-Value $sat 'IntermediateCertificatePaths' @(
        'certificates/sat/AC5_SAT.cer',
        'certificates/sat/AC6_SAT.cer',
        'certificates/sat/AC7_SAT.cer')
}
if ($SatAllowedRevocationHosts.Count -gt 0) {
    Set-Value $sat 'AllowedRevocationHosts' @($SatAllowedRevocationHosts)
}
$trustedRootPaths = @(@(Get-PropertyValue $sat 'TrustedRootCertificatePaths') |
    Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
$allowedRevocationHosts = @(@(Get-PropertyValue $sat 'AllowedRevocationHosts') |
    Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
if ($trustedRootPaths.Count -eq 0) {
    throw 'La migración requiere raíces SAT confiables. No se habilitará bypass productivo.'
}
if ([bool](Get-PropertyValue $sat 'RequireOcsp') -and $allowedRevocationHosts.Count -eq 0) {
    throw 'RequireOcsp=true exige hosts OCSP/CRL permitidos.'
}

$rateLimit = Ensure-Object $security 'AuthRateLimit'
Set-Default $rateLimit 'PermitLimit' 10
Set-Default $rateLimit 'WindowSeconds' 60
$devicePolicy = Ensure-Object $security 'DevicePolicy'
Set-Default $devicePolicy 'RotationDays' 180
Set-Default $devicePolicy 'MinRsaBits' 2048
Set-Default $devicePolicy 'BlockLoginWhenExpired' $false

$alerts = Ensure-Object $config 'Alerts'
$alertDefaults = [ordered]@{
    Enabled = $false
    SmtpTimeoutSeconds = 15
    WebhookTimeoutSeconds = 10
    QueueCapacity = 256
    WorkerCount = 2
    MaxDeliveryAttempts = 3
    RetryInitialDelayMilliseconds = 1000
    DeduplicationWindowSeconds = 60
    ShutdownDrainSeconds = 15
    PersistenceTimeoutSeconds = 5
    PollIntervalMilliseconds = 500
    LeaseSeconds = 180
    BatchSize = 32
    DeliveredRetentionDays = 30
    DeadLetterRetentionDays = 90
    CleanupIntervalHours = 6
}
foreach ($entry in $alertDefaults.GetEnumerator()) { Set-Default $alerts $entry.Key $entry.Value }

$debug = Ensure-Object $config 'DebugFeatures'
Set-Value $debug 'EnableDeveloperDiagnostics' $false
Set-Value $debug 'EnableAdminDashboardCors' $false
Set-Value $debug 'EnableDebugUploadEndpoints' $false
Set-Default $debug 'AllowedEnvironments' @('Development')
Set-Default $debug 'AllowedRoles' @('admin', 'ops')

$storage = Ensure-Object $config 'Storage'
if ([string]::IsNullOrWhiteSpace([string](Get-PropertyValue $storage 'BasePath'))) {
    $legacyKestrelStorage = Get-PropertyValue $kestrel 'Storage'
    $legacyBasePath = Get-PropertyValue $legacyKestrelStorage 'BasePath'
    if (-not [string]::IsNullOrWhiteSpace([string]$legacyBasePath)) {
        Set-Value $storage 'BasePath' ([string]$legacyBasePath)
    }
}
if ([string]::IsNullOrWhiteSpace([string](Get-PropertyValue $storage 'BasePath'))) {
    throw 'La migración no encontró Storage:BasePath.'
}
$storageDefaults = [ordered]@{
    MaxUploadBytes = 0L
    MaxChunkBytes = 2097152
    MaxConcurrentUploadsTotal = 200
    MaxConcurrentUploadsPerRfc = 5
    FirstChunkTimeoutSeconds = 30
    IdleChunkTimeoutSeconds = 120
    MaxUploadDurationMinutes = 0
    ResumeRetentionHours = 72
}
foreach ($entry in $storageDefaults.GetEnumerator()) { Set-Default $storage $entry.Key $entry.Value }

# El manifiesto firmado externo es la única autoridad de actualización. Se
# eliminan tanto el manifiesto incompleto como el contrato HTTP legado.
$clientUpdates = Ensure-Object $config 'ClientUpdates'
Set-Value $clientUpdates 'Clients' ([pscustomobject][ordered]@{})
$config.PSObject.Properties.Remove('Actualizaciones')
$normalizedAllowedHosts = @($AllowedHosts | ForEach-Object { $_.Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
if ($normalizedAllowedHosts.Count -eq 0 -or $normalizedAllowedHosts -contains '*') {
    throw 'AllowedHosts debe declarar hosts concretos; no se permite comodín.'
}
Set-Value $config 'AllowedHosts' ($normalizedAllowedHosts -join ';')

$logging = Ensure-Object $config 'Logging'
$logLevel = Ensure-Object $logging 'LogLevel'
Set-Default $logLevel 'Default' 'Information'
Set-Default $logLevel 'Microsoft.AspNetCore' 'Warning'

Write-Utf8NoBomAtomic $destination (($config | ConvertTo-Json -Depth 20) + "`n")
Write-Host "PASS: configuración legada convertida sin habilitar canales de actualización inseguros."
Write-Host "Salida: $destination"
