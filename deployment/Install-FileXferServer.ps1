[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [string]$ServerPackagePath,

    [string]$ProductionConfigPath,

    [string]$ExpectedSha256,

    [string]$InstallDirectory = 'C:\Program Files\ASE\FileXfer.Server',

    [string]$ServiceName = 'FileXfer.Server',

    [string]$ServiceDisplayName = 'ASE FileXfer Server',

    [string]$ServiceAccount = 'NT AUTHORITY\NetworkService',

    [Parameter(Mandatory)]
    [ValidatePattern('^https://')]
    [string]$HealthUri,

    [ValidateRange(1, 65535)]
    [int]$HttpsPort = 51236,

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
    if ($full.Split([IO.Path]::DirectorySeparatorChar, [StringSplitOptions]::RemoveEmptyEntries).Count -lt 3) {
        throw 'El directorio de instalación es demasiado amplio. Use una carpeta dedicada.'
    }
    return $full
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
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw 'Falta appsettings.Production.json. No se instalará un servidor sin configuración explícita.'
    }
    $raw = Get-Content -LiteralPath $Path -Raw
    if ($raw -match '__[A-Z0-9_]+__') {
        throw 'appsettings.Production.json todavía contiene marcadores __PENDIENTES__.'
    }
    try { $config = $raw | ConvertFrom-Json -ErrorAction Stop }
    catch { throw 'appsettings.Production.json no contiene JSON válido.' }

    foreach ($value in @(
        $config.ConnectionStrings.FileXferDb,
        $config.ConnectionStrings.BudiDev,
        $config.Security.Jwt.SigningKey,
        $config.Security.PathEncryptionKey,
        $config.Storage.BasePath
    )) {
        if ([string]::IsNullOrWhiteSpace([string]$value)) {
            throw 'La configuración productiva omite una conexión, llave o ruta obligatoria.'
        }
    }
    if ([string]$config.Security.Jwt.SigningKey -eq 'CHANGE_ME' -or
        ([string]$config.Security.Jwt.SigningKey).Length -lt 32) {
        throw 'Security:Jwt:SigningKey debe ser un secreto real de al menos 32 caracteres.'
    }
    if ([bool]$config.Security.Receipts.AllowEphemeralDevelopmentKey) {
        throw 'Security:Receipts:AllowEphemeralDevelopmentKey debe ser false en producción.'
    }
}

function Invoke-ServiceControl {
    param([string[]]$Arguments)
    & sc.exe @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe falló con código $LASTEXITCODE." }
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

if (-not $PSCmdlet.ShouldProcess($install, 'Instalar o actualizar FileXfer.Server con rollback')) { return }

New-Item -ItemType Directory -Path $stage | Out-Null
try {
    Expand-SafeArchive $package $stage
    $stagedExe = Join-Path $stage 'FileXfer.Server.exe'
    if (-not (Test-Path -LiteralPath $stagedExe -PathType Leaf)) {
        throw 'El paquete no contiene FileXfer.Server.exe.'
    }

    $stagedConfig = Join-Path $stage 'appsettings.Production.json'
    if (-not [string]::IsNullOrWhiteSpace($ProductionConfigPath)) {
        $configSource = [IO.Path]::GetFullPath($ProductionConfigPath)
        Assert-ProductionConfiguration $configSource
        Copy-Item -LiteralPath $configSource -Destination $stagedConfig -Force
    }
    elseif ($hadPreviousInstall) {
        $currentConfig = Join-Path $install 'appsettings.Production.json'
        Assert-ProductionConfiguration $currentConfig
        Copy-Item -LiteralPath $currentConfig -Destination $stagedConfig -Force
    }
    else {
        throw 'La primera instalación exige -ProductionConfigPath.'
    }

    if ($null -ne $service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }

    if ($hadPreviousInstall) { Move-Item -LiteralPath $install -Destination $backup }
    Move-Item -LiteralPath $stage -Destination $install

    $binaryPath = '"' + (Join-Path $install 'FileXfer.Server.exe') + '"'
    if ($null -eq $service) {
        Invoke-ServiceControl @('create', $ServiceName, "binPath= $binaryPath", 'start= auto', "DisplayName= $ServiceDisplayName", "obj= $ServiceAccount")
    }
    else {
        Invoke-ServiceControl @('config', $ServiceName, "binPath= $binaryPath", 'start= auto', "DisplayName= $ServiceDisplayName", "obj= $ServiceAccount")
    }
    Invoke-ServiceControl @('description', $ServiceName, 'Servicio seguro de transferencia de archivos de la ASE Sinaloa')
    Invoke-ServiceControl @('failure', $ServiceName, 'reset= 86400', 'actions= restart/5000/restart/15000/restart/60000')

    if ($OpenWindowsFirewall) {
        $ruleName = "ASE FileXfer HTTPS $HttpsPort"
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
    $installExists = Test-Path -LiteralPath $install -PathType Container
    if ($installExists) {
        try {
            $running = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
            if ($null -ne $running -and $running.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue }
            Move-Item -LiteralPath $install -Destination $failed
        }
        catch { }
    }
    if (Test-Path -LiteralPath $backup -PathType Container) {
        Move-Item -LiteralPath $backup -Destination $install
        try { Start-Service -Name $ServiceName -ErrorAction SilentlyContinue } catch { }
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $stage -PathType Container) {
        Write-Warning "Staging conservado para diagnóstico: $stage"
    }
}
