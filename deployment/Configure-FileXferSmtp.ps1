#requires -Version 7.0
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$InstallDirectory = 'C:\Servicios\FileXferPruebas',
    [ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$ServiceName = 'FileXferPruebas.Server',
    [string]$SmtpHost = 'mail.ase-sinaloa.gob.mx',
    [ValidateRange(1,65535)][int]$SmtpPort = 465,
    [string]$Account = 'juan.carrillo@ase-sinaloa.gob.mx',
    [string]$Recipient = 'juan.carrillo@ase-sinaloa.gob.mx',
    [uri]$HealthUri = 'https://192.168.2.243:51136/healthz',
    [uri]$ReadinessUri = 'https://192.168.2.243:51136/readyz',
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Net.Mail.MailAddress]::new($Account) | Out-Null
[Net.Mail.MailAddress]::new($Recipient) | Out-Null
if ([uri]::CheckHostName($SmtpHost) -eq [UriHostNameType]::Unknown) { throw 'Indique únicamente el nombre DNS del servidor SMTP.' }
$install = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$configPath = Join-Path $install 'appsettings.Production.json'
$service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
if ($null -eq $service -or -not $service.PathName.Contains($install + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'El servicio indicado no corresponde a la instalación indicada.'
}
foreach ($library in @('BouncyCastle.Cryptography.dll', 'MimeKit.dll', 'MailKit.dll')) {
    $libraryPath = Join-Path $install $library
    if (-not (Test-Path -LiteralPath $libraryPath -PathType Leaf)) { throw 'Publique primero el servidor 2.2.1.11 o posterior, con soporte SMTP 465.' }
    [Reflection.Assembly]::LoadFrom($libraryPath) | Out-Null
}
$originalBytes = [IO.File]::ReadAllBytes($configPath)
$originalAcl = Get-Acl -LiteralPath $configPath
$config = [Text.Encoding]::UTF8.GetString($originalBytes).TrimStart([char]0xFEFF) | ConvertFrom-Json -AsHashtable
if (-not $config.ContainsKey('Alerts')) { $config['Alerts'] = @{} }

# Rechazar overrides silenciosos: la configuración del servicio tiene prioridad sobre JSON.
$registry = Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$environmentProperty = $registry.PSObject.Properties['Environment']
$entries = if ($null -ne $environmentProperty) { @($environmentProperty.Value) } else { @() }
$overrides = @($entries | Where-Object { $_ -match '^Alerts__(Enabled|SmtpHost|SmtpPort|SmtpUseTls|SmtpUser|SmtpPassword|EmailFrom|EmailTo)=' })
foreach ($name in @('Enabled','SmtpHost','SmtpPort','SmtpUseTls','SmtpUser','SmtpPassword','EmailFrom','EmailTo')) {
    if ($null -ne [Environment]::GetEnvironmentVariable("Alerts__$name", 'Machine')) { $overrides += "Alerts__$name" }
}
if ($overrides.Count -gt 0) { throw 'Existen variables Alerts__ que prevalecen sobre JSON. Resuélvalas con el administrador antes de aplicar este archivo; no se imprimieron sus valores.' }

$securePassword = Read-Host "Contraseña SMTP de $Account (no se mostrará)" -AsSecureString
if ($securePassword.Length -eq 0) { throw 'La contraseña no puede estar vacía.' }
$credential = [Net.NetworkCredential]::new($Account, $securePassword)
$smtp = [MailKit.Net.Smtp.SmtpClient]::new()
$smtp.Timeout = 15000
try {
    $socketOptions = if ($SmtpPort -eq 465) { [MailKit.Security.SecureSocketOptions]::SslOnConnect } else { [MailKit.Security.SecureSocketOptions]::StartTls }
    $smtp.Connect($SmtpHost, $SmtpPort, $socketOptions)
    $smtp.Authenticate($credential)
    'PASS: conexión TLS y autenticación SMTP comprobadas. No se envió ningún correo.'
} catch {
    throw ('SMTP no quedó verificado; no se cambió la configuración. Tipo de error: ' + $_.Exception.GetBaseException().GetType().Name)
} finally {
    if ($smtp.IsConnected) { try { $smtp.Disconnect($true) } catch { } }
    $smtp.Dispose()
}
if ($CheckOnly) { $securePassword.Dispose(); return }

# El JSON queda accesible únicamente a SYSTEM, Administradores y la cuenta del servicio.
# Es una protección por permisos de Windows, no cifrado del secreto en reposo.
$acl = [Security.AccessControl.FileSecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'Allow'))
}
$identity = if ($service.StartName -eq 'LocalSystem') { [Security.Principal.SecurityIdentifier]::new('S-1-5-18') } else { [Security.Principal.NTAccount]::new($service.StartName).Translate([Security.Principal.SecurityIdentifier]) }
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'Read', 'Allow'))
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-ffff'
$temporary = Join-Path $install "smtp-settings.$stamp.tmp"
$backup = Join-Path $install "smtp-settings.$stamp.backup"
$written = $false
try {
    $config.Alerts.Enabled = $true
    $config.Alerts.SmtpHost = $SmtpHost
    $config.Alerts.SmtpPort = $SmtpPort
    $config.Alerts.SmtpUseTls = $true
    $config.Alerts.SmtpUser = $Account
    $config.Alerts.SmtpPassword = $credential.Password
    $config.Alerts.EmailFrom = $Account
    $config.Alerts.EmailTo = $Recipient
    foreach ($path in @($temporary, $backup)) {
        New-Item -ItemType File -Path $path -ErrorAction Stop | Out-Null
        Set-Acl -LiteralPath $path -AclObject $acl
    }
    [IO.File]::WriteAllBytes($backup, $originalBytes)
    [IO.File]::WriteAllText($temporary, ($config | ConvertTo-Json -Depth 64), [Text.UTF8Encoding]::new($false))
    Set-Acl -LiteralPath $configPath -AclObject $acl
    [IO.File]::Replace($temporary, $configPath, $null)
    $written = $true
    Set-Acl -LiteralPath $configPath -AclObject $acl
    Restart-Service -Name $ServiceName -ErrorAction Stop
    $healthy = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            $health = Invoke-WebRequest -Uri $HealthUri -TimeoutSec 5
            $ready = Invoke-WebRequest -Uri $ReadinessUri -TimeoutSec 5
            if ($health.StatusCode -eq 200 -and $ready.StatusCode -eq 200) { $healthy = $true; break }
        } catch { }
        Start-Sleep -Seconds 2
    }
    if (-not $healthy) { throw 'El servicio no recuperó healthz/readyz.' }
    'PASS: SMTP configurado y servicio disponible. No se enviaron correos de prueba ni se reprocesaron avisos agotados.'
    "Respaldo protegido: $backup"
    'Los avisos nuevos y los pendientes del servicio usarán este canal; los agotados conservan su evidencia y requieren reproceso autorizado.'
} catch {
    $failureType = $_.Exception.GetType().Name
    if ($written) {
        [IO.File]::WriteAllBytes($configPath, $originalBytes)
        Set-Acl -LiteralPath $configPath -AclObject $originalAcl
        Restart-Service -Name $ServiceName -ErrorAction Continue
    } else { Set-Acl -LiteralPath $configPath -AclObject $originalAcl }
    throw "La configuración SMTP no fue activada correctamente; se restauró el archivo anterior si fue reemplazado. Error: $failureType."
} finally {
    $config.Alerts.SmtpPassword = $null
    $credential = $null
    $securePassword.Dispose()
    # Solo el temporal exacto creado por esta ejecución; nunca borrado recursivo.
    if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force }
}
