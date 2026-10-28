#requires -Version 7.0

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

    [string]$ExpectedBudiDatabase = 'Budi_Dev',

    [string]$ExpectedBudiServer = '192.168.2.8',

    [string]$ExpectedFileXferDatabase = 'FileXferDev',

    [string]$ExpectedFileXferServer = '192.168.2.3\SQL2014',

    [string]$ExpectedStorageBasePath = '\\192.168.2.21\DocumentosBuzonDigital_Pruebas\Transfer\Finalizados',

    [string[]]$SatTrustedRootCertificatePaths = @(),

    [string[]]$SatIntermediateCertificatePaths = @(),

    [string[]]$SatAllowedRevocationHosts = @(),

    [string]$TlsCertificateSubject,

    [string]$ReceiptPrivateKeyPath = 'C:\ProgramData\ASE\FileXfer\secrets\receipt-signing-private.pem',

    [switch]$GeneratePathEncryptionKeyForEmptyDatabase,

    [switch]$AllowLegacyDevelopmentSqlTransport,

    [switch]$EnableClientUpdates,

    [switch]$ReplaceExistingService,

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
    try {
        $fileXferSql = [Data.SqlClient.SqlConnectionStringBuilder]::new($fileXferConnection)
        $budiSql = [Data.SqlClient.SqlConnectionStringBuilder]::new($budiConnection)
    }
    catch {
        throw 'Las cadenas SQL de la configuración no tienen un formato válido.'
    }
    if (-not $fileXferSql.InitialCatalog.Equals($ExpectedFileXferDatabase, [StringComparison]::OrdinalIgnoreCase) -or
        -not $budiSql.InitialCatalog.Equals($ExpectedBudiDatabase, [StringComparison]::OrdinalIgnoreCase) -or
        -not $fileXferSql.DataSource.Equals($ExpectedFileXferServer, [StringComparison]::OrdinalIgnoreCase) -or
        -not $budiSql.DataSource.Equals($ExpectedBudiServer, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Las conexiones SQL no coinciden con los servidores y bases del entorno de desarrollo. Proporcione -ProductionConfigPath correcto antes de tocar el servicio.'
    }
    $storagePath = [string](Get-Setting $config @('Storage', 'BasePath') 'Storage__BasePath')
    if (-not $storagePath.TrimEnd('\', '/').Equals(
        $ExpectedStorageBasePath.TrimEnd('\', '/'),
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Storage:BasePath no coincide con la ruta esperada del entorno de desarrollo. Proporcione -ProductionConfigPath y revise -ExpectedStorageBasePath."
    }
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

    $legacyPathKey = [string](Get-Setting $config @('Security', 'LegacyPathEncryptionKey') 'Security__LegacyPathEncryptionKey')
    if ([string]::IsNullOrWhiteSpace($legacyPathKey)) {
        throw 'Falta Security:LegacyPathEncryptionKey. La instalación exige la llave compartida con BuDi para leer y escribir rutas.'
    }
    elseif ($legacyPathKey.Length -lt 8) {
        throw 'Security:LegacyPathEncryptionKey debe contener al menos 8 caracteres.'
    }

    $pathWriteFormat = [string](Get-Setting $config @('Security', 'PathWriteFormat') 'Security__PathWriteFormat')
    if ([string]::IsNullOrWhiteSpace($pathWriteFormat)) {
        $pathWriteFormat = 'LegacyDes'
    }
    if ($pathWriteFormat -ne 'LegacyDes') {
        throw 'Security:PathWriteFormat debe ser LegacyDes para conservar el formato de rutas compartido con BuDi.'
    }

    $pathKey = [string](Get-Setting $config @('Security', 'PathEncryptionKey') 'Security__PathEncryptionKey')
    if (-not [string]::IsNullOrWhiteSpace($pathKey)) {
        try { $pathKeyBytes = [Convert]::FromBase64String($pathKey) }
        catch { throw 'Security:PathEncryptionKey no contiene Base64 válido.' }
        if ($pathKeyBytes.Length -ne 32) { throw 'Security:PathEncryptionKey debe decodificar exactamente 32 bytes.' }
    }

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
    $intermediates = @(@(Get-NestedValue $config @('Security', 'SAT', 'IntermediateCertificatePaths')) |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    $revocationHosts = @(@(Get-NestedValue $config @('Security', 'SAT', 'AllowedRevocationHosts')) |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    if ($trustedRoots.Count -eq 0) {
        throw 'Faltan raíces SAT confiables para validar localmente la e.firma.'
    }
    $requireOcsp = [bool](Get-NestedValue $config @('Security', 'SAT', 'RequireOcsp'))
    if ($requireOcsp -and $revocationHosts.Count -eq 0) {
        throw 'RequireOcsp=true exige declarar hosts OCSP/CRL permitidos.'
    }
    $configDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($Path))
    foreach ($certificatePath in @($trustedRoots + $intermediates)) {
        $resolvedSatCertificatePath = if ([IO.Path]::IsPathRooted([string]$certificatePath)) {
            [string]$certificatePath
        } else {
            Join-Path $configDirectory ([string]$certificatePath)
        }
        if (-not (Test-Path -LiteralPath $resolvedSatCertificatePath -PathType Leaf)) {
            throw "No existe la autoridad SAT configurada: $resolvedSatCertificatePath"
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
    elseif ($certificateSubject -match '^\s*CN\s*=') {
        throw 'Kestrel:Endpoints:Https:Certificate:Subject debe contener el nombre simple sin el prefijo CN=.'
    }
    else {
        $certificateStore = [string](Get-Setting $config @('Kestrel', 'Endpoints', 'Https', 'Certificate', 'Store') 'Kestrel__Endpoints__Https__Certificate__Store')
        $certificateLocation = [string](Get-Setting $config @('Kestrel', 'Endpoints', 'Https', 'Certificate', 'Location') 'Kestrel__Endpoints__Https__Certificate__Location')
        if ([string]::IsNullOrWhiteSpace($certificateStore)) { $certificateStore = 'My' }
        if ([string]::IsNullOrWhiteSpace($certificateLocation)) { $certificateLocation = 'LocalMachine' }
        $store = $null
        try {
            $storeLocation = [Security.Cryptography.X509Certificates.StoreLocation]([Enum]::Parse(
                [Security.Cryptography.X509Certificates.StoreLocation],
                $certificateLocation,
                $true))
            $store = [Security.Cryptography.X509Certificates.X509Store]::new($certificateStore, $storeLocation)
            try {
                $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
                $matchingCertificates = $store.Certificates.Find(
                    [Security.Cryptography.X509Certificates.X509FindType]::FindBySubjectName,
                    $certificateSubject,
                    $true) | Where-Object HasPrivateKey
                if (@($matchingCertificates).Count -eq 0) {
                    throw "No se encontró un certificado TLS vigente con llave privada para '$certificateSubject' en $certificateLocation/$certificateStore."
                }
            }
            finally {
                if ($null -ne $store) { $store.Close() }
            }
        }
        catch {
            throw "La validación previa del certificado TLS falló: $($_.Exception.Message)"
        }
    }
}

function Assert-PathKeysMatchCatalog {
    param([pscustomobject]$Configuration)

    $connectionString = [string](Get-Setting $Configuration @('ConnectionStrings', 'BudiDev') 'ConnectionStrings__BudiDev')
    $encodedAesKey = [string](Get-Setting $Configuration @('Security', 'PathEncryptionKey') 'Security__PathEncryptionKey')
    $legacyKey = [string](Get-Setting $Configuration @('Security', 'LegacyPathEncryptionKey') 'Security__LegacyPathEncryptionKey')
    $aesKey = if ([string]::IsNullOrWhiteSpace($encodedAesKey)) {
        $null
    } else {
        [Convert]::FromBase64String($encodedAesKey)
    }
    $legacyBytes = [Text.Encoding]::UTF8.GetBytes($legacyKey.Substring(0, 8))
    if ($legacyBytes.Length -ne 8) {
        throw 'Los primeros ocho caracteres de Security:LegacyPathEncryptionKey deben ocupar ocho bytes UTF-8 para DES.'
    }

    $connection = [Data.SqlClient.SqlConnection]::new($connectionString)
    try {
        $connection.Open()
        $v2Command = $connection.CreateCommand()
        $v2Command.CommandTimeout = 15
        $v2Command.CommandText = @'
SELECT [Ruta_ARchivo_Fisico]
FROM [dbo].[Transfer_ArchivosLocales]
WHERE [Ruta_ARchivo_Fisico] LIKE 'v2:%';
'@
        $v2Count = 0
        $v2Reader = $v2Command.ExecuteReader()
        try {
            while ($v2Reader.Read()) {
                if ($null -eq $aesKey) {
                    throw 'El catálogo todavía contiene rutas AES v2 y exige Security:PathEncryptionKey.'
                }
                $protectedPath = $v2Reader.GetString(0)
                $payload = [Convert]::FromBase64String($protectedPath.Substring(3))
                if ($payload.Length -le 28) {
                    throw 'Una ruta AES v2 del catálogo está incompleta.'
                }
                $nonce = [byte[]]::new(12)
                $tag = [byte[]]::new(16)
                $ciphertext = [byte[]]::new($payload.Length - 28)
                [Array]::Copy($payload, 0, $nonce, 0, 12)
                [Array]::Copy($payload, 12, $tag, 0, 16)
                [Array]::Copy($payload, 28, $ciphertext, 0, $ciphertext.Length)
                $plaintext = [byte[]]::new($ciphertext.Length)
                $aes = [Security.Cryptography.AesGcm]::new($aesKey, 16)
                try {
                    $aes.Decrypt(
                        $nonce,
                        $ciphertext,
                        $tag,
                        $plaintext,
                        [Text.Encoding]::UTF8.GetBytes('FileXfer.Path.v2'))
                    if ([string]::IsNullOrWhiteSpace([Text.Encoding]::UTF8.GetString($plaintext))) {
                        throw 'Una ruta AES v2 descifrada está vacía.'
                    }
                }
                finally {
                    $aes.Dispose()
                    [Security.Cryptography.CryptographicOperations]::ZeroMemory($plaintext)
                }
                $v2Count++
            }
        }
        finally { $v2Reader.Dispose() }

        $legacyCommand = $connection.CreateCommand()
        $legacyCommand.CommandTimeout = 15
        $legacyCommand.CommandText = @'
SELECT TOP (10) [Ruta_ARchivo_Fisico]
FROM [dbo].[Transfer_ArchivosLocales]
WHERE NULLIF(LTRIM(RTRIM([Ruta_ARchivo_Fisico])), '') IS NOT NULL
  AND [Ruta_ARchivo_Fisico] NOT LIKE 'v2:%'
ORDER BY [Id] DESC;
'@
        $legacyCount = 0
        $legacyReader = $legacyCommand.ExecuteReader()
        try {
            while ($legacyReader.Read()) {
                $ciphertext = [Convert]::FromBase64String($legacyReader.GetString(0))
                $des = [Security.Cryptography.DES]::Create()
                try {
                    $des.Mode = [Security.Cryptography.CipherMode]::CBC
                    $des.Padding = [Security.Cryptography.PaddingMode]::PKCS7
                    $iv = [byte[]](0x12, 0x24, 0x56, 0x78, 0x90, 0xAB, 0xCD, 0xEF)
                    $decryptor = $des.CreateDecryptor($legacyBytes, $iv)
                    try {
                        $plaintext = $decryptor.TransformFinalBlock($ciphertext, 0, $ciphertext.Length)
                        if ([string]::IsNullOrWhiteSpace([Text.Encoding]::UTF8.GetString($plaintext))) {
                            throw 'Una ruta DES descifrada está vacía.'
                        }
                    }
                    finally { $decryptor.Dispose() }
                }
                finally { $des.Dispose() }
                $legacyCount++
            }
        }
        finally { $legacyReader.Dispose() }

        if ($v2Count -eq 0 -and $legacyCount -eq 0) {
            throw 'El catálogo no tiene rutas con las que verificar las llaves. Detenga el despliegue y revise el destino SQL.'
        }
        Write-Host "PASS: llaves verificadas contra $v2Count rutas AES v2 y $legacyCount rutas DES recientes."
    }
    catch {
        throw 'No se pudieron verificar las llaves contra las rutas de Budi_Dev. La llave AES solo es opcional cuando no queda ninguna ruta v2; la llave DES compartida siempre es obligatoria.'
    }
    finally {
        $connection.Dispose()
        if ($null -ne $aesKey) {
            [Security.Cryptography.CryptographicOperations]::ZeroMemory($aesKey)
        }
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($legacyBytes)
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

function Wait-EndpointReady {
    param([Uri]$Uri, [int]$TimeoutSeconds, [string]$Description)

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
    throw "El servicio no superó $Description en $Uri. Último resultado: $lastError"
}

function Set-ServiceProductionEnvironment {
    param([string]$Name)

    $registryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
    if (-not (Test-Path -LiteralPath $registryPath)) {
        throw "No existe el registro del servicio $Name."
    }

    New-ItemProperty `
        -LiteralPath $registryPath `
        -Name 'Environment' `
        -PropertyType MultiString `
        -Value ([string[]]@(
            'DOTNET_ENVIRONMENT=Production',
            'ASPNETCORE_ENVIRONMENT=Production')) `
        -Force | Out-Null
}

function Restore-ServiceEnvironment {
    param(
        [string]$Name,
        [bool]$Existed,
        [string[]]$Value)

    $registryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
    if (-not (Test-Path -LiteralPath $registryPath)) { return }

    if ($Existed) {
        New-ItemProperty `
            -LiteralPath $registryPath `
            -Name 'Environment' `
            -PropertyType MultiString `
            -Value $Value `
            -Force | Out-Null
    }
    else {
        Remove-ItemProperty -LiteralPath $registryPath -Name 'Environment' -ErrorAction SilentlyContinue
    }
}

Assert-Administrator
$package = [IO.Path]::GetFullPath($ServerPackagePath)
$install = Assert-SafeInstallPath $InstallDirectory
Assert-Package $package $ExpectedSha256
if ($install.Equals('C:\Servicios\FileXfer', [StringComparison]::OrdinalIgnoreCase) -and
    -not $ServiceName.Equals('FileXferService', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Un servicio paralelo no puede instalarse en C:\Servicios\FileXfer, que corresponde a FileXferService.'
}

$parent = Split-Path -Parent $install
$leaf = Split-Path -Leaf $install
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$stage = Join-Path $parent ($leaf + '.staging.' + [Guid]::NewGuid().ToString('N'))
$backup = Join-Path $parent ($leaf + '.backup.' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$failed = Join-Path $parent ($leaf + '.failed.' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$hadPreviousInstall = Test-Path -LiteralPath $install -PathType Container
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$escapedServiceName = $ServiceName.Replace("'", "''")
$serviceMetadata = if ($null -ne $service) {
    Get-CimInstance -ClassName Win32_Service -Filter "Name='$escapedServiceName'" -ErrorAction Stop
} else {
    $null
}
if ($null -ne $serviceMetadata -and
    -not ([string]$serviceMetadata.StartName).Equals($ServiceAccount, [StringComparison]::OrdinalIgnoreCase)) {
    throw "El servicio $ServiceName usa la cuenta '$($serviceMetadata.StartName)', no '$ServiceAccount'. Para evitar perder la contraseña o los permisos, conserve la cuenta actual durante este despliegue."
}
if ($null -eq $service) {
    $supportsPasswordlessCreation =
        $ServiceAccount.StartsWith('NT AUTHORITY\', [StringComparison]::OrdinalIgnoreCase) -or
        $ServiceAccount.StartsWith('NT SERVICE\', [StringComparison]::OrdinalIgnoreCase) -or
        $ServiceAccount.Equals('LocalSystem', [StringComparison]::OrdinalIgnoreCase) -or
        $ServiceAccount.EndsWith('$', [StringComparison]::Ordinal)
    if (-not $supportsPasswordlessCreation) {
        throw 'La creación de un servicio nuevo con una cuenta de usuario exige administrar su contraseña fuera de este instalador. Cree el servicio previamente o utilice una gMSA/cuenta integrada. Las actualizaciones conservan la identidad existente.'
    }
}
if ($null -ne $service -and -not $ReplaceExistingService) {
    throw "El servicio $ServiceName ya existe. Use un nombre/directorio/puerto independientes para desarrollo o indique -ReplaceExistingService cuando el reemplazo esté decidido."
}
$createdService = $false
$newInstallActivated = $false
$previousServiceWasRunning = $null -ne $service -and $service.Status -ne 'Stopped'
$serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$previousServiceEnvironmentExisted = $false
$previousServiceEnvironment = [string[]]@()
if (Test-Path -LiteralPath $serviceRegistryPath) {
    $serviceRegistryProperties = Get-ItemProperty -LiteralPath $serviceRegistryPath
    $previousServiceEnvironmentExisted = $serviceRegistryProperties.PSObject.Properties.Name -contains 'Environment'
    if ($previousServiceEnvironmentExisted) {
        $previousServiceEnvironment = [string[]]$serviceRegistryProperties.Environment
    }
}

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
    Assert-PathKeysMatchCatalog (Get-Content -LiteralPath $stagedConfig -Raw | ConvertFrom-Json -ErrorAction Stop)

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
        Invoke-ServiceControl @(
            'create', $ServiceName,
            'binPath=', $binaryPath,
            'start=', 'auto',
            'DisplayName=', $ServiceDisplayName,
            'obj=', $ServiceAccount)
        $createdService = $true
    }
    else {
        # Conservar la identidad y la contraseña administradas por Windows. Volver a enviar
        # obj= sin password= puede invalidar el inicio de sesión de una cuenta de dominio.
        Invoke-ServiceControl @(
            'config', $ServiceName,
            'binPath=', $binaryPath,
            'start=', 'auto',
            'DisplayName=', $ServiceDisplayName)
    }
    Invoke-ServiceControl @('description', $ServiceName, 'Servicio seguro de transferencia de archivos de la ASE Sinaloa')
    Invoke-ServiceControl @(
        'failure', $ServiceName,
        'reset=', '86400',
        'actions=', 'restart/5000/restart/15000/restart/60000')
    Set-ServiceProductionEnvironment $ServiceName

    if ($OpenWindowsFirewall) {
        $ruleName = "ASE FileXfer HTTPS interno $HttpsPort"
        if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
            New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $HttpsPort | Out-Null
        }
    }

    Start-Service -Name $ServiceName
    $healthEndpoint = [Uri]$HealthUri
    $readinessBuilder = [UriBuilder]::new($healthEndpoint)
    $readinessBuilder.Path = '/readyz'
    $readinessBuilder.Query = ''
    $readinessBuilder.Fragment = ''
    Wait-EndpointReady $healthEndpoint $HealthTimeoutSeconds 'la comprobación de vida'
    Wait-EndpointReady $readinessBuilder.Uri $HealthTimeoutSeconds 'la comprobación de dependencias'

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
    elseif ($null -ne $service) {
        try {
            Restore-ServiceEnvironment `
                -Name $ServiceName `
                -Existed $previousServiceEnvironmentExisted `
                -Value $previousServiceEnvironment
        }
        catch { }
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
