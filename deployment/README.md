# Despliegue y rollback

## Orden de despliegue

1. Crear backup consistente de FileXferDb, BuDi y del volumen de archivos; verificar una restauración de muestra.
2. Detener nuevas cargas o poner el servicio en drenado.
3. Ejecutar `sql/001_budi_transfer_archivos_hardening.sql` en BuDi. El script aborta si detecta duplicados de `ID_Upload`.
4. Revisar y ejecutar `sql/002_filexferdb_idempotent.sql` en FileXferDb. Sus preflight abortan antes de truncar datos o crear índices sobre duplicados.
5. Aplicar la migración EF `TransferCapabilities2027`; confirmar tablas de políticas, revisiones, recibos, cadena e índices de reanudación.
6. Instalar primero el servidor (es compatible con el cliente anterior), validar salud y después distribuir cliente 2.1.0.0 y updater desde artefactos firmados.
7. Validar `/healthz` (proceso vivo) y `/readyz` (FileXferDb y BuDi disponibles), login e.firma+MFA, renovación de sesión, consulta de políticas, catálogos y una carga controlada por cada acto soportado.
8. Probar los cuatro PDF de control: válido, cifrado, reparable y con contenido activo. Confirmar mensajes, porcentajes y que la reparación se revalida.
9. Cortar una carga a mitad, reintentar y comprobar que continúa desde un offset mayor que cero, genera una sola catalogación y un solo recibo.
10. Reabrir tráfico gradualmente y vigilar errores, latencia, espacio, parciales reanudables, omisiones de verificación y recibos.

## Bootstrap administrativo

La cuenta sembrada en una base vacía puede iniciar sesión únicamente para enrolar su primer TOTP. Hasta obtener un JWT con `amr=efirma+mfa`, la política `PrivilegedMfa` bloquea todas las APIs de `admin` y `ops`. Envíe el código a `POST /auth/mfa/verify` como JSON (`{"code":"123456"}`), nunca en la URL. Después de verificar el enrolamiento, la sesión previa se revoca y el usuario debe iniciar sesión nuevamente con e.firma + TOTP. No deshabilite esta política para “facilitar” el alta: convierta el procedimiento en runbook, no en puerta trasera.

## Configuración obligatoria

- `ConnectionStrings__FileXferDb`
- `ConnectionStrings__BudiDev`
- `Storage__BasePath` con ACL exclusiva de la cuenta de servicio
- `AllowedHosts` con DNS explícitos, nunca `*`
- `Security__Jwt__SigningKey` con al menos 32 bytes aleatorios
- `Security__PathEncryptionKey` Base64 de exactamente 32 bytes
- `Security__Receipts__PrivateKeyPemPath` apuntando a una llave RSA persistente de 3072 bits o más, protegida con ACL de la cuenta de servicio
- `Security__Receipts__PrivateKeyPassword` cuando la llave PEM esté cifrada; suministrarla desde un almacén secreto, nunca en el repositorio
- `Security__SAT__TrustedRootCertificatePaths__N`
- `Security__SAT__IntermediateCertificatePaths__N` cuando aplique
- `Security__SAT__AllowedRevocationHosts__N`
- certificado TLS de Kestrel o terminación TLS corporativa
- `FILEXFER_QPDF_PATH` si qpdf no se despliega junto al servicio

Nunca active `AllowDevelopmentBypass` fuera de Development.

En producción, ambas cadenas SQL deben exigir cifrado (`Encrypt=True` o `Strict`), validar el certificado (`TrustServerCertificate=False`) y usar un certificado emitido para el nombre DNS configurado. El servidor rechazará el arranque si alguna conexión incumple estas condiciones o apunta a LocalDB. No convierta `TrustServerCertificate=True` en una solución permanente: cifra el canal, pero no autentica al servidor y conserva exposición a intermediarios.

El entorno BuDi validado durante la preparación negocia TLS 1.0 y presenta una cadena de certificado no confiable. Antes del corte productivo, habilite TLS 1.2 o superior en SQL Server/SChannel, instale un certificado corporativo cuyo SAN coincida con el DNS de conexión y confirme `Encrypt=True;TrustServerCertificate=False`. La compatibilidad temporal de Development no constituye autorización para desplegar ese perfil.

El servicio falla al arrancar en producción si no existe una llave persistente de recibos. Una llave efímera solo está permitida en `Development`. La llave configurada es también la raíz de confianza de la verificación: no la rote sin un procedimiento que exporte/verifique el historial y despliegue soporte explícito para llaves anteriores; cambiarla directamente hará que los recibos históricos dejen de validarse en línea.

## Política de archivos

La lista aceptada ya no vive en el ejecutable. Adminístrela en **Dashboard → Políticas de archivos**. Todo cambio requiere motivo y crea una versión. Deshabilitar “verificación profunda” reduce garantías: el sistema lo advierte al administrador y al usuario, y lo registra en carga y recibo. Use esa excepción solo con análisis de riesgo documentado y un plan de reversión.

`Storage__ResumeRetentionHours` controla cuánto se conserva un parcial (1–168 horas). La limpieza marca la carga como `Expired` y elimina únicamente el archivo parcial identificado; no toca cargas completadas.

## Validaciones productivas obligatorias

- `/healthz` debe responder 200 cuando el proceso esté vivo. `/readyz` debe responder 200 con ambas dependencias en `ready`; debe responder 503, sin filtrar mensajes SQL, si FileXferDb o BuDi no están disponibles.
- Certificado e.firma SAT real vigente y otro revocado/expirado.
- Cadena SAT con egress limitado a los hosts OCSP/CRL declarados.
- TLS con nombre correcto, cadena completa y rotación de pin con solapamiento.
- Prueba PDF autocontenida con `dotnet run --project FileXfer.Pdf.SmokeTests/FileXfer.Pdf.SmokeTests.csproj -c Release -- tools/qpdf/win-x64/bin/qpdf.exe`.
- Reintento de una carga después de cortar la respuesta: debe existir una sola fila `ID_Upload`.
- Descarga de `GET /receipts/{id}`: `signatureValid=true`, hash del archivo correcto y `ReceiptId` visible en el Dashboard; un `admin/ops` sin MFA no puede consultar un recibo ajeno.
- Cambio concurrente de una política: la segunda escritura debe recibir conflicto y obligar a recargar, nunca sobrescribir silenciosamente.
- Revocación de dispositivo y cambio de roles: access y refresh anteriores deben responder 401.
- Restauración de backup y lectura de archivos bajo la cuenta de servicio.

## Rollback

1. Detener tráfico y conservar logs/evidencia.
2. Restaurar primero el cliente anterior y después los binarios anteriores del servidor/updater. No despliegue el cliente 2.1 contra un servidor que aún no expone políticas ni estado reanudable.
3. Preferir restaurar el backup de FileXferDb tomado antes del despliegue. `sql/099_rollback_session_security.sql` solo revierte la última migración y elimina columnas de sesión; úselo únicamente tras confirmar que se revierte también el servidor.
4. No retire las columnas/índices agregados a BuDi durante un rollback ordinario: son compatibles hacia atrás y eliminarlos aumenta el riesgo. Restaure el backup de BuDi si fuese indispensable.
5. No borre el volumen de archivos. Reconciliar filas y archivos antes de cualquier limpieza.
6. Confirmar login, consulta y descarga con la versión anterior antes de reabrir tráfico.

## Retención física

Las APIs realizan baja lógica y no borran archivos físicos. Defina plazo legal, cuarentena, doble aprobación, comparación de rutas descifradas en todo el catálogo y registro de auditoría antes de habilitar un job de purga.
