# Instalación y actualización de Transfer ASE

## Resultado esperado

La actualización sustituye la versión anterior sin cambiar las bases de datos, la ruta UNC ni el puerto interno. El instalador trabaja en staging, conserva la versión anterior, valida el servicio nuevo y revierte automáticamente si `/healthz` no responde correctamente.

Topología confirmada:

```text
Cliente de Internet
  -> https://187.141.128.194:51236
  -> NAT/firewall: 187.141.128.194:51236 -> 192.168.2.243:51136
  -> FileXferService en C:\Servicios\FileXfer
  -> SQL FileXferDev + SQL Budi_Dev + almacenamiento UNC
```

`51236` es el puerto público. Kestrel y el Firewall de Windows del servidor usan `51136`. Cambiar ambos a `51236` rompería la traducción actual o exigiría modificar el NAT.

## Contenido de la liberación

- `client/TransferClienteASE-<versión>-standalone-win-x64.exe`: cliente autónomo de un solo archivo. No requiere instalar .NET ni copiar DLL.
- `client/TransferClienteASE-<versión>-win-x64.zip`: paquete firmado para actualización automática.
- `server/FileXfer.Server-<versión>-win-x64.zip`: servidor autocontenido, Dashboard, manifiesto y ZIP cliente que se publicará.
- `deployment/FileXfer.Deployment-<versión>.zip`: instalador transaccional, migrador de configuración y comprobaciones.
- `release-manifest.json`, `release-manifest.sig` y `checksums.sha256`: evidencia criptográfica de la liberación.

El paquete nuevo no debe contener PFX, llaves privadas, instaladores históricos ni el contrato HTTP antiguo de actualización.

## Datos que deben recuperarse antes de tocar el servidor

1. `Security__PathEncryptionKey` o `Security:PathEncryptionKey` de la instalación actual. Debe ser Base64 de 32 bytes. Si se sustituye, las rutas AES v2 ya cifradas quedan ilegibles.
2. `Security__LegacyPathEncryptionKey` o `Security:LegacyPathEncryptionKey` de la instalación actual. Debe tener al menos 8 caracteres y permite abrir las rutas DES históricas.
3. Llave privada RSA histórica de actualización. Debe corresponder a `update-public.pem` instalado en los clientes anteriores.
4. Certificado TLS confiable con SAN para el nombre DNS público recomendado o, si se insiste en usar la IP, SAN IP `187.141.128.194`.
5. Raíces/intermedios SAT autorizados y lista exacta de hosts OCSP/CRL del SAT.
6. Cuenta de servicio con lectura y escritura en `\\192.168.2.21\DocumentosBuzonDigital_Pruebas\Transfer\Finalizados`.

No use `CER.cer` del paquete anterior como raíz SAT: es el certificado personal de una e.firma, no una autoridad de confianza.

## Respaldo previo obligatorio

En el servidor, abra PowerShell 7 como administrador:

```powershell
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
Copy-Item 'C:\Servicios\FileXfer' "D:\Respaldos\FileXfer-$stamp" -Recurse
sc.exe qc FileXferService | Out-File "D:\Respaldos\FileXferService-$stamp.txt"
Get-ChildItem Env:Security__PathEncryptionKey,Env:Security__LegacyPathEncryptionKey,Env:Security__Jwt__SigningKey -ErrorAction SilentlyContinue |
  Select-Object Name,@{Name='Presente';Expression={-not [string]::IsNullOrWhiteSpace($_.Value)}}
```

El último comando solo informa si los secretos existen; no los imprime.

Si alguna llave no aparece a nivel de máquina, ejecute desde el paquete de despliegue:

```powershell
& .\Find-FileXferPathKeys.ps1 `
  -InstallDirectory 'C:\Servicios\FileXfer' `
  -ServiceName 'FileXferService'
```

La herramienta inspecciona únicamente las configuraciones conocidas, el entorno de máquina, el entorno específico del servicio y el perfil de su cuenta. Valida las llaves, muestra solo su origen y una huella abreviada, y las deja disponibles en el PowerShell actual. No cierre esa consola antes de ejecutar el instalador.

## Migración transparente desde la publicación del 26/10/2026

