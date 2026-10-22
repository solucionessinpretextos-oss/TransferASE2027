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
- `deployment/Test-StagingReadiness.ps1`: preflight bloqueante y auditable para configuración, infraestructura, esquema y salud posterior al despliegue.

## Manuales oficiales

La documentación gráfica y detallada de cliente, Dashboard, servidor, actualizador, validación de archivos y pruebas automatizadas está centralizada en [`docs/manuales`](docs/manuales/README.md). Cada manual se mantiene en Markdown para consulta directa en GitHub y en Word/PDF para distribución institucional.

## Garantías relevantes

- Login exclusivamente con e.firma para todos los roles, con nonce de un solo uso, prueba de posesión RSA, raíces SAT explícitas y URLs OCSP/CRL permitidas. El ecosistema no solicita ni admite MFA en Development o Production.
- Las APIs administrativas mantienen autorización por roles `admin`/`ops`, revocación de dispositivo, versión de seguridad y auditoría; no dependen de secretos TOTP.
- El perfil local `Development` puede omitir cadena y revocación SAT únicamente para pruebas de integración, pero conserva vigencia, RFC, correspondencia CER/KEY y prueba criptográfica de posesión. Producción rechaza esta opción al arrancar.
- JWT de corta duración, `SecurityVersion`, revocación inmediata de dispositivos y familias de refresh token con detección de reutilización.
- Autorización del contexto de carga derivada del JWT y validada contra BuDi; no se aceptan rutas físicas del cliente.
- Hash final calculado sobre bytes releídos del almacenamiento después de `Flush(true)`.
- Cargas reanudables por `FileUuid + longitud + SHA-256 + contexto + dispositivo`: el cliente conserva una identidad estable, una copia segura y un diario SQLite local sin credenciales. Tras reiniciar cliente, servidor o ambos, reconstruye la cola, consulta el checkpoint durable y continúa desde el último byte confirmado. Los parciales vencen y se eliminan de forma controlada.
- Políticas de archivo versionadas en base de datos: desde el Dashboard se habilita/bloquea cada extensión, se define límite de tamaño y se decide si requiere verificación profunda. Cada cambio exige motivo, control de concurrencia e historial con rollback.
- El cliente muestra acción actual, archivo `n/N`, porcentaje por archivo y porcentaje total durante inspección, reparación, transferencia y confirmación del servidor.
- La descarga servidor→cliente exige propietario o `admin/ops` autenticado con e.firma, revalida tamaño y SHA-256 sobre el mismo stream bloqueado que se entrega, admite HTTP Range y conserva parciales para reanudación. El cliente muestra progreso por archivo y total y vuelve a verificar SHA-256 antes de publicar el archivo descargado.
- Las cargas nuevas se correlacionan entre BuDi y FileXfer mediante `ID_Upload`; los registros históricos sin identificador conservan una ruta de lectura compatible y siempre verifican hash/tamaño antes de descargarse.
- Readiness valida conectividad, columnas e índices obligatorios de BuDi para no declarar listo un servidor incapaz de catalogar cargas idempotentes.
- El cliente inspecciona una copia temporal; el servidor vuelve a validar los bytes persistidos antes de catalogarlos.
- PDF cifrado, dañado no reparable o con acciones activas se rechaza. La reparación se hace sobre una copia y se revalida.
- Office moderno sin macros, OpenDocument, EPUB, XPS, Visio, XML, JSON, SVG, texto, imágenes comunes y certificados tienen validadores específicos. Se bloquean cifrado, macros, relaciones externas, rutas internas inseguras y expansión excesiva.
- Los formatos legados o propietarios que no permiten demostrar integridad y ausencia de contraseña con los validadores instalados se rechazan; deben convertirse a un formato verificable.
- La opción “sin verificación profunda” es una excepción administrativa explícita y visible. Aun así se conservan autenticación, autorización, límites, copia inmutable, SHA-256, bloqueo de ejecutables y recibo; no demuestra ausencia de contraseña ni estructura sana.
- Cada carga catalogada emite un recibo RSA-PSS-SHA256 firmado, idempotente y enlazado criptográficamente con el recibo anterior. El Dashboard expone `TraceId`, política aplicada, reanudaciones, omisiones y `ReceiptId`.
- Actualizaciones firmadas con RSA-PSS, hash SHA-256, extracción segura y recuperación ante fallo.
- Pipeline `build/Build-Release.ps1`: publica cliente, updater y servidor; ejecuta pruebas; genera ZIP, hashes, configuración y manifiesto firmado; `build/Verify-Release.ps1` realiza verificación independiente y ejecuta el updater en modo de solo validación.
- El preflight de staging rechaza comodines DNS, marcadores, secretos embebidos, LocalDB fuera de desarrollo, SQL sin cifrado/certificado validado, SMTP sin TLS, storage inaccesible y contratos de esquema incompletos.
- Producción falla al arrancar si faltan SQL, storage, hosts DNS explícitos, raíces SAT o allowlist OCSP/CRL.

## Compilación y pruebas

```powershell
dotnet build FileXfer.Server.Full/FileXfer.Server.csproj -c Release
dotnet build TransferClienteASE/TransferClienteASE.csproj -c Release
dotnet build TransferClienteASEUpdater/TransferClienteASE.Updater.csproj -c Release
dotnet run --project FileXfer.Pdf.SmokeTests/FileXfer.Pdf.SmokeTests.csproj -c Release -- tools/qpdf/win-x64/bin/qpdf.exe
dotnet run --project FileXfer.FileSafety.SmokeTests/FileXfer.FileSafety.SmokeTests.csproj -c Release
dotnet run --project FileXfer.Security.SmokeTests/FileXfer.Security.SmokeTests.csproj -c Release -- <updater.exe>
pwsh -NoProfile -File deployment/Test-StagingReadiness.Smoke.ps1
```

El dashboard se construye desde `FileXfer.AdminDashboard` con `npm ci` y `npm run build`.

## Operación de políticas y recibos

- `GET /file-policies/effective`: política mínima que consume el cliente autenticado; no expone autor, motivo ni marcas de auditoría administrativas.
- `/admin/file-policies`: consulta para `admin/ops` autenticados con e.firma; las modificaciones y rollback exigen rol `admin`.
- `GET /receipts/{receiptId}`: entrega payload canónico, firma, llave pública, hashes de cadena y resultado de verificación al propietario; el acceso cruzado exige rol `admin/ops` y `amr=efirma`.
- `GET /archivos-locales/{id}/download`: descarga autenticada, auditable y reanudable; los archivos históricos que no puedan correlacionarse con hash y longitud internos se bloquean de forma segura.
- La verificación del recibo compara su llave pública y `KeyId` contra la llave confiable configurada; sustituir toda la firma y su llave dentro de SQL no produce `signatureValid=true`.
- Los spans usan `ActivitySource` `FileXfer.Server.Transfer` y las métricas el `Meter` `FileXfer.Server`, listos para el colector OpenTelemetry corporativo.

## Producción

No copie secretos a `appsettings.json`. Use variables de entorno, el almacén de secretos de la plataforma o un proveedor administrado. Siga obligatoriamente [deployment/README.md](deployment/README.md) antes de publicar; las decisiones pendientes y la propuesta de retención/RPO/RTO están en [deployment/OPERATIONAL-BASELINE.md](deployment/OPERATIONAL-BASELINE.md).
