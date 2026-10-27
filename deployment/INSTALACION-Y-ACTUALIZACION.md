# Instalación y actualización de Transfer ASE

## Resultado esperado

El objetivo de esta liberación es el servicio `FileXferPruebas.Server` de 192.168.2.243, instalado en `C:\Servicios\FileXferPruebas`. Su configuración ya usa FileXferDev + Budi_Dev y el storage de pruebas en 192.168.2.21. La instalación reemplazará exclusivamente ese servicio y conservará respaldo transaccional. `FileXferService`, instalado en otra carpeta y conectado a BUZONDIGITAL, queda fuera del alcance.

Topología actual del servicio instalado, que no debe confundirse con el destino de desarrollo:

```text
Cliente de Internet
  -> https://187.141.128.194:51236
  -> NAT/firewall: 187.141.128.194:51236 -> 192.168.2.243:51136
  -> FileXferPruebas.Server en C:\Servicios\FileXferPruebas
  -> SQL FileXferDev + SQL Budi_Dev + storage 192.168.2.21
```

`51236` es el puerto público actual y se traduce a `51136`. El despliegue conserva esa topología y valida que el proceso que ocupa `51136` sea el servicio de pruebas que se está sustituyendo.

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
   Esta misma llave permite escribir nuevas rutas en el formato que siguen consumiendo los demás sistemas BuDi. `Security:PathWriteFormat` debe ser `LegacyDes` en la configuración del servidor compartido; el migrador y la plantilla ya lo establecen. No cambie la llave cada quince días mientras existan registros cifrados con ella. El formato DES histórico usa únicamente sus primeros ocho caracteres.
3. Llave privada RSA histórica de actualización. Debe corresponder a `update-public.pem` instalado en los clientes anteriores.
4. Certificado TLS confiable con SAN para el nombre DNS público recomendado o, si se insiste en usar la IP, SAN IP `187.141.128.194`.
5. Raíces/intermedios SAT autorizados y lista exacta de hosts OCSP/CRL del SAT.
6. Cuenta de servicio con lectura y escritura en `\\192.168.2.21\DocumentosBuzonDigital_Pruebas\Transfer\Finalizados`, el storage del entorno Budi_Dev. El storage 192.168.2.19 pertenece al servicio BUZONDIGITAL actualmente instalado.

No use `CER.cer` del paquete anterior como raíz SAT: es el certificado personal de una e.firma, no una autoridad de confianza.

## Respaldo previo obligatorio

En el servidor, abra PowerShell 7 como administrador:

Compruebe primero el motor. La instalación requiere versión 7 o superior; la utilidad de recuperación de llaves sí es compatible con Windows PowerShell 5.1.

```powershell
$PSVersionTable.PSVersion
Get-Command pwsh.exe -ErrorAction Stop
```

```powershell
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
Copy-Item 'C:\Servicios\FileXferPruebas' "D:\Respaldos\FileXferPruebas-$stamp" -Recurse
sc.exe qc FileXferPruebas.Server | Out-File "D:\Respaldos\FileXferPruebas.Server-$stamp.txt"
Get-ChildItem Env:Security__PathEncryptionKey,Env:Security__LegacyPathEncryptionKey,Env:Security__Jwt__SigningKey -ErrorAction SilentlyContinue |
  Select-Object Name,@{Name='Presente';Expression={-not [string]::IsNullOrWhiteSpace($_.Value)}}
```

El último comando solo informa si los secretos existen; no los imprime.

Si alguna llave no aparece a nivel de máquina, ejecute desde el paquete de despliegue:

```powershell
& .\Find-FileXferPathKeys.ps1 `
  -InstallDirectory 'C:\Servicios\FileXferPruebas' `
  -ServiceName 'FileXferPruebas.Server'
```

La herramienta inspecciona únicamente las configuraciones conocidas, el entorno de máquina, el entorno específico del servicio y el perfil de su cuenta. Valida las llaves, muestra solo su origen y una huella abreviada, y las deja disponibles en el PowerShell actual. No cierre esa consola antes de ejecutar el instalador.