La instalación anterior usa `appsettings.json`. El instalador detecta ese archivo, lo convierte en `appsettings.Production.json`, conserva conexiones, almacenamiento y puerto, elimina `Actualizaciones` HTTP y obliga a configurar seguridad faltante. Como la llave JWT encontrada es demasiado corta, la rota de forma automática; esto solo obliga a iniciar sesión otra vez y no modifica archivos ni rutas cifradas.

Por omisión, el instalador deja `client-update.settings.json` como `client-update.settings.pending.json`. Así los clientes anteriores reciben `404` silencioso y continúan trabajando, en vez de intentar un paquete firmado con una llave que todavía no conocen. No agregue `-EnableClientUpdates` durante esta primera migración.

Ejemplo:

```powershell
pwsh -NoProfile -File .\Install-FileXferServer.ps1 `
  -ServerPackagePath 'D:\Releases\2.2.1.0\server\FileXfer.Server-2.2.1.0-win-x64.zip' `
  -ExpectedSha256 '<SHA256 DE checksums.sha256>' `
  -InstallDirectory 'C:\Servicios\FileXfer' `
  -ServiceName 'FileXferService' `
  -TlsCertificateSubject 'CN=transfer.ase-sinaloa.gob.mx' `
  -AllowedHosts @('transfer.ase-sinaloa.gob.mx','187.141.128.194','192.168.2.243','localhost','127.0.0.1') `
  -SatTrustedRootCertificatePaths @('C:\ProgramData\ASE\FileXfer\certs\sat-root.cer') `
  -SatIntermediateCertificatePaths @('C:\ProgramData\ASE\FileXfer\certs\sat-intermediate.cer') `
  -SatAllowedRevocationHosts @('ocsp.sat.gob.mx','crl.sat.gob.mx') `
  -AllowLegacyDevelopmentSqlTransport `
  -ServiceAccount 'DOMINIO\gmsa-filexfer$' `
  -HealthUri 'https://transfer.ase-sinaloa.gob.mx:51236/healthz' `
  -OpenWindowsFirewall
```

Los nombres SAT del ejemplo son ilustrativos: use únicamente rutas y hosts confirmados por la documentación/cadena real del SAT. El instalador se detiene si faltan; no habilita bypass.

`-AllowLegacyDevelopmentSqlTransport` existe exclusivamente porque este servidor apunta a `FileXferDev` y `Budi_Dev` sobre IPv4 privadas con el TLS legado informado. El runtime rechaza esa excepción si cambia la base o el destino deja de ser privado. Retire el interruptor cuando SQL Server tenga TLS 1.2+, certificado confiable, `Encrypt=True` y `TrustServerCertificate=False`.

Si la instalación anterior está en otra carpeta, indique el archivo de forma explícita:

```powershell
-LegacyConfigPath 'D:\PublicacionAnterior\appsettings.json'
```

Use `-GeneratePathEncryptionKeyForEmptyDatabase` solo si se verificó técnicamente que la base FileXfer no contiene ninguna ruta cifrada previa. No es un atajo de instalación.

Use `-AllowMissingLegacyPathEncryptionKeyForVerifiedEmptyCatalog` solo si una consulta comprobó que `Transfer_ArchivosLocales` no contiene ninguna ruta sin prefijo `v2:`. El inventario del 27/10/2026 encontró 8,435 rutas heredadas, por lo que este interruptor está prohibido para el servidor actual.

## Instalación limpia

1. Copie `appsettings.Production.template.json` como `appsettings.Production.json` fuera de Git.
2. Sustituya todos los marcadores `__...__`.
3. Importe el certificado TLS en `Equipo local > Personal` y conceda lectura de su llave privada a la cuenta del servicio.
4. Configure la llave de recibos RSA persistente y sus ACL.
5. Ejecute el instalador con `-ProductionConfigPath`.

```powershell
pwsh -NoProfile -File .\Install-FileXferServer.ps1 `
  -ServerPackagePath 'D:\Releases\2.2.1.0\server\FileXfer.Server-2.2.1.0-win-x64.zip' `
  -ExpectedSha256 '<SHA256 DE checksums.sha256>' `
  -ProductionConfigPath 'D:\Configuracion\FileXfer\appsettings.Production.json' `
  -HealthUri 'https://transfer.ase-sinaloa.gob.mx:51236/healthz' `
  -ServiceAccount 'DOMINIO\gmsa-filexfer$' `
  -OpenWindowsFirewall
