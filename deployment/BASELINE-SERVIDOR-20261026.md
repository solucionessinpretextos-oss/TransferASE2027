# Baseline de servidor recibido el 26/10/2026

> **Corrección operativa del 28/10/2026:** la atribución original del puerto público `51236` al servicio de pruebas era incorrecta. Ese puerto conduce a la instalación separada `FileXferService`. Para `FileXferPruebas.Server` se autorizó `187.141.128.194:51237 -> 192.168.2.243:51136`. Esta nota prevalece sobre la observación histórica incluida abajo.

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

- Directorio operativo confirmado: `C:\Servicios\FileXferPruebas`.
- Nombre del servicio confirmado: `FileXferPruebas.Server`.
- Puerto HTTPS interno de Kestrel: `51136`.
- Puerto HTTPS público autorizado posteriormente: `51237`, traducido por NAT hacia `192.168.2.243:51136`; `51236` pertenece a `FileXferService`.
- Conexiones confirmadas en el servicio de pruebas: `FileXferDev` en 192.168.2.3\SQL2014 y `Budi_Dev` en 192.168.2.8.
- Ruta de almacenamiento UNC existente.
- Configuración JWT, límites, políticas y parámetros operativos válidos.

La primera inspección se hizo por error sobre `FileXferService`, que corresponde a otra instalación y apunta a `BUZONDIGITAL`. El 27/10/2026 se confirmó directamente que el destino correcto es `FileXferPruebas.Server`, ejecutado desde `C:\Servicios\FileXferPruebas`, con `FileXferDev`, `Budi_Dev`, `\\192.168.2.21\DocumentosBuzonDigital_Pruebas\Transfer\Finalizados` y Kestrel en `51136`. La liberación debe reemplazar únicamente ese servicio de pruebas; `FileXferService` queda fuera del alcance.

Los valores secretos no se documentan en este informe.

## Hallazgos que no deben migrarse

1. Dos PFX se encuentran dentro del directorio publicado. Los certificados con llave privada no deben viajar en el ZIP de aplicación.
2. La configuración combina IIS, Windows Service y un runtime autocontenido. La nueva liberación establece Windows Service como único modelo; ejecutar ambos causaría conflicto de puerto.
3. Existen copias duplicadas de `UPDATES` y `wwwroot/UPDATES` con instaladores históricos.
4. El manifiesto antiguo anuncia la versión `2.0.1.2` por HTTP y no contiene SHA-256 ni firma criptográfica.
5. El instalador histórico `TransferClienteASE-setup.exe` no posee firma Authenticode.
6. El certificado `CER.cer` es una e.firma personal; no es una raíz SAT ni un certificado TLS del servidor.
7. La publicación no contiene `Security:PathEncryptionKey`. La llave original se recuperó únicamente para comprobar y respaldar los 55 ensayos AES; esos datos fueron retirados de forma recuperable el 27/10/2026.
8. Tampoco contiene raíces SAT ni una llave persistente de recibos con el contrato actual. Las autoridades públicas AC5/AC6/AC7 se incorporaron posteriormente al paquete endurecido.
9. El PFX del ZIP recibido era autofirmado para `filexfer.local`. El servicio activo usa otro certificado para `transfer-pruebas.ase-sinaloa.local`, con SAN para `SERVERDIG1`, 192.168.2.243 y 187.141.128.194; sigue siendo autofirmado y debe sustituirse por uno corporativo antes de producción.
10. La llave JWT legada tiene menos de 32 caracteres. El migrador la rota por una llave aleatoria robusta; las sesiones activas deberán autenticarse nuevamente una vez.
11. Las conexiones `FileXferDev` y `Budi_Dev` desactivan cifrado y confían en certificados no validados. El runtime endurecido las rechaza en producción; la migración incorpora una excepción temporal limitada a esas dos bases y a IPv4 privadas.

## Inventario de cifrado de rutas del 27/10/2026

Una consulta inicial sobre `Budi_Dev.dbo.Transfer_ArchivosLocales`, sin extraer rutas, confirmó 8,490 registros: 55 con formato AES-GCM `v2:` y 8,435 con formato heredado sin prefijo.

