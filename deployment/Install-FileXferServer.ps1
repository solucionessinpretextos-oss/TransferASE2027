[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [string]$ServerPackagePath,

    [string]$ProductionConfigPath,

    [string]$LegacyConfigPath,

    [string]$ExpectedSha256,

    [string]$InstallDirectory = 'C:\Servicios\FileXfer',

    [string]$ServiceName = 'FileXferService',

    [string]$ServiceDisplayName = 'ASE FileXfer Service',

    [string]$ServiceAccount = 'NT AUTHORITY\NetworkService',

    [string[]]$AllowedHosts = @('localhost', '127.0.0.1', '192.168.2.243', '187.141.128.194'),

    [string[]]$SatTrustedRootCertificatePaths = @(),

    [string[]]$SatIntermediateCertificatePaths = @(),

    [string[]]$SatAllowedRevocationHosts = @(),

    [string]$TlsCertificateSubject,

    [string]$ReceiptPrivateKeyPath = 'C:\ProgramData\ASE\FileXfer\secrets\receipt-signing-private.pem',

    [switch]$GeneratePathEncryptionKeyForEmptyDatabase,

    [switch]$AllowLegacyDevelopmentSqlTransport,

    [switch]$EnableClientUpdates,

    [Parameter(Mandatory)]
    [ValidatePattern('^https://')]
    [string]$HealthUri,

    [ValidateRange(1, 65535)]
    [int]$HttpsPort = 51136,

    [ValidateRange(10, 600)]
    [int]$HealthTimeoutSeconds = 90,

    [switch]$OpenWindowsFirewall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Abra PowerShell 7 como administrador y ejecute nuevamente el instalador.'
    }
}