## Preparación de la migración desde la publicación del 26/10/2026

La instalación de pruebas usa `appsettings.json` con FileXferDev/Budi_Dev y storage 192.168.2.21. Prepare fuera de Git un `appsettings.Production.json` basado en la plantilla, conservando esas conexiones, el storage y ambas llaves verificadas. Indique ese archivo mediante `-ProductionConfigPath`. El instalador abortará antes de detener el servicio si las bases, el storage o las llaves no coinciden con el catálogo.

Por omisión, el instalador deja `client-update.settings.json` como `client-update.settings.pending.json`. Así los clientes anteriores reciben `404` silencioso y continúan trabajando, en vez de intentar un paquete firmado con una llave que todavía no conocen. No agregue `-EnableClientUpdates` durante esta primera migración.

Ejemplo para sustituir el servicio de pruebas confirmado:

```powershell
pwsh -NoProfile -File .\Install-FileXferServer.ps1 `
  -ServerPackagePath 'D:\Releases\2.2.1.1\server\FileXfer.Server-2.2.1.1-win-x64.zip' `
  -ExpectedSha256 '<SHA256 DE checksums.sha256>' `
  -ProductionConfigPath 'D:\Configuracion\FileXferDev\appsettings.Production.json' `
  -InstallDirectory 'C:\Servicios\FileXferPruebas' `
  -ServiceName 'FileXferPruebas.Server' `
  -TlsCertificateSubject 'CN=transfer-pruebas.ase-sinaloa.local' `
  -AllowedHosts @('transfer-pruebas.ase-sinaloa.local','SERVERDIG1','187.141.128.194','192.168.2.243','localhost','127.0.0.1') `
  -SatTrustedRootCertificatePaths @('C:\ProgramData\ASE\FileXfer\certs\sat-root.cer') `
  -SatIntermediateCertificatePaths @('C:\ProgramData\ASE\FileXfer\certs\sat-intermediate.cer') `
  -SatAllowedRevocationHosts @('ocsp.sat.gob.mx','crl.sat.gob.mx') `
  -AllowLegacyDevelopmentSqlTransport `
  -ServiceAccount 'Ase-sinaloa\juan.carrillo' `
  -HttpsPort 51136 `
  -HealthUri 'https://192.168.2.243:51136/healthz' `
  -ReplaceExistingService `
  -OpenWindowsFirewall
```

El DNS, el certificado TLS y los nombres SAT del ejemplo deben sustituirse por los autorizados. La cuenta mostrada es la que actualmente ejecuta `FileXferPruebas.Server`; el instalador conserva su identidad y la contraseña almacenada por Windows, y aborta si se intenta cambiarla durante el corte. Confirme sus permisos sobre la ruta UNC. El archivo de configuración debe fijar Kestrel en 51136. El instalador se detiene si faltan las raíces y hosts SAT; no habilita bypass.

`-AllowLegacyDevelopmentSqlTransport` existe para FileXferDev en 192.168.2.3\SQL2014 y Budi_Dev en 192.168.2.8, con el transporte SQL de pruebas informado. El instalador valida ambos servidores, ambas bases y el storage 192.168.2.21 antes de detener un servicio. Retire la excepción cuando SQL Server tenga TLS 1.2+, certificado confiable, `Encrypt=True` y `TrustServerCertificate=False`.

Use como referencia la configuración actual de `FileXferPruebas.Server`; el instalador rechazará cualquier archivo que apunte a BUZONDIGITAL o al storage 192.168.2.19:

```powershell
-LegacyConfigPath 'D:\PublicacionAnterior\appsettings.json'
```

No use `-GeneratePathEncryptionKeyForEmptyDatabase` para Budi_Dev: el inventario contiene 55 rutas AES v2 y generar otra llave impediría abrirlas.