En ese inventario inicial se necesitaban `Security__PathEncryptionKey` para las 55 rutas v2 y `Security__LegacyPathEncryptionKey` para las 8,435 rutas DES históricas. Después del saneamiento, solo la segunda permanece obligatoria.

La llave DES proporcionada por el propietario permitió descifrar 8,434 de las 8,435 rutas heredadas; una entrada histórica (ID 61918) tiene un valor anómalo. Una muestra de 500 se volvió a cifrar y coincidió exactamente con los datos almacenados. No se incorporó la llave al repositorio ni a los artefactos.

La llave AES original se recuperó del almacén de secretos de desarrollo y descifró correctamente las 55 rutas `v2:`: 55 válidas y 0 fallidas. Se confirmó que pertenecían exclusivamente a pruebas de Juan Manuel Carrillo Rojo del 20 al 22/10/2026, que no existían archivos físicos y que los únicos 55 recibos del sistema correspondían al mismo conjunto.

El 27/10/2026 se archivaron y retiraron transaccionalmente los 55 registros BuDi, 55 cargas y 55 recibos. Se conservaron siete eventos de auditoría. Las tablas recuperables son `Transfer_ArchivosLocales_Archive_20261027_AesTests`, `Uploads_Archive_20261027_AesTests`, `UploadReceipts_Archive_20261027_AesTests` y `ReceiptChainStates_Archive_20261027_AesTests`. La verificación posterior confirmó cero rutas `v2:`. Por ello `Security:PathEncryptionKey` queda opcional mientras `PathWriteFormat=LegacyDes` y no reaparezcan rutas AES. La llave DES compartida sigue siendo obligatoria.

## Decisiones aplicadas a la liberación nueva

- Migrador explícito de `appsettings.json` a `appsettings.Production.json`.
- Conservación de conexiones, almacenamiento, parámetros operativos y `51136`.
- Rotación controlada del JWT únicamente cuando la llave histórica sea inexistente o débil; no afecta archivos ni rutas cifradas.
- Excepción SQL de pruebas explícita y restringida; no acepta bases productivas, LocalDB, DNS arbitrario ni direcciones públicas.
- Eliminación del contrato `Actualizaciones` y de metadatos incompletos.
- Prohibición de generar silenciosamente una llave de rutas distinta.
- Escritura explícita en formato DES heredado para que los otros sistemas BuDi puedan leer las rutas nuevas con la misma llave histórica. Si reaparece una ruta AES `v2:`, el servidor la rechaza de forma explícita cuando no exista llave AES.
- Generación controlada de una llave RSA persistente para recibos cuando no existe.
- TLS desde el almacén `LocalMachine\My`, sin PFX dentro del paquete.
- Cadena SAT local AC5/AC6/AC7 obligatoria; OCSP/CRL queda deshabilitado por decisión operativa hasta contar con conectividad oficial. El bypass productivo permanece bloqueado.
- Instalación con staging, respaldo, comprobación de salud y rollback.
- Verificación de que IIS/Kestrel anterior no mantenga ocupado el puerto.
- Canal de cliente exclusivamente HTTPS, con hash y firma RSA-PSS-SHA256.
- Activación de actualizaciones diferida por defecto: el actualizador anterior no usaba firma ni llave histórica. El manifiesto queda pendiente hasta distribuir una actualización puente que instale la nueva llave pública.

## Bloqueadores externos pendientes

La ingeniería queda preparada, pero una liberación no puede marcarse productiva hasta disponer de:

1. certificado Authenticode y timestamp RFC 3161;
2. certificado TLS confiable para la identidad pública definitiva;
3. distribución corporativa/manual de la actualización puente del cliente;
4. OCSP/CRL oficial si se decide elevar la validación local a revocación en línea;
5. confirmación de permisos efectivos de la cuenta `Ase-sinaloa\juan.carrillo` —o la cuenta administrada que la sustituya— sobre la ruta UNC.

Ninguno de estos faltantes se resuelve de manera segura desactivando validaciones. Eso sería hacer desaparecer la alarma, no el incendio.
