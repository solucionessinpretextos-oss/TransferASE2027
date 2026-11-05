# Dashboard: consulta, estadísticas y alertas — 5 de noviembre de 2026

## Cambios de la actualización del servidor 2.2.1.11

- Solo consulta (`ops`): puede leer políticas e historial, pero no crear, editar o restaurar versiones. También quedan bloqueadas las operaciones de mantenimiento, reinicio de estadísticas y comprobación de escritura. El directorio de accesos RFC se limita a administradores.
- El servidor exige rol administrador y autenticación e.firma para las operaciones administrativas; la interfaz no es la única barrera.
- Se corrige HTTP 500 al reiniciar estadísticas. La validación de un record posicional debe estar en sus parámetros del constructor; con atributos dirigidos a propiedades MVC fallaba antes de entrar a la acción. El motivo inválido ahora devuelve 400 y el válido 200, con registro de auditoría.
- Diagnóstico incorpora **Entrega de alertas**: paginación, filtro, canal, evento, archivo cuando existe, RFC, intentos fallidos, error, remedio e identificadores. No devuelve el cuerpo interno de las alertas ni secretos.
- La alerta observada agotó tres intentos SMTP al notificar **Login SAT fallido** por certificado fuera de vigencia. No corresponde a una subida de archivo. El host activo era un valor de ejemplo.
- SMTP soporta TLS implícito en 465 y exige STARTTLS en otros puertos. Se conserva la validación de certificados. El cliente usa [MailKit y sus opciones TLS explícitas](https://mimekit.net/docs/html/M_MailKit_MailService_ConnectAsync_2.htm).

## Evidencia de pruebas

- TypeScript: sin errores; Dashboard: 65 pruebas aprobadas y compilación completada.
- Prueba HTTP con los controladores reales: 11 escrituras administrativas devuelven 403 para `ops`; directorio RFC reservado; e.firma obligatoria. Reinicio válido 200, motivos inválidos 400, auditoría y avisos anteriores preservados.
- Regresión reproducida: con los atributos anteriores, el mismo ensayo HTTP devuelve 500; con la corrección pasa.
- Pruebas de monitoreo y suite de seguridad aprobadas, incluyendo outbox durable, reintentos, políticas, recibos, descargas y actualización firmada.
- Consulta de vulnerabilidades NuGet, incluyendo dependencias transitivas del servidor: sin avisos reportados en la consulta realizada.
- Conexión de diagnóstico desde el equipo de desarrollo a `mail.ase-sinaloa.gob.mx:465`: TLS 1.2 y certificado válidos; no se autenticó la cuenta ni se envió correo.
- La revisión visual automatizada quedó sin completar: la herramienta de Windows detuvo la interacción porque no pudo identificar con certeza la URL del navegador. No se presenta como prueba visual aprobada.

## Activación SMTP pendiente

Remitente y destinatario autorizados: `juan.carrillo@ase-sinaloa.gob.mx`; host `mail.ase-sinaloa.gob.mx`; puerto 465. La contraseña debe capturarse en el servidor con `deployment/Configure-FileXferSmtp.ps1` después de publicar. El script valida TLS/autenticación sin enviar correo y protege el archivo de configuración local mediante permisos Windows. No incorpora la contraseña al repositorio.

Los avisos en DeadLetter no se reenvían al cambiar SMTP ni al reiniciar estadísticas. Requieren reproceso controlado autorizado; hasta resolverlos, el indicador de alertas puede seguir degradado. No se eliminó su evidencia para subir artificialmente la salud.

Esta actualización se limita al servidor y al Dashboard del servicio de pruebas. No requiere modificar el cliente ni su canal de actualización. Los paquetes de esta versión son para actualizar la instalación existente, conservando su configuración, certificados, llaves y manifiesto del cliente.
