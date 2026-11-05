#requires -Version 7.0
#requires -RunAsAdministrator
[CmdletBinding(DefaultParameterSetName = 'Configure')]
param(
    [string]$InstallDirectory = 'C:\Servicios\FileXferPruebas',
    [ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$ServiceName = 'FileXferPruebas.Server',
    [string]$SmtpHost = 'mail.ase-sinaloa.gob.mx',
    [ValidateRange(1,65535)][int]$SmtpPort = 465,
    [string]$Account = 'juan.carrillo@ase-sinaloa.gob.mx',
    [string]$Recipient = 'juan.carrillo@ase-sinaloa.gob.mx',
    [uri]$HealthUri = 'https://192.168.2.243:51136/healthz',
    [uri]$ReadinessUri = 'https://192.168.2.243:51136/readyz',
    [Parameter(Mandatory, ParameterSetName = 'Check')][switch]$CheckOnly,
    [Parameter(Mandatory, ParameterSetName = 'Connection')][switch]$ConnectionOnly
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

function Get-SmtpFailureMessage([Exception]$Exception, [string]$Stage) {
    $prefix = "Falló la etapa de $Stage. La configuración sigue intacta."
    for ($cause = $Exception; $null -ne $cause; $cause = $cause.InnerException) {
        if ($cause -is [MailKit.Net.Smtp.SmtpCommandException]) {
            $code = [int]$cause.StatusCode
            $status = "SMTP $code"
            # El texto remoto puede contener datos sensibles. Solo se muestra el código numérico.
            if ($cause.Message -match '\A(?:[45][0-9]{2}[ -])?([45]\.[0-9]{1,3}\.[0-9]{1,3})(?=\s|\z)') {
                $status += " ($($Matches[1]))"
            }
            $detail = switch ($code) {
                421 { 'El servidor de correo no está disponible para esta conexión. Revise disponibilidad y restricciones de acceso.' }
                454 { 'El servidor informó una falla temporal. Pida al administrador de correo revisar el servicio y los registros de acceso.' }
                530 { 'El servidor exige un paso de autenticación o cifrado. Revise la política SMTP para esta conexión.' }
                534 { 'El mecanismo de autenticación no cumple la política del servidor. Confirme qué método admite esta cuenta.' }
                535 { 'El servidor rechazó la autenticación. Verifique el usuario, la contraseña y si SMTP AUTH está habilitado para la cuenta.' }
                538 { 'El servidor exige cifrado para el mecanismo de autenticación.' }
                554 { 'El servidor rechazó el comando. Pida al administrador de correo revisar la política y los registros de acceso.' }
                default { 'Consulte ese código con el administrador de correo.' }
            }
            return "$prefix $status. $detail"
        }
        if ($cause -is [MailKit.Security.SslHandshakeException]) {
            return "$prefix No se pudo establecer TLS. Revise el nombre del servidor, el puerto y el certificado."
        }
        if ($cause -is [Net.Sockets.SocketException]) {
            return "$prefix Error de red $($cause.SocketErrorCode). Revise DNS y acceso al puerto SMTP desde este equipo."
        }
    }
    return "$prefix Tipo de error: $($Exception.GetBaseException().GetType().Name)."
}

$securePassword = $null
$credential = $null
$authenticated = $false
$smtp = [MailKit.Net.Smtp.SmtpClient]::new()
$smtp.Timeout = 15000
try {
    $socketOptions = if ($SmtpPort -eq 465) { [MailKit.Security.SecureSocketOptions]::SslOnConnect } else { [MailKit.Security.SecureSocketOptions]::StartTls }
    try {
        $smtp.Connect($SmtpHost, $SmtpPort, $socketOptions)
    } catch {
        throw (Get-SmtpFailureMessage -Exception $_.Exception -Stage 'conexión, TLS o saludo SMTP')
    }
    "PASS: conexión, TLS y saludo SMTP comprobados en ${SmtpHost}:$SmtpPort."
    $supportsAuth = $smtp.Capabilities.HasFlag([MailKit.Net.Smtp.SmtpCapabilities]::Authentication)
    if ($ConnectionOnly) {
        if ($supportsAuth) { 'El servidor anuncia SMTP AUTH.' } else { 'El servidor no anuncia SMTP AUTH para esta conexión.' }
        return
    }
    if (-not $supportsAuth) { throw 'La conexión TLS se completó, pero el servidor no anuncia SMTP AUTH. La configuración sigue intacta.' }
    $securePassword = Read-Host "Contraseña SMTP de $Account (no se mostrará)" -AsSecureString
    if ($securePassword.Length -eq 0) { throw 'La contraseña no puede estar vacía.' }
    $credential = [Net.NetworkCredential]::new($Account, $securePassword)
    try {
        $smtp.Authenticate($credential)
        $authenticated = $true
    } catch {
        throw (Get-SmtpFailureMessage -Exception $_.Exception -Stage 'autenticación SMTP')
    }
    'PASS: conexión TLS y autenticación SMTP comprobadas. No se envió ningún correo.'
} finally {
    if ($null -ne $securePassword -and (-not $authenticated -or $CheckOnly)) {
        $credential = $null
        $securePassword.Dispose()
    }
    if ($smtp.IsConnected) { try { $smtp.Disconnect($true) } catch { } }
    $smtp.Dispose()
}
if ($CheckOnly) { return }

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
$temporaryCreated = $false
$replaceAttempted = $false
$written = $false
$stage = 'preparar archivos protegidos'
try {
    $config.Alerts.Enabled = $true
    $config.Alerts.SmtpHost = $SmtpHost
    $config.Alerts.SmtpPort = $SmtpPort
    $config.Alerts.SmtpUseTls = $true
    $config.Alerts.SmtpUser = $Account
    $config.Alerts.SmtpPassword = $credential.Password
    $config.Alerts.EmailFrom = $Account
    $config.Alerts.EmailTo = $Recipient
    New-Item -ItemType File -Path $temporary -ErrorAction Stop | Out-Null
    $temporaryCreated = $true
    Set-Acl -LiteralPath $temporary -AclObject $acl
    New-Item -ItemType File -Path $backup -ErrorAction Stop | Out-Null
    Set-Acl -LiteralPath $backup -AclObject $acl
    [IO.File]::WriteAllBytes($backup, $originalBytes)
    [IO.File]::WriteAllText($temporary, ($config | ConvertTo-Json -Depth 64), [Text.UTF8Encoding]::new($false))
    $stage = 'reemplazar la configuración activa'
    if (-not [Collections.StructuralComparisons]::StructuralEqualityComparer.Equals([IO.File]::ReadAllBytes($configPath), $originalBytes)) {
        throw 'La configuración cambió durante la verificación SMTP.'
    }
    $replaceAttempted = $true
    Set-Acl -LiteralPath $configPath -AclObject $acl
    # Ya se escribió el respaldo. NullString evita que PowerShell pase una ruta vacía a .NET.
    [IO.File]::Replace($temporary, $configPath, [NullString]::Value)
    $written = $true
    Set-Acl -LiteralPath $configPath -AclObject $acl
    $stage = 'reiniciar el servicio'
    Restart-Service -Name $ServiceName -ErrorAction Stop
    $stage = 'comprobar la disponibilidad del servicio'
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
    $cause = $_.Exception.GetBaseException()
    $failure = '{0} (0x{1:X8})' -f $cause.GetType().Name, $cause.HResult
    $recovery = 'La operación se detuvo antes de reemplazar el archivo activo.'
    if ($replaceAttempted) {
        try {
            $originalIsActive = (Test-Path -LiteralPath $configPath -PathType Leaf) -and
                [Collections.StructuralComparisons]::StructuralEqualityComparer.Equals([IO.File]::ReadAllBytes($configPath), $originalBytes)
            if (-not $originalIsActive) {
                # Windows puede dejar el destino ausente si falla un reemplazo. Restaure desde un temporal protegido.
                if (-not (Test-Path -LiteralPath $temporary -PathType Leaf)) {
                    New-Item -ItemType File -Path $temporary -ErrorAction Stop | Out-Null
                }
                Set-Acl -LiteralPath $temporary -AclObject $acl
                [IO.File]::WriteAllBytes($temporary, $originalBytes)
                [IO.File]::Move($temporary, $configPath, $true)
            }
            Set-Acl -LiteralPath $configPath -AclObject $originalAcl
            if ($written -or -not $originalIsActive) { Restart-Service -Name $ServiceName -ErrorAction Stop }
            $recovery = "El archivo de configuración anterior quedó restaurado. Respaldo protegido: $backup."
        } catch {
            $restoreCause = $_.Exception.GetBaseException()
            $restoreFailure = '{0} (0x{1:X8})' -f $restoreCause.GetType().Name, $restoreCause.HResult
            $recovery = "No se pudo completar la recuperación ($restoreFailure). Respaldo protegido: $backup."
        }
    }
    throw "Falló la activación SMTP en la etapa de $stage. Error: $failure. $recovery"
} finally {
    $config.Alerts.SmtpPassword = $null
    $credential = $null
    $securePassword.Dispose()
    # Solo el temporal exacto creado por esta ejecución; nunca borrado recursivo.
    if ($temporaryCreated -and (Test-Path -LiteralPath $temporary -PathType Leaf)) {
        try { Remove-Item -LiteralPath $temporary -Force } catch {
            Write-Warning "Quedó un archivo temporal protegido que no se pudo eliminar: $temporary."
        }
    }
}
