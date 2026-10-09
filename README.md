# Transfer ASE 2027

Ecosistema corporativo para transferencia segura de archivos entre el cliente WPF y el servidor FileXfer. Incluye dashboard administrativo, actualizador firmado y una compuerta PDF basada en qpdf.

## Componentes

- `TransferClienteASE`: cliente WPF .NET 8.
- `FileXfer.Server.Full`: API REST + gRPC + dashboard estático.
- `FileXfer.AdminDashboard`: React/Vite/Tailwind; su build se publica en `FileXfer.Server.Full/wwwroot/admin`.
- `TransferClienteASEUpdater`: actualizador con manifiesto firmado, SHA-256 y rollback.
- `FileXfer.Pdf`: inspección, rechazo y reparación controlada de PDF mediante qpdf.
- `FileXfer.Pdf.SmokeTests` y `FileXfer.Security.SmokeTests`: pruebas ejecutables de controles críticos.
- `deployment/sql`: migraciones idempotentes para FileXferDb y BuDi.

## Garantías relevantes

- Login e.firma con nonce de un solo uso, prueba de posesión RSA, raíces SAT explícitas, URLs OCSP/CRL permitidas y MFA obligatorio para toda API `admin`/`ops`.
- JWT de corta duración, `SecurityVersion`, revocación inmediata de dispositivos y familias de refresh token con detección de reutilización.
- Autorización del contexto de carga derivada del JWT y validada contra BuDi; no se aceptan rutas físicas del cliente.
- Hash final calculado sobre bytes releídos del almacenamiento después de `Flush(true)`.
- PDF cifrado, dañado no reparable o con acciones activas se rechaza. La reparación se hace sobre una copia y se revalida.
- Actualizaciones firmadas con RSA-PSS, hash SHA-256, extracción segura y recuperación ante fallo.
- Producción falla al arrancar si faltan SQL, storage, hosts DNS explícitos, raíces SAT o allowlist OCSP/CRL.

## Compilación y pruebas

```powershell
dotnet build FileXfer.Server.Full/FileXfer.Server.csproj -c Release
dotnet build TransferClienteASE/TransferClienteASE.csproj -c Release
dotnet build TransferClienteASEUpdater/TransferClienteASE.Updater.csproj -c Release
dotnet run --project FileXfer.Security.SmokeTests/FileXfer.Security.SmokeTests.csproj -c Release -- <updater.exe>
```

El dashboard se construye desde `FileXfer.AdminDashboard` con `npm ci` y `npm run build`.

## Producción

No copie secretos a `appsettings.json`. Use variables de entorno, el almacén de secretos de la plataforma o un proveedor administrado. Siga obligatoriamente [deployment/README.md](deployment/README.md) antes de publicar.
