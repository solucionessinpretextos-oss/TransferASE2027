[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'Test-StagingReadiness.ps1'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('filexfer-readiness-' + [Guid]::NewGuid().ToString('N'))
$fileXferVariable = 'FILEXFER_SMOKE_FILEXFERDB_CONNECTION'
$budiVariable = 'FILEXFER_SMOKE_BUDI_CONNECTION'
$originalFileXfer = [Environment]::GetEnvironmentVariable($fileXferVariable)
$originalBudi = [Environment]::GetEnvironmentVariable($budiVariable)

function Write-JsonFile {
    param([string]$Path, [object]$Value)

    [IO.File]::WriteAllText(
        $Path,
        (($Value | ConvertTo-Json -Depth 8) + "`n"),
        [Text.UTF8Encoding]::new($false))
}

function Invoke-PreflightProcess {
    param([string]$Profile, [string]$Report)

    $executable = (Get-Process -Id $PID).Path
    & $executable -NoProfile -NonInteractive -File $scriptPath -ConfigurationPath $Profile -Phase Configuration -ReportPath $Report | Out-Host
    return $LASTEXITCODE
}

function Test-BoundedHttpReader {
    $tokens = $null
    $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        $scriptPath,
        [ref]$tokens,
        [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) {
        throw "El preflight contiene errores de sintaxis: $($parseErrors.Count)."
    }

    $readerFunction = $ast.Find(
        {
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq 'Read-BoundedHttpContent'
        },
        $true)
    if ($null -eq $readerFunction) { throw 'No existe Read-BoundedHttpContent.' }

    # Se evalúa únicamente la función seleccionada desde el script confiable del repositorio.
    Invoke-Expression $readerFunction.Extent.Text
    $cancellationToken = [Threading.CancellationToken]::None

    $validContent = [Net.Http.StreamContent]::new(
        [IO.MemoryStream]::new([byte[]]::new(65536), $false))
    try {
        $validBytes = Read-BoundedHttpContent `
            -Content $validContent `
            -MaximumBytes 65536 `
            -CancellationToken $cancellationToken
        if ($validBytes.Length -ne 65536) {
            throw 'La lectura acotada alteró un cuerpo válido de 64 KiB.'
        }
    }
    finally {
        $validContent.Dispose()
    }

    $oversizedContent = [Net.Http.StreamContent]::new(
        [IO.MemoryStream]::new([byte[]]::new(65537), $false))
    $oversizedRejected = $false
    try {
        [void](Read-BoundedHttpContent `
            -Content $oversizedContent `
            -MaximumBytes 65536 `
            -CancellationToken $cancellationToken)
    }
    catch [IO.InvalidDataException] {
        $oversizedRejected = $true
    }
    finally {
        $oversizedContent.Dispose()
    }

    if (-not $oversizedRejected) {
        throw 'La lectura acotada aceptó un cuerpo mayor de 64 KiB.'
    }
}

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    Test-BoundedHttpReader
    [Environment]::SetEnvironmentVariable(
        $fileXferVariable,
        'Server=tcp:sql-stg.corp.local,1433;Database=FileXferDev;Integrated Security=True;Encrypt=Strict;TrustServerCertificate=False;')
    [Environment]::SetEnvironmentVariable(
        $budiVariable,
        'Server=tcp:budi-stg.corp.local,1433;Database=Budi_Dev;Integrated Security=True;Encrypt=True;TrustServerCertificate=False;')

    $profile = [ordered]@{
        environment = 'Staging'
        serverBaseUrl = 'https://transfer-stg.corp.local'
        allowedHosts = @('transfer-stg.corp.local')
        fileXferDb = [ordered]@{ connectionStringEnvironmentVariable = $fileXferVariable }
        budiDev = [ordered]@{ connectionStringEnvironmentVariable = $budiVariable }
        storage = [ordered]@{
            basePath = 'D:\FileXfer\Storage'
            minimumFreeSpaceGb = 10
            serviceAccount = 'CORP\gmsa-filexfer$'
        }
        alerts = [ordered]@{
            enabled = $true
            smtpHost = 'smtp.corp.local'
            smtpPort = 587
            useTls = $true
            emailFrom = 'filexfer@corp.local'
            emailTo = @('operaciones@corp.local')
        }
    }

    $validProfile = Join-Path $temporaryRoot 'valid.json'
    $validReport = Join-Path $temporaryRoot 'valid-report.json'
    Write-JsonFile $validProfile $profile
    $validExit = Invoke-PreflightProcess $validProfile $validReport
    if ($validExit -ne 0) { throw 'El perfil válido fue rechazado.' }
    $validResult = Get-Content -LiteralPath $validReport -Raw | ConvertFrom-Json
    if (-not $validResult.Summary.Ready -or $validResult.Summary.Failed -ne 0) {
        throw 'El reporte válido no quedó en estado Ready.'
    }

    $profile.allowedHosts = @('*')
    $invalidProfile = Join-Path $temporaryRoot 'invalid.json'
    $invalidReport = Join-Path $temporaryRoot 'invalid-report.json'
    Write-JsonFile $invalidProfile $profile
    $invalidExit = Invoke-PreflightProcess $invalidProfile $invalidReport
    if ($invalidExit -eq 0) { throw 'El perfil inseguro fue aceptado.' }
    $invalidResult = Get-Content -LiteralPath $invalidReport -Raw | ConvertFrom-Json
    if ($invalidResult.Summary.Failed -lt 1) { throw 'El reporte inseguro no registró el fallo.' }

    $profile.allowedHosts = @('transfer-stg.corp.local')
    $profile.serverBaseUrl = 'https://localhost'
    $profile.allowedHosts = @('localhost')
    $localhostProfile = Join-Path $temporaryRoot 'localhost.json'
    $localhostReport = Join-Path $temporaryRoot 'localhost-report.json'
    Write-JsonFile $localhostProfile $profile
    $localhostExit = Invoke-PreflightProcess $localhostProfile $localhostReport
    if ($localhostExit -eq 0) { throw 'El perfil de staging apuntando a localhost fue aceptado.' }

    $profile.serverBaseUrl = 'https://transfer-stg.corp.local'
    $profile.allowedHosts = @('transfer-stg.corp.local')
    $profile.alerts.smtpPassword = 'smoke-test-value'
    $secretProfile = Join-Path $temporaryRoot 'secret.json'
    $secretReport = Join-Path $temporaryRoot 'secret-report.json'
    Write-JsonFile $secretProfile $profile
    $secretExit = Invoke-PreflightProcess $secretProfile $secretReport
    if ($secretExit -eq 0) { throw 'El perfil con secreto embebido fue aceptado.' }
    [void]$profile.alerts.Remove('smtpPassword')

    $profile.storage.requireCurrentIdentity = $false
    $optionalIdentityProfile = Join-Path $temporaryRoot 'optional-identity.json'
    $optionalIdentityReport = Join-Path $temporaryRoot 'optional-identity-report.json'
    Write-JsonFile $optionalIdentityProfile $profile
    $optionalIdentityExit = Invoke-PreflightProcess $optionalIdentityProfile $optionalIdentityReport
    if ($optionalIdentityExit -eq 0) { throw 'El perfil que intenta desactivar la identidad del storage fue aceptado.' }
    [void]$profile.storage.Remove('requireCurrentIdentity')

    [Environment]::SetEnvironmentVariable(
        $fileXferVariable,
        'Server=tcp:sql-stg.corp.local,1433;Database=FileXferDev;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;')
    $unsafeSqlProfile = Join-Path $temporaryRoot 'unsafe-sql.json'
    $unsafeSqlReport = Join-Path $temporaryRoot 'unsafe-sql-report.json'
    Write-JsonFile $unsafeSqlProfile $profile
    $unsafeSqlExit = Invoke-PreflightProcess $unsafeSqlProfile $unsafeSqlReport
    if ($unsafeSqlExit -eq 0) { throw 'La conexión SQL insegura fue aceptada.' }

    Write-Host 'PASS staging-readiness bounded HTTP, configuration, strict encryption and fail-closed smoke tests'
}
finally {
    [Environment]::SetEnvironmentVariable($fileXferVariable, $originalFileXfer)
    [Environment]::SetEnvironmentVariable($budiVariable, $originalBudi)
    if ([IO.Directory]::Exists($temporaryRoot)) {
        [IO.Directory]::Delete($temporaryRoot, $true)
    }
}
