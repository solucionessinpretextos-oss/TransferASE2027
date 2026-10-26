# Instalación y actualización de Transfer ASE

## Qué contiene una liberación

- `client/TransferClienteASE-<versión>-standalone-win-x64.exe`: cliente portátil de un solo archivo. No requiere instalar .NET, qpdf ni copiar DLL junto al EXE.
- `client/TransferClienteASE-<versión>-win-x64.zip`: paquete firmado que consumen los clientes ya instalados. Conserva updater y llave pública externos para compatibilidad con versiones anteriores.
- `server/FileXfer.Server-<versión>-win-x64.zip`: servidor autocontenido, Dashboard, manifiesto de actualización y el ZIP del cliente listo para servir.
- `deployment/FileXfer.Deployment-<versión>.zip`: scripts, plantilla de producción y comprobaciones operativas.
- `release-manifest.json`, `release-manifest.sig` y `checksums.sha256`: evidencia criptográfica de la liberación.

## Requisitos previos que no son opcionales

1. Un nombre DNS para el servidor, por ejemplo `transfer.ase-sinaloa.gob.mx`, apuntando a `187.141.128.194` desde Internet y a `192.168.2.243` desde la red interna si se usa DNS dividido.
2. Un certificado TLS válido cuyo SAN incluya ese DNS. Un certificado para `filexfer.local` no valida una conexión a la IP pública.
3. Regla NAT del puerto TCP 51236 hacia `192.168.2.243:51236` y regla de Firewall de Windows.
4. Cuenta de servicio con lectura/escritura sobre `\\192.168.2.21\DocumentosBuzonDigital_Pruebas\Transfer\Finalizados`. Para ambiente corporativo se recomienda gMSA; `LocalSystem` no es una identidad apropiada para un recurso UNC.
5. La misma llave privada de actualización cuya llave pública ya poseen los clientes instalados. Sin ella no existe una migración automática segura desde esos clientes.

## Primera instalación del servidor

1. Importe el certificado TLS en `Certificados (equipo local) > Personal` y conceda lectura de su llave privada a la cuenta del servicio.
2. Copie `appsettings.Production.template.json` como `appsettings.Production.json` fuera del repositorio.
3. Sustituya todos los valores `__PENDIENTES__`. No deje secretos dentro del ZIP ni de Git.
4. Compruebe que `Kestrel:Endpoints:Https:Certificate:Subject` identifica el certificado correcto y que el puerto es 51236.
5. Abra PowerShell 7 como administrador en la carpeta del paquete de despliegue.
6. Ejecute:

```powershell
pwsh -NoProfile -File .\Install-FileXferServer.ps1 `
  -ServerPackagePath 'D:\Releases\2.2.1.0\server\FileXfer.Server-2.2.1.0-win-x64.zip' `
  -ExpectedSha256 '<SHA256 TOMADO DE checksums.sha256>' `
  -ProductionConfigPath 'D:\Configuracion\FileXfer\appsettings.Production.json' `
  -HealthUri 'https://transfer.ase-sinaloa.gob.mx:51236/healthz' `
  -ServiceAccount 'DOMINIO\gmsa-filexfer$' `
  -OpenWindowsFirewall
```

7. Confirme `HTTP 200` en `/healthz`, abra `/admin/` y pruebe autenticación con e.firma.
8. Desde otro equipo descargue exactamente la URL publicada en `client-update.settings.json` y compruebe su SHA-256.

El instalador extrae primero en staging, conserva la configuración productiva, detiene el servicio, mueve la versión anterior a un respaldo, instala, inicia y consulta salud con TLS normal. Si la salud falla, revierte a la carpeta anterior. No usa `-SkipCertificateCheck`.

## Actualizar el servidor

El nuevo ZIP ya lleva el manifiesto y el cliente que debe publicar. Para actualizar no repita la ruta de configuración: el instalador conserva la existente.

```powershell
pwsh -NoProfile -File .\Install-FileXferServer.ps1 `
  -ServerPackagePath 'D:\Releases\2.2.1.0\server\FileXfer.Server-2.2.1.0-win-x64.zip' `
  -ExpectedSha256 '<SHA256 TOMADO DE checksums.sha256>' `
  -HealthUri 'https://transfer.ase-sinaloa.gob.mx:51236/healthz' `
  -ServiceAccount 'DOMINIO\gmsa-filexfer$'
```

Conserve el respaldo hasta terminar el piloto. Después puede archivarlo fuera del servidor según la política de retención.

## Actualización automática de clientes

1. El cliente consulta `GET /admin/client-updates/transfer-cliente-ase?currentVersion=<versión>`.
2. El servidor responde versión, URL HTTPS, SHA-256 y firma RSA-PSS-SHA256.
3. El cliente descarga el ZIP, valida hash y firma antes de iniciar el updater.
4. El updater vuelve a validar, espera el cierre del cliente, respalda, aplica y revierte si una copia falla.
5. El cliente nuevo arranca desde el mismo directorio.

La liberación debe construirse con la llave histórica:

```powershell
pwsh -NoProfile -File .\build\Build-Release.ps1 `
  -Version 2.2.1.0 `
  -MinSupportedVersion 2.1.0.0 `
  -DownloadUrl 'https://transfer.ase-sinaloa.gob.mx:51236/updates/TransferClienteASE-2.2.1.0-win-x64.zip' `
  -SigningKeyPath 'D:\Secretos\TransferASE\update-private.pem' `
  -TrustedPublicKeyPath 'D:\Configuracion\TransferASE\update-public.pem' `
  -CodeSigningCertificateThumbprint '<HUELLA AUTHENTICODE>' `
  -TimestampServer '<URL RFC3161 AUTORIZADA>' `
  -ReleaseNotes 'Cliente autónomo y actualización automática endurecida.' `
  -IsCritical $true
```

`update-private.pem` nunca se copia al servidor ni al repositorio. El servidor solo necesita metadatos firmados; los clientes llevan la llave pública.

## Si no aparece la llave privada histórica

No cambie el servidor para aceptar paquetes sin firma y no sustituya silenciosamente `update-public.pem`. Las opciones seguras son:

1. Recuperar la llave histórica de la bóveda o respaldo y producir la actualización automática normal.
2. Distribuir una única actualización puente por el mecanismo corporativo de software. Esa versión instala la nueva raíz pública; desde ese momento todas las versiones posteriores vuelven a ser automáticas.

Criptográficamente no existe una tercera opción segura. El certificado público permite verificar, pero no permite fabricar una firma válida.

## Despliegue por anillos

1. Instale manualmente el EXE autónomo en un equipo técnico.
2. Publique a 3-5 equipos piloto y observe salud, fallas, reinicios y descargas grandes durante 24 horas.
3. Mantenga en el servidor el ZIP anterior y su manifiesto durante la ventana de reversión.
4. Habilite `IsCritical=true` para la población general solo después del piloto.
5. Ante errores, retire `client-update.settings.json` nuevo o restaure servidor; nunca edite hashes ni firmas a mano.
