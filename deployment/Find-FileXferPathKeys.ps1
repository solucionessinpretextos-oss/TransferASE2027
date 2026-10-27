[CmdletBinding()]
param(
    [string]$InstallDirectory = 'C:\Servicios\FileXfer',

    [string]$ServiceName = 'FileXferService'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$settingDefinitions = @(
    [pscustomobject]@{
        JsonName = 'PathEncryptionKey'
        EnvironmentName = 'Security__PathEncryptionKey'
        Description = 'AES v2'
    },
    [pscustomobject]@{
        JsonName = 'LegacyPathEncryptionKey'
        EnvironmentName = 'Security__LegacyPathEncryptionKey'
        Description = 'DES heredada'
    }
)

$candidates = [Collections.Generic.List[object]]::new()

function Add-KeyCandidate {
    param(
        [string]$EnvironmentName,
        [string]$Value,
        [string]$Source
    )

    if ([string]::IsNullOrWhiteSpace($Value)) { return }
    $script:candidates.Add([pscustomobject]@{
        EnvironmentName = $EnvironmentName
        Value = $Value
        Source = $Source
    })
}

function Get-Fingerprint {
    param([string]$Value)

    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash($bytes)
        return ([BitConverter]::ToString($hash).Replace('-', '').Substring(0, 12))
    }
    finally {
        $sha256.Dispose()
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

foreach ($definition in $settingDefinitions) {
    Add-KeyCandidate $definition.EnvironmentName `
        ([Environment]::GetEnvironmentVariable($definition.EnvironmentName, 'Process')) `
        'Entorno del PowerShell actual'
    Add-KeyCandidate $definition.EnvironmentName `
        ([Environment]::GetEnvironmentVariable($definition.EnvironmentName, 'Machine')) `
        'Entorno de máquina'
}

$serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$serviceRegistry = Get-ItemProperty -LiteralPath $serviceRegistryPath -Name Environment -ErrorAction SilentlyContinue
$serviceEnvironment = if ($null -eq $serviceRegistry) { @() } else { @($serviceRegistry.Environment) }
foreach ($entry in @($serviceEnvironment)) {
    if ([string]::IsNullOrWhiteSpace([string]$entry)) { continue }
    $separator = ([string]$entry).IndexOf('=')
    if ($separator -le 0) { continue }
    $name = ([string]$entry).Substring(0, $separator)
    $value = ([string]$entry).Substring($separator + 1)
    foreach ($definition in $settingDefinitions) {
        if ($name.Equals($definition.EnvironmentName, [StringComparison]::OrdinalIgnoreCase)) {
            Add-KeyCandidate $definition.EnvironmentName $value "Entorno específico del servicio $ServiceName"
        }
    }
}

$service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
if ($null -ne $service -and -not [string]::IsNullOrWhiteSpace([string]$service.StartName)) {
    try {
        $account = [Security.Principal.NTAccount]::new([string]$service.StartName)
        $sid = $account.Translate([Security.Principal.SecurityIdentifier]).Value
        $userEnvironmentPath = "Registry::HKEY_USERS\$sid\Environment"
        foreach ($definition in $settingDefinitions) {
            $value = Get-ItemPropertyValue -LiteralPath $userEnvironmentPath `
                -Name $definition.EnvironmentName -ErrorAction SilentlyContinue
            Add-KeyCandidate $definition.EnvironmentName ([string]$value) `
                "Perfil de la cuenta $($service.StartName)"
        }
    }
    catch {
        Write-Verbose "No fue posible consultar el perfil de $($service.StartName): $($_.Exception.Message)"
    }
}

$configurationRoots = @(
    [IO.Path]::GetFullPath($InstallDirectory),
    'C:\ProgramData\ASE\FileXfer'
) | Sort-Object -Unique

foreach ($root in $configurationRoots) {
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $root -Filter 'appsettings*.json' -File -Recurse -ErrorAction SilentlyContinue) {
        try { $json = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -ErrorAction Stop }
        catch {
            Write-Warning "Se omitió JSON inválido: $($file.FullName)"
            continue
        }
        $securityProperty = $json.PSObject.Properties['Security']
        if ($null -eq $securityProperty) { continue }
        $security = $securityProperty.Value
        foreach ($definition in $settingDefinitions) {
            $valueProperty = $security.PSObject.Properties[$definition.JsonName]
            $value = if ($null -eq $valueProperty) { $null } else { $valueProperty.Value }
            Add-KeyCandidate $definition.EnvironmentName ([string]$value) $file.FullName
        }
    }
}

$results = [Collections.Generic.List[object]]::new()
foreach ($definition in $settingDefinitions) {
    $matches = @($candidates | Where-Object EnvironmentName -EQ $definition.EnvironmentName)
    $distinctValues = @($matches.Value | Sort-Object -Unique)
    if ($distinctValues.Count -eq 0) {
        $results.Add([pscustomobject]@{
            Llave = $definition.Description
            Estado = 'NO ENCONTRADA'
            Fuente = '-'
            Huella = '-'
        })
        continue
    }
    if ($distinctValues.Count -gt 1) {
        throw "Se encontraron valores diferentes para $($definition.EnvironmentName). No se eligió ninguno automáticamente. Fuentes: $($matches.Source -join '; ')"
    }

    $value = [string]$distinctValues[0]
    if ($definition.EnvironmentName -eq 'Security__PathEncryptionKey') {
        try { $decoded = [Convert]::FromBase64String($value) }
        catch { throw 'La llave AES v2 encontrada no contiene Base64 válido.' }
        if ($decoded.Length -ne 32) { throw 'La llave AES v2 encontrada no decodifica exactamente 32 bytes.' }
        [Array]::Clear($decoded, 0, $decoded.Length)
    }
    elseif ($value.Length -lt 8) {
        throw 'La llave DES heredada encontrada contiene menos de 8 caracteres.'
    }

    [Environment]::SetEnvironmentVariable($definition.EnvironmentName, $value, 'Process')
    $results.Add([pscustomobject]@{
        Llave = $definition.Description
        Estado = 'RECUPERADA Y VALIDADA'
        Fuente = ($matches.Source | Sort-Object -Unique) -join '; '
        Huella = Get-Fingerprint $value
    })
}

$results | Format-Table -AutoSize

if ($results.Estado -contains 'NO ENCONTRADA') {
    throw 'No se recuperaron ambas llaves. No continúe con la instalación ni genere reemplazos.'
}

Write-Host 'PASS: ambas llaves quedaron disponibles solo en el proceso actual de PowerShell; sus valores no fueron impresos.'
