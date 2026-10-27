# Despliegue y rollback

La generación, firma y verificación de binarios está documentada en `RELEASE.md`. No publique manualmente una carpeta `bin/Release`: use el manifiesto firmado generado por `build/Build-Release.ps1`.

Para la actualización en sitio del servidor publicado el 26/10/2026 use `INSTALACION-Y-ACTUALIZACION.md`. El diagnóstico de ese ZIP está en `BASELINE-SERVIDOR-20261026.md`; `Install-FileXferServer.ps1` invoca `Convert-LegacyServerConfiguration.ps1` para conservar la operación sin reintroducir el canal HTTP antiguo ni PFX dentro del paquete.

## Preflight automatizado

Copie `staging-readiness.example.json` fuera del repositorio, reemplace todos los valores `.example` y configure las dos cadenas SQL exclusivamente en las variables de entorno indicadas por el perfil. El JSON no admite contraseñas, tokens, llaves ni cadenas de conexión embebidas.

Ejecute las fases en orden y conserve cada reporte JSON como evidencia del cambio:

```powershell
$env:FILEXFER_FILEXFERDB_CONNECTION = '<obtener desde el almacén secreto>'
$env:FILEXFER_BUDI_CONNECTION = '<obtener desde el almacén secreto>'

pwsh -NoProfile -File deployment/Test-StagingReadiness.ps1 `
  -ConfigurationPath 'C:\FileXfer\staging-readiness.json' `
  -Phase Infrastructure `
  -ReportPath 'C:\FileXfer\evidence\01-infrastructure.json'

pwsh -NoProfile -File deployment/Test-StagingReadiness.ps1 `
  -ConfigurationPath 'C:\FileXfer\staging-readiness.json' `
  -Phase PostMigration `
  -ReportPath 'C:\FileXfer\evidence\02-post-migration.json'

pwsh -NoProfile -File deployment/Test-StagingReadiness.ps1 `
  -ConfigurationPath 'C:\FileXfer\staging-readiness.json' `
  -Phase PostDeploy `
  -ReportPath 'C:\FileXfer\evidence\03-post-deploy.json'
