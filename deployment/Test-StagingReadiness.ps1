[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ConfigurationPath,

    [ValidateSet('Configuration', 'Infrastructure', 'PostMigration', 'PostDeploy')]
    [string]$Phase = 'Infrastructure',

    [string]$ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Results = [Collections.Generic.List[object]]::new()
$script:Configuration = $null
$script:EnvironmentName = ''
$script:FileXferSql = $null
$script:BudiSql = $null

function Add-Result {
    param(
        [Parameter(Mandatory)][string]$Check,
        [Parameter(Mandatory)][ValidateSet('PASS', 'WARN', 'FAIL')][string]$Status,
        [Parameter(Mandatory)][string]$Message
    )

    $script:Results.Add([pscustomobject][ordered]@{
        Check = $Check
        Status = $Status
        Message = $Message
    })
}

function Get-RequiredProperty {
    param([object]$Object, [string]$Name, [string]$Context)

    if ($null -eq $Object) {
        throw "Falta la sección '$Context'."
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        throw "Falta '$Context.$Name'."
    }

    if ($property.Value -is [string] -and [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        throw "'$Context.$Name' no puede estar vacío."
    }

    return $property.Value
}

function Assert-OnlyProperties {
    param([object]$Object, [string]$Context, [string[]]$AllowedProperties)

    if ($null -eq $Object) { throw "Falta la sección '$Context'." }
    $unexpected = @($Object.PSObject.Properties.Name | Where-Object { $_ -notin $AllowedProperties })
    if ($unexpected.Count -gt 0) {
        throw "El perfil contiene $($unexpected.Count) propiedad(es) no admitida(s) en '$Context'. No se permiten campos adicionales ni secretos embebidos."
    }
}

function Test-IpLiteral {
    param([string]$Value)

    $address = $null
    return [Net.IPAddress]::TryParse($Value, [ref]$address)
}

function ConvertTo-SafeMessage {
    param([Exception]$Exception)

    $message = $Exception.Message
    $message = [regex]::Replace(
        $message,
        '(?i)(password|pwd|secret|token)\s*=\s*[^;,\s]+',
        '$1=<redacted>')
    if ($message.Length -gt 500) { $message = $message.Substring(0, 500) }
    return $message
}

function Test-Placeholder {
    param([string]$Value)

    return [string]::IsNullOrWhiteSpace($Value) -or
        $Value -match '(?i)(\.invalid$|\.example$|\.example\.|(^|\.)(localhost|localdomain)$|change[-_ ]?me|reemplazar|servidor|example\.com$)'
}

function Test-EmailAddress {
    param([string]$Address)

    try {
        $parsed = [Net.Mail.MailAddress]::new($Address)
        return $parsed.Address -eq $Address
    }
    catch {
        return $false
    }
}

function Test-DnsResolution {
    param([string]$Check, [string]$HostName)

    try {
        $addresses = @([Net.Dns]::GetHostAddresses($HostName))
        if ($addresses.Count -eq 0) { throw 'DNS no devolvió direcciones.' }
        if ($script:EnvironmentName -ne 'Development' -and
            @($addresses | Where-Object { [Net.IPAddress]::IsLoopback($_) }).Count -gt 0) {
            throw 'DNS resolvió a una dirección loopback fuera de Development.'
        }
        Add-Result $Check PASS "DNS resolvió $HostName."
        return $true
    }
    catch {
        Add-Result $Check FAIL "No fue posible resolver $HostName."
        return $false
    }
}

function Open-TcpConnection {
    param([string]$HostName, [int]$Port, [int]$TimeoutMilliseconds = 10000)

    $client = [Net.Sockets.TcpClient]::new()
    try {
        $connect = $client.ConnectAsync($HostName, $Port)
        if (-not $connect.Wait($TimeoutMilliseconds)) {
            throw 'Tiempo de conexión agotado.'
        }
        if ($connect.IsFaulted) {
            throw $connect.Exception.GetBaseException()
        }
        $client.ReceiveTimeout = $TimeoutMilliseconds
        $client.SendTimeout = $TimeoutMilliseconds
        return $client
    }
    catch {
        $client.Dispose()
        throw
    }
}

function Read-SmtpResponse {
    param([IO.StreamReader]$Reader)

    $lines = [Collections.Generic.List[string]]::new()
    while ($true) {
        $line = $Reader.ReadLine()
        if ($null -eq $line) { throw 'El servidor SMTP cerró la conexión.' }
        $lines.Add($line)
        if ($line.Length -lt 4 -or $line[3] -ne '-') { break }
        if ($lines.Count -gt 100) { throw 'Respuesta SMTP excesivamente larga.' }
    }

    $status = 0
    if ($lines[0].Length -lt 3 -or -not [int]::TryParse($lines[0].Substring(0, 3), [ref]$status)) {
        throw 'Respuesta SMTP inválida.'
    }
    return [pscustomobject]@{ Status = $status; Lines = @($lines) }
}

function Write-SmtpCommand {
    param([IO.StreamWriter]$Writer, [string]$Command)

    $Writer.Write($Command + "`r`n")
    $Writer.Flush()
}

function Test-SmtpTls {
    param([string]$HostName, [int]$Port)

    $tcp = $null
    $network = $null
    $reader = $null
    $writer = $null
    $ssl = $null
    try {
        $tcp = Open-TcpConnection $HostName $Port
        $network = $tcp.GetStream()

        if ($Port -eq 465) {
            $ssl = [Net.Security.SslStream]::new($network, $false)
            $ssl.AuthenticateAsClient($HostName, $null, [Security.Authentication.SslProtocols]::Tls12, $true)
        }
        else {
            $reader = [IO.StreamReader]::new($network, [Text.Encoding]::ASCII, $false, 1024, $true)
            $writer = [IO.StreamWriter]::new($network, [Text.Encoding]::ASCII, 1024, $true)
            $writer.NewLine = "`r`n"

            $banner = Read-SmtpResponse $reader
            if ($banner.Status -ne 220) { throw "SMTP respondió $($banner.Status) al conectar." }

            Write-SmtpCommand $writer 'EHLO filexfer-preflight.local'
            $ehlo = Read-SmtpResponse $reader
            if ($ehlo.Status -ne 250) { throw "SMTP respondió $($ehlo.Status) a EHLO." }
            if (-not (@($ehlo.Lines) -match '(?i)(^|[ -])STARTTLS([ =]|$)')) {
                throw 'El servidor SMTP no anuncia STARTTLS.'
            }

            Write-SmtpCommand $writer 'STARTTLS'
            $startTls = Read-SmtpResponse $reader
            if ($startTls.Status -ne 220) { throw "SMTP respondió $($startTls.Status) a STARTTLS." }

            $reader.Dispose()
            $writer.Dispose()
            $reader = $null
            $writer = $null
            $ssl = [Net.Security.SslStream]::new($network, $false)
            $ssl.AuthenticateAsClient($HostName, $null, [Security.Authentication.SslProtocols]::Tls12, $true)
        }

        Add-Result 'SMTP TLS' PASS "Negociación TLS 1.2 y validación de certificado correctas con ${HostName}:$Port."
    }
    catch {
        Add-Result 'SMTP TLS' FAIL "No se pudo validar TLS con ${HostName}:$Port. $(ConvertTo-SafeMessage $_.Exception)"
    }
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
        if ($null -ne $writer) { $writer.Dispose() }
        if ($null -ne $ssl) { $ssl.Dispose() }
        elseif ($null -ne $network) { $network.Dispose() }
        if ($null -ne $tcp) { $tcp.Dispose() }
    }
}

function Resolve-SqlConfiguration {
    param([string]$Name, [object]$Node)

    $variableName = [string](Get-RequiredProperty $Node 'connectionStringEnvironmentVariable' $Name)
    if ($variableName -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
        Add-Result "$Name configuración" FAIL 'El nombre de variable de entorno SQL es inválido.'
        return $null
    }

    $connectionString = [Environment]::GetEnvironmentVariable($variableName)
    if ([string]::IsNullOrWhiteSpace($connectionString)) {
        Add-Result "$Name configuración" FAIL "No existe la variable de entorno $variableName."
        return $null
    }

    try {
        $genericBuilder = [Data.Common.DbConnectionStringBuilder]::new()
        $genericBuilder.set_ConnectionString($connectionString)
        $usesStrictEncryption = $genericBuilder.ContainsKey('Encrypt') -and
            ([string]$genericBuilder['Encrypt']).Equals('Strict', [StringComparison]::OrdinalIgnoreCase)
        $compatibleConnectionString = $genericBuilder.get_ConnectionString()
        if ($usesStrictEncryption) {
            $genericBuilder['Encrypt'] = 'True'
            $compatibleConnectionString = $genericBuilder.get_ConnectionString()
        }

        $builder = [Data.SqlClient.SqlConnectionStringBuilder]::new($compatibleConnectionString)
        $valid = $true
        if ([string]::IsNullOrWhiteSpace($builder.DataSource) -or [string]::IsNullOrWhiteSpace($builder.InitialCatalog)) {
            Add-Result "$Name configuración" FAIL 'La conexión debe declarar servidor y base de datos.'
            $valid = $false
        }
        if (-not $builder.Encrypt -and -not $usesStrictEncryption) {
            Add-Result "$Name cifrado" FAIL 'La conexión SQL debe usar Encrypt=True o Encrypt=Strict.'
            $valid = $false
        }
        if ($builder.TrustServerCertificate) {
            Add-Result "$Name certificado" FAIL 'TrustServerCertificate debe ser False.'
            $valid = $false
        }
        if (-not [string]::IsNullOrWhiteSpace($builder.AttachDBFilename)) {
            Add-Result "$Name archivo adjunto" FAIL 'AttachDBFilename no está permitido en staging o producción.'
            $valid = $false
        }
        if ($script:EnvironmentName -ne 'Development' -and $builder.DataSource -match '(?i)\(localdb\)') {
            Add-Result "$Name servidor" FAIL 'LocalDB no está permitido fuera de Development.'
            $valid = $false
        }
        if (-not $builder.IntegratedSecurity) {
            Add-Result "$Name identidad" WARN 'Se usa autenticación SQL; gMSA y autenticación integrada son la opción recomendada.'
        }
        if (-not $valid) { return $null }

        Add-Result "$Name configuración" PASS 'Conexión obtenida desde variable de entorno; cifrado y validación de certificado están exigidos.'
        return [pscustomobject]@{
            Name = $Name
            ConnectionString = $compatibleConnectionString
            Server = $builder.DataSource
            Database = $builder.InitialCatalog
        }
    }
    catch {
        Add-Result "$Name configuración" FAIL "La cadena SQL no es válida. $(ConvertTo-SafeMessage $_.Exception)"
        return $null
    }
}

function Get-MissingSqlRequirements {
    param([Data.SqlClient.SqlConnection]$Connection, [string]$Kind)

    if ($Kind -eq 'FileXferDb') {
        $query = @'
SELECT RequirementName
FROM (VALUES
    (N'Migración DurableAlertOutbox2027', CASE WHEN OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261015202505_DurableAlertOutbox2027') THEN 1 ELSE 0 END),
    (N'Tabla Uploads', CASE WHEN OBJECT_ID(N'dbo.Uploads', N'U') IS NOT NULL THEN 1 ELSE 0 END),
    (N'Tabla FileTypePolicies', CASE WHEN OBJECT_ID(N'dbo.FileTypePolicies', N'U') IS NOT NULL THEN 1 ELSE 0 END),
    (N'Tabla FileTypePolicyRevisions', CASE WHEN OBJECT_ID(N'dbo.FileTypePolicyRevisions', N'U') IS NOT NULL THEN 1 ELSE 0 END),
    (N'Tabla UploadReceipts', CASE WHEN OBJECT_ID(N'dbo.UploadReceipts', N'U') IS NOT NULL THEN 1 ELSE 0 END),
    (N'Tabla ReceiptChainStates', CASE WHEN OBJECT_ID(N'dbo.ReceiptChainStates', N'U') IS NOT NULL THEN 1 ELSE 0 END),
    (N'Tabla AlertOutboxDeliveries', CASE WHEN OBJECT_ID(N'dbo.AlertOutboxDeliveries', N'U') IS NOT NULL THEN 1 ELSE 0 END),
    (N'Columnas de reanudación', CASE WHEN COL_LENGTH(N'dbo.Uploads', N'ExpectedLength') IS NOT NULL AND COL_LENGTH(N'dbo.Uploads', N'ResumeExpiresUtc') IS NOT NULL AND COL_LENGTH(N'dbo.Uploads', N'ResumedBytes') IS NOT NULL THEN 1 ELSE 0 END),
    (N'Columnas de verificación', CASE WHEN COL_LENGTH(N'dbo.Uploads', N'VerificationRequired') IS NOT NULL AND COL_LENGTH(N'dbo.Uploads', N'VerificationPerformed') IS NOT NULL AND COL_LENGTH(N'dbo.Uploads', N'VerificationOutcome') IS NOT NULL THEN 1 ELSE 0 END),
    (N'Índice de cargas reanudables', CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Uploads') AND name = N'IX_Uploads_Status_ResumeExpiresUtc') THEN 1 ELSE 0 END),
    (N'Índice único de políticas', CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FileTypePolicies') AND name = N'IX_FileTypePolicies_Extension' AND is_unique = 1) THEN 1 ELSE 0 END),
    (N'Índice de trabajo outbox', CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AlertOutboxDeliveries') AND name = N'IX_AlertOutboxDeliveries_Status_NextAttemptUtc_LeaseExpiresUtc') THEN 1 ELSE 0 END),
    (N'Índice único de deduplicación outbox', CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AlertOutboxDeliveries') AND name = N'IX_AlertOutboxDeliveries_Transport_DeduplicationKey_DeduplicationBucket' AND is_unique = 1) THEN 1 ELSE 0 END),
    (N'Constraints de outbox', CASE WHEN (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.AlertOutboxDeliveries') AND name IN (N'CK_AlertOutboxDeliveries_Status', N'CK_AlertOutboxDeliveries_AttemptCount', N'CK_AlertOutboxDeliveries_Lease', N'CK_AlertOutboxDeliveries_DeliveredUtc')) = 4 THEN 1 ELSE 0 END)
) required(RequirementName, IsPresent)
WHERE IsPresent = 0;
'@
    }
    else {
        $query = @'
SELECT RequirementName
FROM (VALUES
    (N'Base BUZONDIGITAL', CASE WHEN DB_NAME() = N'BUZONDIGITAL' THEN 1 ELSE 0 END),
    (N'Tabla Transfer_ArchivosLocales', CASE WHEN OBJECT_ID(N'dbo.Transfer_ArchivosLocales', N'U') IS NOT NULL THEN 1 ELSE 0 END),
    (N'Columna MovimientoId int', CASE WHEN EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales') AND c.name = N'MovimientoId' AND t.name = N'int') THEN 1 ELSE 0 END),
    (N'Columna ID_Upload uniqueidentifier', CASE WHEN EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales') AND c.name = N'ID_Upload' AND t.name = N'uniqueidentifier') THEN 1 ELSE 0 END),
    (N'Índice único ID_Upload', CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales') AND name = N'UX_Transfer_ArchivosLocales_ID_Upload' AND is_unique = 1) THEN 1 ELSE 0 END),
    (N'Índice de contexto de resultado', CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Transfer_ArchivosLocales') AND name = N'IX_Transfer_ArchivosLocales_ResultadoContexto') THEN 1 ELSE 0 END)
) required(RequirementName, IsPresent)
WHERE IsPresent = 0;
'@
    }

    $command = $Connection.CreateCommand()
    $command.CommandText = $query
    $command.CommandTimeout = 15
    $reader = $command.ExecuteReader()
    try {
        $missing = [Collections.Generic.List[string]]::new()
        while ($reader.Read()) { $missing.Add($reader.GetString(0)) }
        return @($missing)
    }
    finally {
        $reader.Dispose()
        $command.Dispose()
    }
}

function Test-SqlDatabase {
    param([object]$SqlConfiguration, [string]$Kind, [bool]$ValidateSchema)

    if ($null -eq $SqlConfiguration) { return }
    $connection = $null
    try {
        $builder = [Data.SqlClient.SqlConnectionStringBuilder]::new($SqlConfiguration.ConnectionString)
        if ($builder.ConnectTimeout -eq 0 -or $builder.ConnectTimeout -gt 15) { $builder.ConnectTimeout = 15 }
        $connection = [Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
        $connection.Open()

        $command = $connection.CreateCommand()
        $command.CommandText = 'SELECT encrypt_option, auth_scheme FROM sys.dm_exec_connections WHERE session_id = @@SPID;'
        $command.CommandTimeout = 15
        $reader = $command.ExecuteReader()
        try {
            if (-not $reader.Read()) { throw 'No se obtuvo información de transporte SQL.' }
            $encrypted = $reader.GetString(0)
            $authentication = $reader.GetString(1)
        }
        finally {
            $reader.Dispose()
            $command.Dispose()
        }

        if ($encrypted -ne 'TRUE') {
            Add-Result "$Kind transporte" FAIL 'SQL aceptó una sesión sin cifrado.'
        }
        else {
            Add-Result "$Kind transporte" PASS "Conexión SQL cifrada; autenticación $authentication."
        }

        if ($ValidateSchema) {
            $missing = @(Get-MissingSqlRequirements $connection $Kind)
            if ($missing.Count -gt 0) {
                Add-Result "$Kind esquema" FAIL ('Contrato incompleto: ' + ($missing -join ', ') + '.')
            }
            else {
                Add-Result "$Kind esquema" PASS 'El contrato de tablas, columnas, índices y migraciones está completo.'
            }
        }
    }
    catch {
        if ($_.Exception -is [Data.SqlClient.SqlException]) {
            Add-Result "$Kind conectividad" FAIL "SQL rechazó la comprobación (código $($_.Exception.Number))."
        }
        else {
            Add-Result "$Kind conectividad" FAIL (ConvertTo-SafeMessage $_.Exception)
        }
    }
    finally {
        if ($null -ne $connection) { $connection.Dispose() }
    }
}

function Test-Storage {
    param([object]$Storage)

    $basePath = [string](Get-RequiredProperty $Storage 'basePath' 'storage')
    $minimumFreeSpaceGb = [double](Get-RequiredProperty $Storage 'minimumFreeSpaceGb' 'storage')
    $serviceAccount = [string](Get-RequiredProperty $Storage 'serviceAccount' 'storage')

    if (-not [IO.Path]::IsPathFullyQualified($basePath) -or (Test-Placeholder $basePath)) {
        Add-Result 'Storage configuración' FAIL 'Storage debe ser una ruta absoluta real, no un marcador.'
        return
    }
    if ($minimumFreeSpaceGb -lt 1) {
        Add-Result 'Storage capacidad mínima' FAIL 'minimumFreeSpaceGb debe ser al menos 1.'
        return
    }

    try {
        $serviceSid = [Security.Principal.NTAccount]::new($serviceAccount).Translate([Security.Principal.SecurityIdentifier])
    }
    catch {
        Add-Result 'Storage identidad' FAIL 'No fue posible resolver la cuenta de servicio a un SID de Windows.'
        return
    }

    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($null -eq $currentIdentity.User -or -not $currentIdentity.User.Equals($serviceSid)) {
        Add-Result 'Storage identidad' FAIL "Ejecute el preflight como la cuenta de servicio declarada ($serviceAccount)."
        return
    }
    Add-Result 'Storage identidad' PASS 'El preflight se ejecuta con el SID de la cuenta de servicio declarada.'

    if (-not (Test-Path -LiteralPath $basePath -PathType Container)) {
        Add-Result 'Storage existencia' FAIL 'La ruta de storage no existe o no es accesible.'
        return
    }

    try {
        $storageDirectory = [IO.DirectoryInfo]::new([IO.Path]::GetFullPath($basePath))
        if (($storageDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            Add-Result 'Storage reparse point' FAIL 'La raíz de storage no puede ser un enlace, junction ni reparse point.'
            return
        }
        Add-Result 'Storage reparse point' PASS 'La raíz de storage no es un reparse point.'

        $acl = Get-Acl -LiteralPath $storageDirectory.FullName
        $allowedSids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        [void]$allowedSids.Add($serviceSid.Value)
        [void]$allowedSids.Add('S-1-5-18')
        [void]$allowedSids.Add('S-1-5-32-544')
        [void]$allowedSids.Add('S-1-3-0')

        $dataRights = [Security.AccessControl.FileSystemRights]::Read -bor
            [Security.AccessControl.FileSystemRights]::Write -bor
            [Security.AccessControl.FileSystemRights]::Modify -bor
            [Security.AccessControl.FileSystemRights]::FullControl -bor
            [Security.AccessControl.FileSystemRights]::ReadAndExecute
        $unexpectedAccess = [Collections.Generic.List[string]]::new()
        foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) { continue }
            if (($rule.FileSystemRights -band $dataRights) -eq 0) { continue }
            $sid = ([Security.Principal.SecurityIdentifier]$rule.IdentityReference).Value
            if (-not $allowedSids.Contains($sid)) { $unexpectedAccess.Add($sid) }
        }

        $ownerSid = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
        if (-not $allowedSids.Contains($ownerSid)) {
            Add-Result 'Storage ACL' FAIL 'El propietario del storage no es la cuenta de servicio, SYSTEM ni Administradores.'
            return
        }
        if ($unexpectedAccess.Count -gt 0) {
            Add-Result 'Storage ACL' FAIL "La ACL concede acceso de datos a $($unexpectedAccess.Count) principal(es) no autorizados."
            return
        }
        Add-Result 'Storage ACL' PASS 'La ACL limita acceso de datos a la cuenta de servicio, SYSTEM, Administradores y CREATOR OWNER.'
    }
    catch {
        Add-Result 'Storage ACL' FAIL "No fue posible demostrar la ACL exclusiva. $(ConvertTo-SafeMessage $_.Exception)"
        return
    }

    $probePath = Join-Path $basePath ('.filexfer-preflight-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $stream = [IO.File]::Open($probePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        try {
            $bytes = [Text.Encoding]::UTF8.GetBytes('FILEXFER-STORAGE-PREFLIGHT')
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally {
            $stream.Dispose()
        }
        [IO.File]::Delete($probePath)
        Add-Result 'Storage lectura/escritura' PASS 'La cuenta actual puede crear, confirmar y eliminar un archivo de prueba exclusivo.'

        $item = Get-Item -LiteralPath $basePath
        if ($null -ne $item.PSDrive -and $null -ne $item.PSDrive.Free) {
            $freeGb = [math]::Round(([double]$item.PSDrive.Free / 1GB), 2)
            if ($freeGb -lt $minimumFreeSpaceGb) {
                Add-Result 'Storage espacio libre' FAIL "Hay $freeGb GB libres; se exigen $minimumFreeSpaceGb GB."
            }
            else {
                Add-Result 'Storage espacio libre' PASS "Hay $freeGb GB libres; mínimo exigido $minimumFreeSpaceGb GB."
            }
        }
        else {
            Add-Result 'Storage espacio libre' WARN 'No fue posible obtener espacio libre; valide la cuota del volumen por separado.'
        }
    }
    catch {
        if (Test-Path -LiteralPath $probePath -PathType Leaf) {
            try { [IO.File]::Delete($probePath) } catch { }
        }
        Add-Result 'Storage lectura/escritura' FAIL "La cuenta actual no pudo completar la prueba de I/O. $(ConvertTo-SafeMessage $_.Exception)"
    }
}

function Read-BoundedHttpContent {
    param(
        [Net.Http.HttpContent]$Content,
        [int]$MaximumBytes,
        [Threading.CancellationToken]$CancellationToken
    )

    $input = $null
    $output = [IO.MemoryStream]::new([Math]::Min($MaximumBytes + 1, 8192))
    try {
        $input = $Content.ReadAsStreamAsync($CancellationToken).GetAwaiter().GetResult()
        $buffer = [byte[]]::new([Math]::Min($MaximumBytes + 1, 8192))
        while ($true) {
            $remaining = ($MaximumBytes + 1) - [int]$output.Length
            if ($remaining -le 0) {
                throw [IO.InvalidDataException]::new("La respuesta excede el límite de $MaximumBytes bytes.")
            }

            $read = $input.ReadAsync(
                $buffer,
                0,
                [Math]::Min($buffer.Length, $remaining),
                $CancellationToken).GetAwaiter().GetResult()
            if ($read -eq 0) { break }

            $output.Write($buffer, 0, $read)
            if ($output.Length -gt $MaximumBytes) {
                throw [IO.InvalidDataException]::new("La respuesta excede el límite de $MaximumBytes bytes.")
            }
        }

        return $output.ToArray()
    }
    finally {
        if ($null -ne $input) { $input.Dispose() }
        $output.Dispose()
    }
}

function Test-ApplicationEndpoints {
    param([Uri]$BaseUri)

    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(15)
    try {
        foreach ($endpoint in @('healthz', 'readyz')) {
            $uri = [Uri]::new($BaseUri, $endpoint)
            try {
                $response = $client.GetAsync($uri, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
                try {
                    if ([int]$response.StatusCode -ne 200) {
                        Add-Result "HTTPS /$endpoint" FAIL "Respondió HTTP $([int]$response.StatusCode)."
                        continue
                    }
                    if ($null -ne $response.Content.Headers.ContentLength -and $response.Content.Headers.ContentLength -gt 65536) {
                        Add-Result "HTTPS /$endpoint" FAIL 'La respuesta excede el límite de 64 KiB.'
                        continue
                    }

                    $contentCancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(15))
                    try {
                        $bytes = Read-BoundedHttpContent `
                            -Content $response.Content `
                            -MaximumBytes 65536 `
                            -CancellationToken $contentCancellation.Token
                    }
                    finally {
                        $contentCancellation.Dispose()
                    }
                    try {
                        $payload = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
                        $expectedStatus = if ($endpoint -eq 'healthz') { 'ok' } else { 'ready' }
                        $actualStatus = [string](Get-RequiredProperty $payload 'status' "/$endpoint")
                        if (-not $actualStatus.Equals($expectedStatus, [StringComparison]::Ordinal)) {
                            Add-Result "HTTPS /$endpoint" FAIL "El contrato respondió status='$actualStatus'; se esperaba '$expectedStatus'."
                        }
                        else {
                            Add-Result "HTTPS /$endpoint" PASS "HTTP 200, TLS válido y contrato status='$expectedStatus'."
                        }
                    }
                    catch {
                        Add-Result "HTTPS /$endpoint" FAIL 'La respuesta no contiene el contrato JSON esperado.'
                    }
                }
                finally {
                    $response.Dispose()
                }
            }
            catch {
                Add-Result "HTTPS /$endpoint" FAIL "No fue posible validar el endpoint. $(ConvertTo-SafeMessage $_.Exception)"
            }
        }
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }
}

try {
    $ConfigurationPath = [IO.Path]::GetFullPath($ConfigurationPath)
    if (-not (Test-Path -LiteralPath $ConfigurationPath -PathType Leaf)) {
        throw 'No existe el archivo de configuración del preflight.'
    }

    $rawConfiguration = [IO.File]::ReadAllText($ConfigurationPath)
    $script:Configuration = $rawConfiguration | ConvertFrom-Json
    Assert-OnlyProperties $script:Configuration 'root' @('environment', 'serverBaseUrl', 'allowedHosts', 'fileXferDb', 'budiDev', 'storage', 'alerts')
    $fileXferNode = Get-RequiredProperty $script:Configuration 'fileXferDb' 'root'
    $budiNode = Get-RequiredProperty $script:Configuration 'budiDev' 'root'
    $storage = Get-RequiredProperty $script:Configuration 'storage' 'root'
    $alerts = Get-RequiredProperty $script:Configuration 'alerts' 'root'
    Assert-OnlyProperties $fileXferNode 'fileXferDb' @('connectionStringEnvironmentVariable')
    Assert-OnlyProperties $budiNode 'budiDev' @('connectionStringEnvironmentVariable')
    Assert-OnlyProperties $storage 'storage' @('basePath', 'minimumFreeSpaceGb', 'serviceAccount')
    Assert-OnlyProperties $alerts 'alerts' @('enabled', 'smtpHost', 'smtpPort', 'useTls', 'emailFrom', 'emailTo')
    Add-Result 'Secretos en perfil' PASS 'El perfil usa un esquema cerrado y no contiene propiedades adicionales o sensibles.'

    $script:EnvironmentName = [string](Get-RequiredProperty $script:Configuration 'environment' 'root')
    if ($script:EnvironmentName -notin @('Development', 'Staging', 'Production')) {
        throw "environment debe ser Development, Staging o Production."
    }
    Add-Result 'Entorno' PASS "Perfil declarado para $script:EnvironmentName."

    $baseUrlText = [string](Get-RequiredProperty $script:Configuration 'serverBaseUrl' 'root')
    $baseUri = $null
    if (-not [Uri]::TryCreate($baseUrlText, [UriKind]::Absolute, [ref]$baseUri) -or
        $baseUri.Scheme -ne [Uri]::UriSchemeHttps -or
        -not [string]::IsNullOrEmpty($baseUri.Query) -or
        -not [string]::IsNullOrEmpty($baseUri.Fragment) -or
        $baseUri.AbsolutePath -ne '/' -or
        (Test-IpLiteral $baseUri.Host) -or
        (Test-Placeholder $baseUri.Host)) {
        Add-Result 'URL del servidor' FAIL 'serverBaseUrl debe ser un origen HTTPS con nombre DNS, sin ruta, query ni fragmento.'
        $baseUri = $null
    }
    else {
        Add-Result 'URL del servidor' PASS "HTTPS configurado para $($baseUri.Host)."
    }

    $allowedHosts = @((Get-RequiredProperty $script:Configuration 'allowedHosts' 'root'))
    $hostsValid = $allowedHosts.Count -gt 0
    foreach ($allowedHost in $allowedHosts) {
        if ([string]$allowedHost -eq '*' -or [string]$allowedHost -match '[:/]' -or
            (Test-IpLiteral ([string]$allowedHost)) -or (Test-Placeholder ([string]$allowedHost))) {
            $hostsValid = $false
        }
    }
    if ($null -ne $baseUri -and $baseUri.IsAbsoluteUri -and $baseUri.Host -notin $allowedHosts) { $hostsValid = $false }
    if ($hostsValid) {
        Add-Result 'AllowedHosts' PASS 'La lista es explícita e incluye el host público.'
    }
    else {
        Add-Result 'AllowedHosts' FAIL 'Use hosts DNS explícitos, sin comodín, esquema, puerto ni marcadores.'
    }

    $script:FileXferSql = Resolve-SqlConfiguration 'FileXferDb' $fileXferNode
    $script:BudiSql = Resolve-SqlConfiguration 'BudiDev' $budiNode

    $storagePath = [string](Get-RequiredProperty $storage 'basePath' 'storage')
    $minimumFree = [double](Get-RequiredProperty $storage 'minimumFreeSpaceGb' 'storage')
    $serviceAccount = [string](Get-RequiredProperty $storage 'serviceAccount' 'storage')
    if ([IO.Path]::IsPathFullyQualified($storagePath) -and -not (Test-Placeholder $storagePath) -and $minimumFree -ge 1 -and -not (Test-Placeholder $serviceAccount)) {
        Add-Result 'Storage configuración' PASS 'Ruta absoluta, umbral de capacidad y cuenta de servicio declarados.'
    }
    else {
        Add-Result 'Storage configuración' FAIL 'La configuración de storage contiene valores incompletos o marcadores.'
    }

    $alertsEnabled = [bool](Get-RequiredProperty $alerts 'enabled' 'alerts')
    $smtpHost = [string](Get-RequiredProperty $alerts 'smtpHost' 'alerts')
    $smtpPort = [int](Get-RequiredProperty $alerts 'smtpPort' 'alerts')
    $smtpUseTls = [bool](Get-RequiredProperty $alerts 'useTls' 'alerts')
    $emailFrom = [string](Get-RequiredProperty $alerts 'emailFrom' 'alerts')
    $emailTo = @((Get-RequiredProperty $alerts 'emailTo' 'alerts'))
    $smtpValid = $alertsEnabled -and $smtpUseTls -and $smtpPort -ge 1 -and $smtpPort -le 65535 -and
        -not (Test-IpLiteral $smtpHost) -and -not (Test-Placeholder $smtpHost) -and
        (Test-EmailAddress $emailFrom) -and $emailTo.Count -gt 0
    foreach ($recipient in $emailTo) {
        if (-not (Test-EmailAddress ([string]$recipient))) { $smtpValid = $false }
    }
    if ($smtpValid) {
        Add-Result 'SMTP configuración' PASS 'Canal SMTP habilitado con TLS, host, puerto y direcciones válidas.'
    }
    else {
        Add-Result 'SMTP configuración' FAIL 'SMTP debe estar habilitado, usar TLS y declarar host, puerto y correos válidos.'
    }

    if ($Phase -ne 'Configuration') {
        if ($null -ne $baseUri -and $baseUri.IsAbsoluteUri) { [void](Test-DnsResolution 'DNS API' $baseUri.Host) }
        [void](Test-DnsResolution 'DNS SMTP' $smtpHost)

        $validateSchema = $Phase -in @('PostMigration', 'PostDeploy')
        Test-SqlDatabase $script:FileXferSql 'FileXferDb' $validateSchema
        Test-SqlDatabase $script:BudiSql 'BudiDev' $validateSchema
        Test-Storage $storage
        if ($smtpValid) { Test-SmtpTls $smtpHost $smtpPort }
    }

    if ($Phase -eq 'PostDeploy' -and $null -ne $baseUri -and $baseUri.IsAbsoluteUri) {
        Test-ApplicationEndpoints $baseUri
    }
}
catch {
    Add-Result 'Preflight' FAIL (ConvertTo-SafeMessage $_.Exception)
}

$passCount = @($script:Results | Where-Object Status -eq 'PASS').Count
$warningCount = @($script:Results | Where-Object Status -eq 'WARN').Count
$failureCount = @($script:Results | Where-Object Status -eq 'FAIL').Count
$report = [ordered]@{
    SchemaVersion = 1
    Product = 'Transfer ASE 2027'
    Phase = $Phase
    Environment = $script:EnvironmentName
    CreatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    Machine = [Environment]::MachineName
    Identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    ConfigurationSha256 = if (Test-Path -LiteralPath $ConfigurationPath -PathType Leaf) { (Get-FileHash -LiteralPath $ConfigurationPath -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
    Summary = [ordered]@{
        Passed = $passCount
        Warnings = $warningCount
        Failed = $failureCount
        Ready = $failureCount -eq 0
    }
    Results = @($script:Results)
}

$script:Results | Format-Table -AutoSize | Out-Host
Write-Host "Resultado: PASS=$passCount WARN=$warningCount FAIL=$failureCount"

if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = [IO.Path]::GetFullPath($ReportPath)
    $reportDirectory = Split-Path -Parent $ReportPath
    if (-not [string]::IsNullOrWhiteSpace($reportDirectory)) {
        [IO.Directory]::CreateDirectory($reportDirectory) | Out-Null
    }
    [IO.File]::WriteAllText(
        $ReportPath,
        (($report | ConvertTo-Json -Depth 8) + "`n"),
        [Text.UTF8Encoding]::new($false))
    Write-Host "Reporte: $ReportPath"
}

if ($failureCount -gt 0) { exit 1 }
exit 0