La llave DES compartida es obligatoria aun cuando se migre a un catálogo nuevo, porque el formato de escritura debe seguir siendo legible para los demás sistemas BuDi.

El servicio lee tanto las rutas DES históricas como las 55 rutas AES `v2:` existentes. Aunque escriba rutas nuevas con el formato compartido DES, todavía necesita la llave AES original para leer las 55 anteriores. El instalador no acepta otra llave ni sustituye esos registros automáticamente. Verifique la disponibilidad de ambas llaves y de las rutas antes del cambio de servicio.

## Instalación limpia

1. Copie `appsettings.Production.template.json` como `appsettings.Production.json` fuera de Git.
2. Sustituya todos los marcadores `__...__`.
3. Importe el certificado TLS en `Equipo local > Personal` y conceda lectura de su llave privada a la cuenta del servicio.
4. Configure la llave de recibos RSA persistente y sus ACL.
5. Ejecute el instalador con `-ProductionConfigPath`.

```powershell
pwsh -NoProfile -File .\Install-FileXferServer.ps1 `
  -ServerPackagePath 'D:\Releases\2.2.1.1\server\FileXfer.Server-2.2.1.1-win-x64.zip' `
  -ExpectedSha256 '<SHA256 DE checksums.sha256>' `
  -ProductionConfigPath 'D:\Configuracion\FileXferPruebas\appsettings.Production.json' `
  -InstallDirectory 'C:\Servicios\FileXferPruebas' `
  -ServiceName 'FileXferPruebas.Server' `
  -ServiceAccount 'Ase-sinaloa\juan.carrillo' `
  -HealthUri 'https://192.168.2.243:51136/healthz' `
  -AllowLegacyDevelopmentSqlTransport `
  -ReplaceExistingService `
  -OpenWindowsFirewall
```

## Qué hace el instalador

1. Verifica SHA-256 y rutas seguras del ZIP.
2. Extrae a una carpeta temporal junto a la instalación.
3. Migra o conserva la configuración y comprueba llaves, certificados, SAT, conexiones, ruta y puerto.
4. Materializa las llaves históricas necesarias en la configuración migrada y restringe su ACL a administradores, `SYSTEM` y la cuenta del servicio.
5. Detiene `FileXferPruebas.Server` y verifica que ningún IIS/Kestrel ajeno conserve `51136`.
6. Renombra la versión anterior como respaldo; no la borra.
7. Activa el servidor nuevo, actualiza el servicio e inicia.
8. Consulta `/healthz` con validación TLS normal; nunca usa `SkipCertificateCheck`.
9. Si algo falla, aparta la versión fallida, restaura la anterior y reinicia el servicio.

Si el puerto sigue ocupado, determine si la publicación actual corre en IIS. No ejecute simultáneamente IIS y Windows Service sobre el mismo puerto.

## Validación posterior

```powershell
Get-Service FileXferPruebas.Server
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
  -Version 2.2.1.1 `
  -MinSupportedVersion 2.0.1.2 `
  -DownloadUrl 'https://transfer.ase-sinaloa.gob.mx:51236/updates/TransferClienteASE-2.2.1.1-win-x64.zip' `
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
  'C:\Servicios\FileXferPruebas\client-update.settings.pending.json' `
  'C:\Servicios\FileXferPruebas\client-update.settings.json'
Restart-Service FileXferPruebas.Server
```

También puede usar `-EnableClientUpdates` en instalaciones futuras, pero solo si el paquete está firmado con la llave histórica o la versión puente ya fue desplegada.

## Despliegue por anillos

1. Equipo técnico.
2. De 3 a 5 equipos piloto durante 24 horas.
3. Población general solo si el piloto no presenta regresiones.
4. Conserve el respaldo del servidor, ZIP anterior y manifiesto firmado durante toda la ventana.
5. Ante una anomalía, retire el manifiesto nuevo y restaure el servidor; nunca edite firmas o hashes a mano.
