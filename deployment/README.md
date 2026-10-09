# Despliegue y rollback

## Orden de despliegue

1. Crear backup consistente de FileXferDb, BuDi y del volumen de archivos; verificar una restauración de muestra.
2. Detener nuevas cargas o poner el servicio en drenado.
3. Ejecutar `sql/001_budi_transfer_archivos_hardening.sql` en BuDi. El script aborta si detecta duplicados de `ID_Upload`.
4. Revisar y ejecutar `sql/002_filexferdb_idempotent.sql` en FileXferDb. Sus preflight abortan antes de truncar datos o crear índices sobre duplicados.
5. Instalar servidor, cliente y updater desde artefactos firmados; configurar secretos y certificados fuera del paquete.
6. Validar `/healthz`, login e.firma+MFA, renovación de sesión, consulta de catálogos y una carga controlada por cada acto soportado.
7. Probar los cuatro PDF de control: válido, cifrado, reparable y con contenido activo. Confirmar los mensajes visibles al usuario.
8. Reabrir tráfico gradualmente y vigilar errores, latencia, espacio, alertas de seguridad y rechazos PDF.

## Bootstrap administrativo

La cuenta sembrada en una base vacía puede iniciar sesión únicamente para enrolar su primer TOTP. Hasta obtener un JWT con `amr=efirma+mfa`, la política `PrivilegedMfa` bloquea todas las APIs de `admin` y `ops`. Envíe el código a `POST /auth/mfa/verify` como JSON (`{"code":"123456"}`), nunca en la URL. Después de verificar el enrolamiento, la sesión previa se revoca y el usuario debe iniciar sesión nuevamente con e.firma + TOTP. No deshabilite esta política para “facilitar” el alta: convierta el procedimiento en runbook, no en puerta trasera.

## Configuración obligatoria

- `ConnectionStrings__FileXferDb`
- `ConnectionStrings__BudiDev`
- `Storage__BasePath` con ACL exclusiva de la cuenta de servicio
- `AllowedHosts` con DNS explícitos, nunca `*`
- `Security__Jwt__SigningKey` con al menos 32 bytes aleatorios
- `Security__PathEncryptionKey` Base64 de exactamente 32 bytes
- `Security__SAT__TrustedRootCertificatePaths__N`
- `Security__SAT__IntermediateCertificatePaths__N` cuando aplique
- `Security__SAT__AllowedRevocationHosts__N`
- certificado TLS de Kestrel o terminación TLS corporativa
- `FILEXFER_QPDF_PATH` si qpdf no se despliega junto al servicio

Nunca active `AllowDevelopmentBypass` fuera de Development.

## Validaciones productivas obligatorias

- Certificado e.firma SAT real vigente y otro revocado/expirado.
- Cadena SAT con egress limitado a los hosts OCSP/CRL declarados.
- TLS con nombre correcto, cadena completa y rotación de pin con solapamiento.
- Reintento de una carga después de cortar la respuesta: debe existir una sola fila `ID_Upload`.
- Revocación de dispositivo y cambio de roles: access y refresh anteriores deben responder 401.
- Restauración de backup y lectura de archivos bajo la cuenta de servicio.

## Rollback

1. Detener tráfico y conservar logs/evidencia.
2. Restaurar los binarios anteriores de servidor/cliente/updater.
3. Preferir restaurar el backup de FileXferDb tomado antes del despliegue. `sql/099_rollback_session_security.sql` solo revierte la última migración y elimina columnas de sesión; úselo únicamente tras confirmar que se revierte también el servidor.
4. No retire las columnas/índices agregados a BuDi durante un rollback ordinario: son compatibles hacia atrás y eliminarlos aumenta el riesgo. Restaure el backup de BuDi si fuese indispensable.
5. No borre el volumen de archivos. Reconciliar filas y archivos antes de cualquier limpieza.
6. Confirmar login, consulta y descarga con la versión anterior antes de reabrir tráfico.

## Retención física

Las APIs realizan baja lógica y no borran archivos físicos. Defina plazo legal, cuarentena, doble aprobación, comparación de rutas descifradas en todo el catálogo y registro de auditoría antes de habilitar un job de purga.
