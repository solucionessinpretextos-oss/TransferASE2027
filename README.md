# Transfer ASE 2027

Ecosistema corporativo para transferencia segura de archivos entre el cliente WPF y el servidor FileXfer. Incluye dashboard administrativo, actualizador firmado y una compuerta de inspección de archivos; los PDF se validan y reparan mediante qpdf cuando es seguro hacerlo.

## Componentes

- `TransferClienteASE`: cliente WPF .NET 8.
- `FileXfer.Server.Full`: API REST + gRPC + dashboard estático.
- `FileXfer.AdminDashboard`: React/Vite/Tailwind; su build se publica en `FileXfer.Server.Full/wwwroot/admin`.
- `TransferClienteASEUpdater`: actualizador con manifiesto firmado, SHA-256 y rollback.
- `FileXfer.Pdf`: motor común de inspección de archivos y reparación controlada de PDF mediante qpdf.
- `FileXfer.Pdf.SmokeTests`, `FileXfer.FileSafety.SmokeTests` y `FileXfer.Security.SmokeTests`: pruebas ejecutables de controles críticos.
- `deployment/sql`: migraciones idempotentes para FileXferDb y BuDi.

## Garantías relevantes

- Login e.firma con nonce de un solo uso, prueba de posesión RSA, raíces SAT explícitas, URLs OCSP/CRL permitidas y MFA obligatorio para toda API `admin`/`ops`.
- JWT de corta duración, `SecurityVersion`, revocación inmediata de dispositivos y familias de refresh token con detección de reutilización.
- Autorización del contexto de carga derivada del JWT y validada contra BuDi; no se aceptan rutas físicas del cliente.
- Hash final calculado sobre bytes releídos del almacenamiento después de `Flush(true)`.
- El cliente muestra acción actual, archivo `n/N`, porcentaje por archivo y porcentaje total durante inspección, reparación, transferencia y confirmación del servidor.
- El cliente inspecciona una copia temporal; el servidor vuelve a validar los bytes persistidos antes de catalogarlos.
- PDF cifrado, dañado no reparable o con acciones activas se rechaza. La reparación se hace sobre una copia y se revalida.
- Office moderno sin macros, OpenDocument, EPUB, XPS, Visio, XML, JSON, SVG, texto, imágenes comunes y certificados tienen validadores específicos. Se bloquean cifrado, macros, relaciones externas, rutas internas inseguras y expansión excesiva.
- Los formatos legados o propietarios que no permiten demostrar integridad y ausencia de contraseña con los validadores instalados se rechazan; deben convertirse a un formato verificable.
- Actualizaciones firmadas con RSA-PSS, hash SHA-256, extracción segura y recuperación ante fallo.
- Producción falla al arrancar si faltan SQL, storage, hosts DNS explícitos, raíces SAT o allowlist OCSP/CRL.

## Compilación y pruebas

```powershell
dotnet build FileXfer.Server.Full/FileXfer.Server.csproj -c Release
dotnet build TransferClienteASE/TransferClienteASE.csproj -c Release
dotnet build TransferClienteASEUpdater/TransferClienteASE.Updater.csproj -c Release
dotnet run --project FileXfer.FileSafety.SmokeTests/FileXfer.FileSafety.SmokeTests.csproj -c Release
dotnet run --project FileXfer.Security.SmokeTests/FileXfer.Security.SmokeTests.csproj -c Release -- <updater.exe>
```

El dashboard se construye desde `FileXfer.AdminDashboard` con `npm ci` y `npm run build`.

## Producción

No copie secretos a `appsettings.json`. Use variables de entorno, el almacén de secretos de la plataforma o un proveedor administrado. Siga obligatoriamente [deployment/README.md](deployment/README.md) antes de publicar.
