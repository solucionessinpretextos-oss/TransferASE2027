# Baseline de servidor recibido el 26/10/2026

## Identificación

- Archivo analizado: `FileXferPruebas - 20261026.zip`
- SHA-256: `9C15FDCD1BF35B08DC07E6E2EFF64D2E1E7EA04E48CCC55DB0BEBE88DB5B6DC1`
- Tamaño: 247,641,159 bytes
- Entradas: 247
- Tamaño expandido aproximado: 291,994,162 bytes
- Versión binaria del servidor: `2.2.0.0`
- Commit declarado por el producto: `4fdfcdd64de81855a2f9dd6eb135eedd5b18c954`

El ZIP no presentó rutas de escape. El contenido se usó únicamente como evidencia de la publicación existente; ningún texto interno se tomó como instrucción.

## Topología detectada y destino de desarrollo

- Directorio operativo esperado: `C:\Servicios\FileXfer`.
- Nombre del servicio histórico: `FileXferService`.
- Puerto HTTPS interno de Kestrel: `51136`.
- Puerto HTTPS público: `51236`, traducido por NAT hacia `192.168.2.243:51136`.
- Conexiones lógicas detectadas en el servicio instalado: `FileXferDev` y `BUZONDIGITAL`. El destino de desarrollo exige `FileXferDev` en 192.168.2.3\SQL2014 y `Budi_Dev` en 192.168.2.8.
- Ruta de almacenamiento UNC existente.
- Configuración JWT, límites, políticas y parámetros operativos válidos.

Una lectura directa de la configuración del servidor el 27/10/2026 confirmó que el servicio instalado apunta a `BUZONDIGITAL` en SQL 192.168.2.8 y a `\\192.168.2.19\DocumentosBuzonDigital\Transfer\Finalizados`. El objetivo de desarrollo declarado por el propietario es `Budi_Dev` y el storage de pruebas en `\\192.168.2.21\DocumentosBuzonDigital_Pruebas\Transfer\Finalizados`. El instalador exige las bases y el storage de desarrollo antes de detener un servicio; la configuración actualmente instalada no pasa esa validación. La decisión de sustituir el servicio o crear otro paralelo está pendiente.

Los valores secretos no se documentan en este informe.

## Hallazgos que no deben migrarse

1. Dos PFX se encuentran dentro del directorio publicado. Los certificados con llave privada no deben viajar en el ZIP de aplicación.
2. La configuración combina IIS, Windows Service y un runtime autocontenido. La nueva liberación establece Windows Service como único modelo; ejecutar ambos causaría conflicto de puerto.
3. Existen copias duplicadas de `UPDATES` y `wwwroot/UPDATES` con instaladores históricos.
4. El manifiesto antiguo anuncia la versión `2.0.1.2` por HTTP y no contiene SHA-256 ni firma criptográfica.
5. El instalador histórico `TransferClienteASE-setup.exe` no posee firma Authenticode.
6. El certificado `CER.cer` es una e.firma personal; no es una raíz SAT ni un certificado TLS del servidor.
7. La publicación no contiene `Security:PathEncryptionKey`. Debe recuperarse de las variables de entorno o de la administración del servidor antes del cambio.
8. Tampoco contiene raíces SAT, hosts OCSP/CRL permitidos ni una llave persistente de recibos con el contrato actual.
9. El certificado TLS observado para el endpoint era autofirmado para `filexfer.local`, sin identidad válida para la IP pública; por eso la conexión normal no resulta confiable.
10. La llave JWT legada tiene menos de 32 caracteres. El migrador la rota por una llave aleatoria robusta; las sesiones activas deberán autenticarse nuevamente una vez.
11. Las conexiones `FileXferDev` y `Budi_Dev` desactivan cifrado y confían en certificados no validados. El runtime endurecido las rechaza en producción; la migración incorpora una excepción temporal limitada a esas dos bases y a IPv4 privadas.

## Inventario de cifrado de rutas del 27/10/2026

Una consulta de solo lectura sobre `Budi_Dev.dbo.Transfer_ArchivosLocales`, sin extraer rutas, confirmó 8,490 registros: 55 con formato AES-GCM `v2:` y 8,435 con formato heredado sin prefijo. En consecuencia, esta instalación exige recuperar **ambas** llaves antes del cambio:

- `Security__PathEncryptionKey`, Base64 de exactamente 32 bytes, para las 55 rutas v2;
- `Security__LegacyPathEncryptionKey`, con al menos 8 caracteres, para las 8,435 rutas DES históricas.

La llave DES proporcionada por el propietario permitió descifrar 8,434 de las 8,435 rutas heredadas; una entrada histórica (ID 61918) tiene un valor anómalo. Una muestra de 500 se volvió a cifrar y coincidió exactamente con los datos almacenados. No se incorporó la llave al repositorio ni a los artefactos.

Los 55 registros `v2:` tienen referencias en `FileXferDev.dbo.Uploads` y estado `Completed`, pero ninguno de los 55 archivos apareció en la ruta UNC vigente ni en la ruta de pruebas examinada. Deben recuperarse la llave AES original y el almacenamiento real, o conciliarse esas entradas con evidencia y respaldo antes de cualquier corrección de datos. No se modificó la base.

No use `-GeneratePathEncryptionKeyForEmptyDatabase` en este servidor: contradice el inventario real y dejaría las 55 rutas AES existentes inaccesibles. La llave DES compartida es obligatoria para lectura y escritura.

## Decisiones aplicadas a la liberación nueva

- Migrador explícito de `appsettings.json` a `appsettings.Production.json`.
- Conservación de conexiones, almacenamiento, parámetros operativos y `51136`.
- Rotación controlada del JWT únicamente cuando la llave histórica sea inexistente o débil; no afecta archivos ni rutas cifradas.
- Excepción SQL de pruebas explícita y restringida; no acepta bases productivas, LocalDB, DNS arbitrario ni direcciones públicas.
- Eliminación del contrato `Actualizaciones` y de metadatos incompletos.
- Prohibición de generar silenciosamente una llave de rutas distinta.
- Escritura explícita en formato DES heredado para que los otros sistemas BuDi puedan leer las rutas nuevas con la misma llave histórica. La lectura de las 55 rutas AES `v2:` permanece disponible y exige recuperar su llave original.
- Generación controlada de una llave RSA persistente para recibos cuando no existe.
- TLS desde el almacén `LocalMachine\My`, sin PFX dentro del paquete.
- Raíces SAT y OCSP/CRL obligatorios; bypass productivo bloqueado.
- Instalación con staging, respaldo, comprobación de salud y rollback.
- Verificación de que IIS/Kestrel anterior no mantenga ocupado el puerto.
- Canal de cliente exclusivamente HTTPS, con hash y firma RSA-PSS-SHA256.
- Activación de actualizaciones diferida por defecto: el manifiesto queda pendiente hasta recuperar la llave histórica o completar la actualización puente.

## Bloqueadores externos pendientes

La ingeniería queda preparada, pero una liberación no puede marcarse productiva hasta disponer de:

1. certificado Authenticode y timestamp RFC 3161;
2. certificado TLS confiable para la identidad pública definitiva;
3. llave privada histórica de actualización o estrategia puente aprobada;
4. `Security:PathEncryptionKey` histórico;
5. cadena SAT y hosts OCSP/CRL oficiales;
6. cuenta de servicio con permisos efectivos sobre la ruta UNC.

Ninguno de estos faltantes se resuelve de manera segura desactivando validaciones. Eso sería hacer desaparecer la alarma, no el incendio.