```

- `Configuration` valida el perfil sin conectarse a servicios; esta fase se usa también en el pipeline de liberación.
- `Infrastructure` comprueba DNS, sesiones SQL cifradas, identidad y ACL exclusiva del storage, ausencia de reparse points, escritura confirmada y negociación SMTP TLS 1.2 con certificado válido. No envía correo ni modifica bases de datos.
- `PostMigration` añade validación de migraciones, tablas, columnas, constraints e índices de FileXferDb y BuDi.
- `PostDeploy` añade `/healthz` y `/readyz` mediante HTTPS, exige HTTP 200 y rechaza redirecciones.

El proceso termina con código distinto de cero ante cualquier `FAIL`. No continúe a la fase siguiente aunque el resto de las comprobaciones aparezca como correcto.

La comprobación de storage debe ejecutarse bajo la misma cuenta configurada en `storage.serviceAccount`; no existe un interruptor para omitirla. La ACL solo admite acceso de datos para esa cuenta, `SYSTEM`, `Administradores` y `CREATOR OWNER`. Cualquier otro principal bloquea el despliegue.

## Orden de despliegue

1. Ejecutar la fase automatizada `Infrastructure`; después ejecutar en BuDi el preflight de solo lectura `sql/000_budi_transfer_archivos_preflight.sql` y conservar ambas salidas.
2. Crear backup consistente de FileXferDb, BuDi y del volumen de archivos; verificar una restauración de muestra. Para BuDi puede usarse `sql/010_budi_backup_before_hardening.sql`, suministrando una ruta nueva mediante `-v BackupFile=...`; el script ejecuta `RESTORE VERIFYONLY`.
3. Detener nuevas cargas o poner el servicio en drenado.
4. Ejecutar `sql/001_budi_transfer_archivos_hardening.sql` en BuDi con `sqlcmd -b -I`. El script usa una transacción, bloqueo de aplicación y postcondiciones; aborta y revierte si detecta duplicados o índices incompatibles.
5. Revisar y ejecutar, en orden, `sql/002_filexferdb_idempotent.sql`, `sql/003_transfer_capabilities_2027.sql`, `sql/004_durable_alert_outbox_2027.sql` y `sql/005_disable_mfa_2027.sql` en FileXferDb. Los scripts son idempotentes; sus preflight abortan antes de truncar datos o crear índices sobre duplicados. El último normaliza cuentas heredadas y revoca sesiones antiguas.
6. Ejecutar la fase `PostMigration`; esta confirma en `__EFMigrationsHistory` que se aplicó hasta `DisableMfa2027`, comprueba que no queden cuentas o enrolamientos MFA activos y verifica tablas de políticas, revisiones, recibos, cadena, índices de reanudación y `AlertOutboxDeliveries`.
7. Instalar primero el servidor (es compatible con el cliente anterior), validar salud y después distribuir cliente 2.2.1.0 y updater desde artefactos firmados.
8. Ejecutar la fase `PostDeploy`; después validar login de cliente y administrativo únicamente con e.firma, renovación de sesión, consulta de políticas, catálogos y una carga controlada por cada acto soportado.
9. Probar los cuatro PDF de control: válido, cifrado, reparable y con contenido activo. Confirmar mensajes, porcentajes y que la reparación se revalida.
10. Cortar una carga a mitad, reintentar y comprobar que continúa desde un offset mayor que cero, genera una sola catalogación y un solo recibo.
11. Descargar un archivo, cancelar a mitad y reanudarlo; confirmar HTTP 206, progreso visible, SHA-256 coincidente y auditoría `file_download_verified`.
12. Reabrir tráfico gradualmente y vigilar errores, latencia, espacio, parciales reanudables, omisiones de verificación, descargas bloqueadas y recibos.

Ejemplo con autenticación integrada; reemplace servidor y ruta por los autorizados:

```powershell
sqlcmd -S "SERVIDOR" -E -d Budi_Dev -b -I -i deployment/sql/000_budi_transfer_archivos_preflight.sql
sqlcmd -S "SERVIDOR" -E -d Budi_Dev -b -I -v BackupFile="D:\Backups\Budi_Dev_YYYYMMDD_HHMM.bak" -i deployment/sql/010_budi_backup_before_hardening.sql
sqlcmd -S "SERVIDOR" -E -d Budi_Dev -b -I -i deployment/sql/001_budi_transfer_archivos_hardening.sql
```

No coloque usuario ni contraseña en scripts, historial de consola o archivos del repositorio. Use autenticación integrada o el almacén secreto del agente de despliegue.

## Bootstrap administrativo

La cuenta sembrada en una base vacía inicia sesión con e.firma y recibe sus permisos mediante rol. MFA/TOTP y sus endpoints de enrolamiento no existen en el runtime. Proteja el RFC administrador con alta controlada, dispositivo administrado, TLS, auditoría y revocación inmediata; nunca compense la ausencia de MFA relajando la validación SAT o compartiendo la llave privada de e.firma.

## Configuración obligatoria

- `ConnectionStrings__FileXferDb`
- `ConnectionStrings__BudiDev`
- `Storage__BasePath` con ACL exclusiva de la cuenta de servicio
- `AllowedHosts` con DNS explícitos, nunca `*`
- `Security__Jwt__SigningKey` con al menos 32 bytes aleatorios
- `Security__PathEncryptionKey` Base64 de exactamente 32 bytes
- `Security__LegacyPathEncryptionKey` con la llave DES histórica que utilizan los demás sistemas BuDi; conserve sus primeros ocho caracteres y manténgala mientras existan rutas históricas
- `Security__PathWriteFormat=LegacyDes` para que las rutas nuevas sigan el formato compartido; el servidor continúa leyendo las rutas AES `v2:` existentes con su llave original
- `Security__Receipts__PrivateKeyPemPath` apuntando a una llave RSA persistente de 3072 bits o más, protegida con ACL de la cuenta de servicio
- `Security__Receipts__PrivateKeyPassword` cuando la llave PEM esté cifrada; suministrarla desde un almacén secreto, nunca en el repositorio
- `Security__SAT__TrustedRootCertificatePaths__N`
- `Security__SAT__IntermediateCertificatePaths__N` cuando aplique
- `Security__SAT__AllowedRevocationHosts__N`
- certificado TLS de Kestrel o terminación TLS corporativa
- `FILEXFER_QPDF_PATH` si qpdf no se despliega junto al servicio

Si se activa `Alerts__Enabled=true`, el arranque exige al menos un canal externo completo:

- Webhook: `Alerts__WebhookUrl` debe ser HTTPS, sin credenciales embebidas ni fragmentos. El cliente no sigue redirecciones, limita conexiones y termina la petición según `Alerts__WebhookTimeoutSeconds` (1–120 segundos).
- Email: `Alerts__SmtpHost`, `Alerts__SmtpPort`, `Alerts__EmailFrom` y `Alerts__EmailTo`; `Alerts__SmtpUseTls` debe permanecer en `true`. Usuario y contraseña SMTP se proporcionan juntos y la contraseña debe venir del almacén secreto mediante `Alerts__SmtpPassword`, nunca de un archivo versionado.
- `Alerts__SmtpTimeoutSeconds` debe estar entre 1 y 120 segundos. Un fallo de entrega se registra, pero no revierte la operación de negocio que originó la alerta.
- La entrega externa usa un outbox persistente en FileXferDb y `Alerts__WorkerCount` consumidores (1–16). `Alerts__QueueCapacity` es ahora el umbral de backlog que degrada la salud; alcanzar ese valor no descarta filas SQL.
- `Alerts__MaxDeliveryAttempts` controla los intentos (1–10) y `Alerts__RetryInitialDelayMilliseconds` el backoff exponencial inicial (100–30000 ms). El mismo evento se suprime durante `Alerts__DeduplicationWindowSeconds` (0–3600) para evitar tormentas.
- La persistencia tiene un límite independiente (`Alerts__PersistenceTimeoutSeconds`). Los workers toman leases recuperables (`Alerts__LeaseSeconds`), consultan por lotes (`Alerts__BatchSize`) y vuelven a intentar después de reinicios o caídas.
- Las entregas exitosas se conservan `Alerts__DeliveredRetentionDays` (30 por omisión); las que agotan reintentos pasan a dead-letter y se conservan `Alerts__DeadLetterRetentionDays` (90 por omisión). El Dashboard marca el subsistema con error mientras exista dead-letter pendiente de atención.
- La semántica externa es al menos una vez: un timeout ocurrido después de que el proveedor aceptó el mensaje puede producir un duplicado. El webhook recibe `Idempotency-Key` con el identificador del evento; configure el receptor para respetarlo cuando soporte idempotencia.

Nunca active `AllowDevelopmentBypass` fuera de Development.

En producción, ambas cadenas SQL deben exigir cifrado (`Encrypt=True` o `Strict`), validar el certificado (`TrustServerCertificate=False`) y usar un certificado emitido para el nombre DNS configurado. El servidor rechazará el arranque si alguna conexión incumple estas condiciones o apunta a LocalDB. No convierta `TrustServerCertificate=True` en una solución permanente: cifra el canal, pero no autentica al servidor y conserva exposición a intermediarios.

El entorno BuDi validado durante la preparación negocia TLS 1.0 y presenta una cadena de certificado no confiable. Antes del corte productivo, habilite TLS 1.2 o superior en SQL Server/SChannel, instale un certificado corporativo cuyo SAN coincida con el DNS de conexión y confirme `Encrypt=True;TrustServerCertificate=False`. La compatibilidad temporal de Development no constituye autorización para desplegar ese perfil.

El servicio falla al arrancar en producción si no existe una llave persistente de recibos. Una llave efímera solo está permitida en `Development`. La llave configurada es también la raíz de confianza de la verificación: no la rote sin un procedimiento que exporte/verifique el historial y despliegue soporte explícito para llaves anteriores; cambiarla directamente hará que los recibos históricos dejen de validarse en línea.

Al reemplazar un servicio existente, el instalador conserva su cuenta de inicio y la contraseña administrada por Windows. Aborta si `-ServiceAccount` no coincide con la identidad registrada. Para crear un servicio nuevo solo admite cuentas integradas, virtuales o gMSA; una cuenta de usuario convencional debe configurarse previamente mediante el procedimiento corporativo de secretos.

## Política de archivos

La lista aceptada ya no vive en el ejecutable. Adminístrela en **Dashboard → Políticas de archivos**. Todo cambio requiere motivo y crea una versión. Deshabilitar “verificación profunda” reduce garantías: el sistema lo advierte al administrador y al usuario, y lo registra en carga y recibo. Use esa excepción solo con análisis de riesgo documentado y un plan de reversión.

`Storage__ResumeRetentionHours` controla cuánto se conserva un parcial (1–168 horas; valor recomendado y predeterminado: 72). La limpieza marca la carga como `Expired` y elimina únicamente el archivo parcial identificado; no toca cargas completadas.

## Validaciones productivas obligatorias

- `/healthz` debe responder 200 cuando el proceso esté vivo. `/readyz` debe responder 200 solo cuando ambas dependencias y el esquema BuDi requerido estén listos; debe responder 503, sin filtrar mensajes SQL, si FileXferDb, BuDi, `ID_Upload` o los índices obligatorios no están disponibles.
- Certificado e.firma SAT real vigente y otro revocado/expirado.
- Cadena SAT con egress limitado a los hosts OCSP/CRL declarados.
- TLS con nombre correcto, cadena completa y rotación de pin con solapamiento.
- Prueba PDF autocontenida con `dotnet run --project FileXfer.Pdf.SmokeTests/FileXfer.Pdf.SmokeTests.csproj -c Release -- tools/qpdf/win-x64/bin/qpdf.exe`.
- Reintento de una carga después de cortar la respuesta: debe existir una sola fila `ID_Upload`.
- Descarga de `GET /receipts/{id}`: `signatureValid=true`, hash del archivo correcto y `ReceiptId` visible en el Dashboard; solo el propietario o un `admin/ops` autenticado con e.firma puede consultar el recibo.
- Cambio concurrente de una política: la segunda escritura debe recibir conflicto y obligar a recargar, nunca sobrescribir silenciosamente.
- Revocación de dispositivo y cambio de roles: access y refresh anteriores deben responder 401.
- Restauración de backup y lectura de archivos bajo la cuenta de servicio.

## Rollback

1. Detener tráfico y conservar logs/evidencia.
2. Restaurar primero el cliente anterior y después los binarios anteriores del servidor/updater. No despliegue el cliente 2.1 contra un servidor que aún no expone políticas ni estado reanudable.
3. Preferir restaurar el backup de FileXferDb tomado antes del despliegue. `sql/099_rollback_session_security.sql` solo revierte la última migración y elimina columnas de sesión; úselo únicamente tras confirmar que se revierte también el servidor.
4. No retire las columnas/índices agregados a BuDi durante un rollback ordinario: son compatibles hacia atrás y eliminarlos aumenta el riesgo. Si la restauración fuese indispensable, mantenga el tráfico detenido, conserve un respaldo posterior al incidente y restaure el backup verificado tomado por el procedimiento anterior.
5. No borre el volumen de archivos. Reconciliar filas y archivos antes de cualquier limpieza.
6. Confirmar login, consulta y descarga con la versión anterior antes de reabrir tráfico.

## Retención física

Las APIs realizan baja lógica y no borran archivos físicos. La línea base recomendada está en `OPERATIONAL-BASELINE.md`: ningún expediente completado se purga hasta que Jurídico/Archivo apruebe la clasificación; después, la eliminación requiere legal hold, cuarentena de 30 días, doble aprobación, comparación de rutas descifradas en todo el catálogo y registro de auditoría.