function Assert-SafeInstallPath {
    param([string]$Path)

    $full = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $root = [IO.Path]::GetPathRoot($full).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([string]::Equals($full, $root, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'La raíz de una unidad no puede ser el directorio de instalación.'
    }
    if ($full.Split([IO.Path]::DirectorySeparatorChar, [StringSplitOptions]::RemoveEmptyEntries).Count -lt 2) {
        throw 'El directorio de instalación es demasiado amplio. Use una carpeta dedicada.'
    }
    return $full
}

function Get-PropertyValue {
    param($Parent, [string]$Name)

    if ($null -eq $Parent) { return $null }
    $property = $Parent.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-NestedValue {
    param($Root, [string[]]$Path)

    $current = $Root
    foreach ($part in $Path) {
        $current = Get-PropertyValue $current $part
        if ($null -eq $current) { return $null }
    }
    return $current
}

function Get-Setting {
    param($Config, [string[]]$Path, [string]$EnvironmentName)

    $value = Get-NestedValue $Config $Path
    if ($null -ne $value -and -not [string]::IsNullOrWhiteSpace([string]$value)) { return $value }
    if (-not [string]::IsNullOrWhiteSpace($EnvironmentName)) {
        return [Environment]::GetEnvironmentVariable($EnvironmentName)
    }
    return $null
}

function Assert-Package {
    param([string]$Path, [string]$Hash)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "No existe el paquete de servidor: $Path"
    }
    if (-not [IO.Path]::GetExtension($Path).Equals('.zip', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'El paquete de servidor debe ser ZIP.'
    }
    if (-not [string]::IsNullOrWhiteSpace($Hash)) {
        if ($Hash -notmatch '^[0-9a-fA-F]{64}$') { throw 'ExpectedSha256 no es un SHA-256 válido.' }
        $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        if (-not $actual.Equals($Hash, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'El SHA-256 del paquete de servidor no coincide. Instalación cancelada.'
        }
    }
}

function Expand-SafeArchive {
    param([string]$ArchivePath, [string]$Destination)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        if ($archive.Entries.Count -eq 0 -or $archive.Entries.Count -gt 30000) {
            throw 'El paquete tiene una cantidad inválida de entradas.'
        }
        [long]$expanded = 0
        $destinationRoot = [IO.Path]::GetFullPath($Destination).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ([string]::IsNullOrWhiteSpace($name) -or $name.StartsWith('/') -or
                [IO.Path]::IsPathRooted($name) -or $name.Split('/') -contains '..') {
                throw "El ZIP contiene una ruta insegura: $name"
            }
            if ($entry.Length -lt 0 -or $expanded -gt (4GB - $entry.Length)) {
                throw 'El ZIP excede el tamaño expandido permitido.'
            }
            $expanded += $entry.Length
            $target = [IO.Path]::GetFullPath((Join-Path $Destination $name))
            if (-not ($target + [IO.Path]::DirectorySeparatorChar).StartsWith($destinationRoot, [StringComparison]::OrdinalIgnoreCase)) {
                throw "El ZIP intenta escribir fuera de staging: $name"
            }
            if ($name.EndsWith('/')) {
                New-Item -ItemType Directory -Path $target -Force | Out-Null
                continue
            }
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            $input = $entry.Open()
            try {
                $output = [IO.FileStream]::new($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $input.CopyTo($output) } finally { $output.Dispose() }
            }
            finally { $input.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}

function Assert-ProductionConfiguration {
    param([string]$Path, [int]$ExpectedHttpsPort)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw 'Falta appsettings.Production.json. No se instalará un servidor sin configuración explícita.'
    }
    $raw = Get-Content -LiteralPath $Path -Raw
    if ($raw -match '__[A-Z0-9_]+__') {
        throw 'appsettings.Production.json todavía contiene marcadores __PENDIENTES__.'
    }
    try { $config = $raw | ConvertFrom-Json -ErrorAction Stop }
    catch { throw 'appsettings.Production.json no contiene JSON válido.' }

    $requiredSettings = @(
        @{ Path = @('ConnectionStrings', 'FileXferDb'); Env = 'ConnectionStrings__FileXferDb'; Description = 'ConnectionStrings:FileXferDb' },
        @{ Path = @('ConnectionStrings', 'BudiDev'); Env = 'ConnectionStrings__BudiDev'; Description = 'ConnectionStrings:BudiDev' },
        @{ Path = @('Storage', 'BasePath'); Env = 'Storage__BasePath'; Description = 'Storage:BasePath' }
    )
    foreach ($setting in $requiredSettings) {
        if ([string]::IsNullOrWhiteSpace([string](Get-Setting $config $setting.Path $setting.Env))) {
            throw "Falta $($setting.Description) tanto en JSON como en variable de entorno."
        }
    }

    $fileXferConnection = [string](Get-Setting $config @('ConnectionStrings', 'FileXferDb') 'ConnectionStrings__FileXferDb')
    $budiConnection = [string](Get-Setting $config @('ConnectionStrings', 'BudiDev') 'ConnectionStrings__BudiDev')
    $allowLegacySql = [bool](Get-NestedValue $config @('Security', 'SqlTransport', 'AllowLegacyDevelopmentSqlTransport'))
    $hasInsecureSql = $fileXferConnection -match '(?i)(^|;)\s*Encrypt\s*=\s*(False|Optional)\s*(;|$)' -or
        $fileXferConnection -match '(?i)(^|;)\s*TrustServerCertificate\s*=\s*True\s*(;|$)' -or
        $budiConnection -match '(?i)(^|;)\s*Encrypt\s*=\s*(False|Optional)\s*(;|$)' -or
        $budiConnection -match '(?i)(^|;)\s*TrustServerCertificate\s*=\s*True\s*(;|$)'
    if ($hasInsecureSql -and -not $allowLegacySql) {
        throw 'Las conexiones SQL de pruebas no cumplen TLS. Corrija SQL o habilite explícitamente la excepción restringida de pruebas.'
    }
    if ($allowLegacySql -and
        ($fileXferConnection -notmatch '(?i)(^|;)\s*(Database|Initial Catalog)\s*=\s*FileXferDev\s*(;|$)' -or
         $budiConnection -notmatch '(?i)(^|;)\s*(Database|Initial Catalog)\s*=\s*Budi_Dev\s*(;|$)')) {
        throw 'La excepción SQL legada solo es válida para FileXferDev y Budi_Dev.'
    }

    $jwtKey = [string](Get-Setting $config @('Security', 'Jwt', 'SigningKey') 'Security__Jwt__SigningKey')
    if ([string]::IsNullOrWhiteSpace($jwtKey) -or $jwtKey -eq 'CHANGE_ME' -or $jwtKey.Length -lt 32) {
        throw 'Security:Jwt:SigningKey debe ser un secreto real de al menos 32 caracteres.'
    }

    $pathKey = [string](Get-Setting $config @('Security', 'PathEncryptionKey') 'Security__PathEncryptionKey')
    try { $pathKeyBytes = [Convert]::FromBase64String($pathKey) }
    catch { throw 'Security:PathEncryptionKey no contiene Base64 válido.' }
    if ($pathKeyBytes.Length -ne 32) { throw 'Security:PathEncryptionKey debe decodificar exactamente 32 bytes.' }

    $allowEphemeral = Get-NestedValue $config @('Security', 'Receipts', 'AllowEphemeralDevelopmentKey')
    if ([bool]$allowEphemeral) {
        throw 'Security:Receipts:AllowEphemeralDevelopmentKey debe ser false en producción.'
    }
    $receiptPem = [string](Get-Setting $config @('Security', 'Receipts', 'PrivateKeyPem') 'Security__Receipts__PrivateKeyPem')
    $receiptPath = [string](Get-Setting $config @('Security', 'Receipts', 'PrivateKeyPemPath') 'Security__Receipts__PrivateKeyPemPath')
    if ([string]::IsNullOrWhiteSpace($receiptPem) -and [string]::IsNullOrWhiteSpace($receiptPath)) {
        throw 'Falta la llave RSA persistente para recibos digitales.'
    }
    if (-not [string]::IsNullOrWhiteSpace($receiptPath) -and -not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
        throw "No existe la llave privada de recibos: $receiptPath"
    }

    $trustedRoots = @(@(Get-NestedValue $config @('Security', 'SAT', 'TrustedRootCertificatePaths')) |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    $revocationHosts = @(@(Get-NestedValue $config @('Security', 'SAT', 'AllowedRevocationHosts')) |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    if ($trustedRoots.Count -eq 0 -or $revocationHosts.Count -eq 0) {
        throw 'Faltan raíces SAT confiables o hosts OCSP/CRL permitidos.'
    }
    foreach ($certificatePath in $trustedRoots) {
        if (-not (Test-Path -LiteralPath ([string]$certificatePath) -PathType Leaf)) {
            throw "No existe la raíz SAT configurada: $certificatePath"
        }
    }
    if ([bool](Get-NestedValue $config @('Security', 'SAT', 'AllowDevelopmentBypass')) -or
        [bool](Get-NestedValue $config @('Security', 'SAT', 'AllowDevelopmentUntrustedChain'))) {
        throw 'Los bypass SAT de desarrollo deben permanecer deshabilitados.'
    }

    $allowedHosts = [string](Get-Setting $config @('AllowedHosts') 'AllowedHosts')
    if ([string]::IsNullOrWhiteSpace($allowedHosts) -or $allowedHosts.Split(';') -contains '*') {
        throw 'AllowedHosts debe declarar hosts concretos; el comodín está prohibido.'
    }

    $httpsUrl = [string](Get-Setting $config @('Kestrel', 'Endpoints', 'Https', 'Url') 'Kestrel__Endpoints__Https__Url')
    $httpsUri = $null
    if (-not [Uri]::TryCreate($httpsUrl, [UriKind]::Absolute, [ref]$httpsUri) -or
        $httpsUri.Scheme -ne 'https' -or $httpsUri.Port -ne $ExpectedHttpsPort) {
        throw "Kestrel debe escuchar por HTTPS en el puerto interno $ExpectedHttpsPort."
    }
    $certificateSubject = [string](Get-Setting $config @('Kestrel', 'Endpoints', 'Https', 'Certificate', 'Subject') 'Kestrel__Endpoints__Https__Certificate__Subject')
    $certificatePath = [string](Get-Setting $config @('Kestrel', 'Endpoints', 'Https', 'Certificate', 'Path') 'Kestrel__Endpoints__Https__Certificate__Path')
    if ([string]::IsNullOrWhiteSpace($certificateSubject) -and [string]::IsNullOrWhiteSpace($certificatePath)) {
        throw 'Kestrel no tiene un certificado TLS explícito por almacén o archivo.'
    }
    if (-not [string]::IsNullOrWhiteSpace($certificatePath)) {
        $resolvedCertificatePath = if ([IO.Path]::IsPathRooted($certificatePath)) {
            $certificatePath
        } else {
            Join-Path (Split-Path -Parent $Path) $certificatePath
        }
        if (-not (Test-Path -LiteralPath $resolvedCertificatePath -PathType Leaf)) {
            throw "No existe el certificado TLS configurado: $resolvedCertificatePath"
        }
    }
}

function Convert-LegacyConfiguration {
    param([string]$Source, [string]$Destination)

    if ([string]::IsNullOrWhiteSpace($TlsCertificateSubject)) {
        throw 'La migración legada exige -TlsCertificateSubject para abandonar los PFX autocontenidos y el certificado filexfer.local.'
    }
    $converter = Join-Path $PSScriptRoot 'Convert-LegacyServerConfiguration.ps1'
    if (-not (Test-Path -LiteralPath $converter -PathType Leaf)) {
        throw 'Falta Convert-LegacyServerConfiguration.ps1 en el paquete de despliegue.'
    }
    $arguments = @{
        LegacyConfigPath = $Source
        OutputPath = $Destination
        AllowedHosts = $AllowedHosts
        SatTrustedRootCertificatePaths = $SatTrustedRootCertificatePaths
        SatIntermediateCertificatePaths = $SatIntermediateCertificatePaths
        SatAllowedRevocationHosts = $SatAllowedRevocationHosts
        ReceiptPrivateKeyPath = $ReceiptPrivateKeyPath
        ServiceAccount = $ServiceAccount
        TlsCertificateSubject = $TlsCertificateSubject
        Force = $true
    }
    if ($GeneratePathEncryptionKeyForEmptyDatabase) {
        $arguments.GeneratePathEncryptionKeyForEmptyDatabase = $true
    }
    if ($AllowLegacyDevelopmentSqlTransport) {
        $arguments.AllowLegacyDevelopmentSqlTransport = $true
    }
    & $converter @arguments
}

function Invoke-ServiceControl {
    param([string[]]$Arguments)
    & sc.exe @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe falló con código $LASTEXITCODE." }
}

function Protect-ConfigurationFile {
    param([string]$Path, [string]$Account)

    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    try {
        $serviceSid = ([Security.Principal.NTAccount]::new($Account)).Translate(
            [Security.Principal.SecurityIdentifier]).Value
    }
    catch {
        throw "No se pudo resolver la cuenta '$Account' para proteger appsettings.Production.json."
    }
    $aclArguments = @(
        $Path,
        '/inheritance:r',
        '/grant:r',
        "*$($currentSid):(F)",
        '*S-1-5-18:(F)',
        '*S-1-5-32-544:(F)',
        "*$($serviceSid):(R)"
    )
    & icacls.exe @aclArguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'No se pudo restringir la ACL de appsettings.Production.json.'
    }
}

function Wait-Healthy {
    param([Uri]$Uri, [int]$TimeoutSeconds)

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastError = $null
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $Uri -Method Get -TimeoutSec 10 -UseBasicParsing
            if ($response.StatusCode -eq 200) { return }
            $lastError = "HTTP $($response.StatusCode)"
        }
        catch { $lastError = $_.Exception.Message }
        Start-Sleep -Seconds 2
    }
    throw "El servicio no quedó saludable en $Uri. Último resultado: $lastError"
}

Assert-Administrator
$package = [IO.Path]::GetFullPath($ServerPackagePath)
$install = Assert-SafeInstallPath $InstallDirectory
Assert-Package $package $ExpectedSha256

$parent = Split-Path -Parent $install
$leaf = Split-Path -Leaf $install
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$stage = Join-Path $parent ($leaf + '.staging.' + [Guid]::NewGuid().ToString('N'))
$backup = Join-Path $parent ($leaf + '.backup.' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$failed = Join-Path $parent ($leaf + '.failed.' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$hadPreviousInstall = Test-Path -LiteralPath $install -PathType Container
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$createdService = $false
$newInstallActivated = $false
$previousServiceWasRunning = $null -ne $service -and $service.Status -ne 'Stopped'

if (-not $PSCmdlet.ShouldProcess($install, 'Instalar o actualizar FileXfer.Server con rollback')) { return }

New-Item -ItemType Directory -Path $stage | Out-Null
try {
    Expand-SafeArchive $package $stage
    $stagedExe = Join-Path $stage 'FileXfer.Server.exe'
    if (-not (Test-Path -LiteralPath $stagedExe -PathType Leaf)) {
        throw 'El paquete no contiene FileXfer.Server.exe.'
    }

    $activeUpdateSettings = Join-Path $stage 'client-update.settings.json'
    $pendingUpdateSettings = Join-Path $stage 'client-update.settings.pending.json'
    if ($EnableClientUpdates) {
        if (-not (Test-Path -LiteralPath $activeUpdateSettings -PathType Leaf)) {
            throw 'Se solicitó habilitar actualizaciones, pero el paquete no contiene client-update.settings.json.'
        }
    }
    elseif (Test-Path -LiteralPath $activeUpdateSettings -PathType Leaf) {
        Move-Item -LiteralPath $activeUpdateSettings -Destination $pendingUpdateSettings
        Write-Warning 'Actualización automática en espera: los clientes anteriores seguirán operando sin intentar un paquete firmado con la llave nueva.'
    }

    $stagedConfig = Join-Path $stage 'appsettings.Production.json'
    if (-not [string]::IsNullOrWhiteSpace($ProductionConfigPath)) {
        $configSource = [IO.Path]::GetFullPath($ProductionConfigPath)
        Copy-Item -LiteralPath $configSource -Destination $stagedConfig -Force
    }
    elseif (-not [string]::IsNullOrWhiteSpace($LegacyConfigPath)) {
        Convert-LegacyConfiguration ([IO.Path]::GetFullPath($LegacyConfigPath)) $stagedConfig
    }
    elseif ($hadPreviousInstall) {
        $currentProductionConfig = Join-Path $install 'appsettings.Production.json'
        $currentLegacyConfig = Join-Path $install 'appsettings.json'
        if (Test-Path -LiteralPath $currentProductionConfig -PathType Leaf) {
            Copy-Item -LiteralPath $currentProductionConfig -Destination $stagedConfig -Force
        }
        elseif (Test-Path -LiteralPath $currentLegacyConfig -PathType Leaf) {
            Convert-LegacyConfiguration $currentLegacyConfig $stagedConfig
        }
        else {
            throw 'La instalación existente no contiene una configuración productiva ni legada recuperable.'
        }
    }
    else {
        throw 'La primera instalación exige -ProductionConfigPath o -LegacyConfigPath.'
    }
    Assert-ProductionConfiguration $stagedConfig $HttpsPort

    if ($null -ne $service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }

    $remainingListeners = @(Get-NetTCPConnection -LocalPort $HttpsPort -State Listen -ErrorAction SilentlyContinue)
    if ($remainingListeners.Count -gt 0) {
        $processIds = ($remainingListeners.OwningProcess | Sort-Object -Unique) -join ', '
        throw "El puerto interno $HttpsPort continúa ocupado por PID $processIds. Detenga el hosting IIS/Kestrel anterior antes de reintentar."
    }

    if ($hadPreviousInstall) { Move-Item -LiteralPath $install -Destination $backup }
    Move-Item -LiteralPath $stage -Destination $install
    $newInstallActivated = $true
    Protect-ConfigurationFile (Join-Path $install 'appsettings.Production.json') $ServiceAccount

    $binaryPath = '"' + (Join-Path $install 'FileXfer.Server.exe') + '"'
    if ($null -eq $service) {
        Invoke-ServiceControl @('create', $ServiceName, "binPath= $binaryPath", 'start= auto', "DisplayName= $ServiceDisplayName", "obj= $ServiceAccount")
        $createdService = $true
    }
    else {
        Invoke-ServiceControl @('config', $ServiceName, "binPath= $binaryPath", 'start= auto', "DisplayName= $ServiceDisplayName", "obj= $ServiceAccount")
    }
    Invoke-ServiceControl @('description', $ServiceName, 'Servicio seguro de transferencia de archivos de la ASE Sinaloa')
    Invoke-ServiceControl @('failure', $ServiceName, 'reset= 86400', 'actions= restart/5000/restart/15000/restart/60000')

    if ($OpenWindowsFirewall) {
        $ruleName = "ASE FileXfer HTTPS interno $HttpsPort"
        if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
            New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $HttpsPort | Out-Null
        }
    }

    Start-Service -Name $ServiceName
    Wait-Healthy ([Uri]$HealthUri) $HealthTimeoutSeconds

    Write-Host "PASS: $ServiceDisplayName instalado y saludable."
    Write-Host "Instalación: $install"
    if ($hadPreviousInstall) { Write-Host "Respaldo recuperable: $backup" }
}
catch {
    if ($newInstallActivated -and (Test-Path -LiteralPath $install -PathType Container)) {
        try {
            $running = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
            if ($null -ne $running -and $running.Status -ne 'Stopped') {
                Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
            }
            Move-Item -LiteralPath $install -Destination $failed
        }
        catch { }
    }
    if ($createdService) {
        try { & sc.exe delete $ServiceName | Out-Null } catch { }
    }
    if (Test-Path -LiteralPath $backup -PathType Container) {
        Move-Item -LiteralPath $backup -Destination $install
        try { Start-Service -Name $ServiceName -ErrorAction SilentlyContinue } catch { }
    }
    elseif ($previousServiceWasRunning) {
        try { Start-Service -Name $ServiceName -ErrorAction SilentlyContinue } catch { }
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $stage -PathType Container) {
        Write-Warning "Staging conservado para diagnóstico: $stage"
    }
}
