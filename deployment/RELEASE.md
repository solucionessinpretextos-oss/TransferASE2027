# Liberación verificable

## Contrato de actualización

El canal vigente distribuye un ZIP de cliente. El servidor publica versión, URL HTTPS, SHA-256 y firma RSA-PSS-SHA256 sobre el siguiente contenido canónico:

```text
FILEXFER-UPDATE-V1
transfer-cliente-ase
<version>
<sha256-en-minúsculas>
```

El cliente verifica hash y firma antes de iniciar `TransferClienteASE.Updater.exe`. El updater vuelve a verificarlos, extrae en staging, valida rutas y límites, respalda la instalación, aplica archivos y revierte si alguna copia falla. Los canales legados sin firma permanecen retirados.

## Construcción

Requisitos:

- PowerShell 7, .NET 8 SDK, Node.js/npm y qpdf incluido en `tools`.
- Llave RSA privada de liberación de 3072 bits o más, fuera del repositorio.
- Llave pública correspondiente ya distribuida como raíz confiable.
- Para producción, certificado Authenticode con llave privada y servidor RFC 3161 de sellado de tiempo.
- Árbol Git limpio.

Ejemplo productivo:

```powershell
pwsh -NoProfile -File build/Build-Release.ps1 `
  -Version 2.1.1.0 `
  -MinSupportedVersion 2.1.0.0 `
  -DownloadUrl "https://descargas.ase-sinaloa.gob.mx/transfer/TransferClienteASE-2.1.1.0-win-x64.zip" `
  -SigningKeyPath "D:\Secretos\transfer-update-private.pem" `
  -TrustedPublicKeyPath "D:\Configuracion\transfer-update-public.pem" `
  -CodeSigningCertificateThumbprint "HUELLA_CERTIFICADO" `
  -TimestampServer "URL_RFC3161_AUTORIZADA" `
  -ReleaseNotes "Correcciones de seguridad y estabilidad."
```

Si la llave privada PEM está cifrada, la contraseña se lee de `TRANSFER_RELEASE_SIGNING_KEY_PASSWORD`. Nunca se acepta por argumento ni se copia al artefacto.

`-AllowUnsignedBinaries` y `-AllowDirtyWorkingTree` existen únicamente para staging. Cualquiera de esas condiciones deja `ProductionReady=false`; el verificador rechaza el paquete salvo que se proporcione explícitamente `-AllowNonProduction`.

## Salidas

La liberación se crea en `artifacts/releases/<version>` y contiene:

- ZIP firmado del cliente.
- ZIP del servidor.
- paquete de scripts de despliegue;
- updater autónomo para verificación;
- símbolos PDB separados del runtime y destinados a diagnóstico restringido;
- verificador independiente incluido en `tools`;
- llave pública de bootstrap;
- fragmento `client-update.settings.json`;
- hashes SHA-256;
- manifiesto de liberación y firma RSA-PSS.

La llave privada nunca se copia a la salida. El pipeline inspecciona también el ZIP y rechaza extensiones o nombres compatibles con material privado.

## Verificación independiente

```powershell
pwsh -NoProfile -File build/Verify-Release.ps1 `
  -ReleaseDirectory "D:\Releases\2.1.1.0" `
  -TrustedPublicKeyPath "D:\Configuracion\transfer-update-public.pem"
```

El verificador comprueba firma del manifiesto, hash y longitud de todos los artefactos, firma canónica del cliente, confinamiento del ZIP, presencia del cliente/updater/llave pública y ejecuta el updater en modo `--verify-only`.

## Publicación y rollback

1. Conservar la liberación en almacenamiento inmutable y restringido.
2. Publicar primero el ZIP en la URL definitiva y comprobar que su SHA-256 coincide.
3. Desplegar servidor y configuración únicamente después de que la descarga sea accesible por HTTPS.
4. Mantener disponible el ZIP de la versión anterior y su configuración firmada durante toda la ventana.
5. Desplegar por anillos: equipo técnico, grupo piloto y población general.
6. Si aumenta el error de inicio o actualización, retirar el manifiesto nuevo; las instalaciones no iniciadas permanecen en la versión anterior y las iniciadas usan el rollback local del updater.

Una llave de actualización no se rota sustituyendo silenciosamente `update-public.pem`. La rotación requiere una versión puente firmada con la llave anterior que distribuya la nueva raíz y un periodo de solapamiento.