```

## Qué hace el instalador

1. Verifica SHA-256 y rutas seguras del ZIP.
2. Extrae a una carpeta temporal junto a la instalación.
3. Migra o conserva la configuración y comprueba llaves, certificados, SAT, conexiones, ruta y puerto.
4. Materializa las llaves históricas necesarias en la configuración migrada y restringe su ACL a administradores, `SYSTEM` y la cuenta del servicio.
5. Detiene `FileXferService` y verifica que ningún IIS/Kestrel ajeno conserve `51136`.
6. Renombra la versión anterior como respaldo; no la borra.
7. Activa el servidor nuevo, actualiza el servicio e inicia.
8. Consulta `/healthz` con validación TLS normal; nunca usa `SkipCertificateCheck`.
9. Si algo falla, aparta la versión fallida, restaura la anterior y reinicia el servicio.

Si el puerto sigue ocupado, determine si la publicación actual corre en IIS. No ejecute simultáneamente IIS y Windows Service sobre el mismo puerto.

## Validación posterior

```powershell
Get-Service FileXferService
Get-NetTCPConnection -LocalPort 51136 -State Listen
Invoke-WebRequest 'https://transfer.ase-sinaloa.gob.mx:51236/healthz'
```

Después:

1. Abra `/admin/` e inicie sesión con e.firma.
2. Pruebe con Juan Manuel Carrillo Rojo: consulta de oficios/requerimientos, carga, reanudación, recibo y descarga.
3. Reinicie cliente y servicio durante una carga de prueba y confirme reanudación.
4. Verifique un archivo grande y uno inválido/reparable.
5. Confirme que el Dashboard distingue transmisión, completado, rechazado y falla.

## Actualización automática de clientes

1. El cliente consulta `GET /admin/client-updates/transfer-cliente-ase`.
2. El servidor responde URL HTTPS, versión, SHA-256 y firma RSA-PSS-SHA256.
3. Cliente y updater validan hash y firma antes de instalar.
4. El updater usa staging, respaldo y rollback local.

La liberación productiva debe construirse con la llave histórica:

```powershell
pwsh -NoProfile -File .\build\Build-Release.ps1 `
  -Version 2.2.1.0 `
  -MinSupportedVersion 2.0.1.2 `
  -DownloadUrl 'https://transfer.ase-sinaloa.gob.mx:51236/updates/TransferClienteASE-2.2.1.0-win-x64.zip' `
  -SigningKeyPath 'D:\Secretos\TransferASE\update-private.pem' `
  -TrustedPublicKeyPath 'D:\Configuracion\TransferASE\update-public.pem' `
  -CodeSigningCertificateThumbprint '<HUELLA AUTHENTICODE>' `
  -TimestampServer '<URL RFC3161 AUTORIZADA>' `
  -ReleaseNotes 'Cliente autónomo, reanudación durable y actualización segura.' `
  -IsCritical $true
```

Si no aparece la llave privada histórica, la única salida segura es una actualización puente distribuida manualmente/corporativamente que instale la nueva llave pública. No se debe reactivar el instalador HTTP sin hash ni aceptar firmas omitidas.

Cuando los clientes ya confíen en la nueva llave pública, active el manifiesto pendiente:

```powershell
Copy-Item `
  'C:\Servicios\FileXfer\client-update.settings.pending.json' `
  'C:\Servicios\FileXfer\client-update.settings.json'
Restart-Service FileXferService
```

También puede usar `-EnableClientUpdates` en instalaciones futuras, pero solo si el paquete está firmado con la llave histórica o la versión puente ya fue desplegada.

## Despliegue por anillos

1. Equipo técnico.
2. De 3 a 5 equipos piloto durante 24 horas.
3. Población general solo si el piloto no presenta regresiones.
4. Conserve el respaldo del servidor, ZIP anterior y manifiesto firmado durante toda la ventana.
5. Ante una anomalía, retire el manifiesto nuevo y restaure el servidor; nunca edite firmas o hashes a mano.
