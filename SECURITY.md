# Política de seguridad

## Propiedades que deben mantenerse

1. Ningún archivo se acepta sin identidad autenticada, contexto BuDi autorizado, límites de tamaño y hash persistido verificado.
2. Un PDF no puede enviarse si está cifrado, no abre, no supera la reparación/revalidación o contiene acciones activas (`JavaScript`, `OpenAction`, `Launch`, adjuntos, formularios remotos u otras acciones bloqueadas).
3. Una actualización no se ejecuta sin firma RSA-PSS válida, hash SHA-256 y extracción confinada al staging.
4. Las rutas físicas, secretos, llaves privadas y cadenas de conexión no se devuelven por API ni se versionan.
5. Una desactivación, cambio de roles/MFA o revocación de dispositivo invalida las sesiones aplicables inmediatamente.
6. Producción nunca usa EF InMemory ni rutas de almacenamiento implícitas.
7. Un rol `admin` u `ops` no accede a APIs privilegiadas sin un JWT emitido tras verificar e.firma y TOTP en la misma autenticación.
8. Ninguna descarga entrega bytes si el registro BuDi no pertenece al RFC autorizado, la ruta sale del storage, atraviesa un reparse point o tamaño/SHA-256 difieren del registro interno de carga.
9. Toda carga nueva debe correlacionarse entre FileXferDb y BuDi mediante un `ID_Upload` único. La compatibilidad por ruta aplica únicamente a históricos con `ID_Upload` nulo y nunca sustituye la verificación criptográfica.

## Fuera de alcance del repositorio

- Gestión real de certificados TLS, raíces/intermedios SAT y secretos de producción.
- Reglas de firewall/egress para limitar OCSP/CRL a los hosts SAT configurados.
- EDR/antivirus/DLP del volumen de archivos y del servidor.
- Backups, pruebas de restauración, alta disponibilidad y monitoreo de infraestructura.
- Política legal de retención. Las bajas de usuario son lógicas; la limpieza física requiere un proceso auditable independiente.

## Reporte de vulnerabilidades

Reporte de forma privada al responsable de seguridad del proyecto. Incluya versión, precondiciones, pasos mínimos, impacto y evidencia sin datos personales ni archivos reales. No publique PoC, certificados o secretos en issues públicos.

## Criterio de liberación

No liberar si falla una compilación Release, una prueba PDF/seguridad, la migración de preflight o la validación productiva de e.firma, TLS, SQL y permisos de storage. Un “funciona en mi máquina” no es un control compensatorio.
