# Línea base operativa propuesta

Estado: **aprobación técnica inicial; requiere datos de staging y validación de Jurídico/Infraestructura antes de producción**.

## Decisiones recibidas

| Tema | Decisión / estado |
|---|---|
| Repositorio | Privado: `solucionessinpretextos-oss/TransferASE2027`; rama `main` publicada. |
| Entrega de alertas | Outbox SQL implementado en FileXferDb; SMTP es el canal elegido. |
| Antivirus/EDR | No disponible por ahora. Riesgo aceptado únicamente para preparación/staging; no equivale a análisis antimalware. |
| Volumen | Sin estimación. Se debe medir y ajustar antes de fijar capacidad productiva. |
| Staging | Servidor, DNS, SQL, BuDi y storage pendientes. |
| Ventana | Autorizada, pero faltan fecha, hora, duración y zona horaria concretas. |

## Retención recomendada

| Información | Retención propuesta | Eliminación |
|---|---:|---|
| Cargas parciales | 24 horas | Automática; ya implementada y limitada al parcial identificado. |
| Outbox entregado | 30 días | Automática; ya implementada. |
| Outbox dead-letter | 90 días | Automática; ya implementada. Debe resolverse antes, no ignorarse. |
| Logs operativos | 90 días en línea + 1 año archivado | Purga automatizada y auditable. |
| Auditoría de seguridad | 1 año en línea + 7 años archivada | Solo después de comprobar integridad y ausencia de legal hold. |
| Archivos completados, recibos y evidencia | 1 año en línea + 6 años en archivo (7 años total) | Sin purga automática hasta aprobación de Jurídico/Archivo. Luego: legal hold, cuarentena de 30 días, doble aprobación y acta de eliminación. |

La cifra de siete años es una línea base conservadora de ingeniería, no una interpretación legal. Si la tabla de retención documental institucional exige más tiempo, prevalece esa tabla. El costo es almacenamiento adicional; el beneficio es no destruir evidencia antes de conocer su clasificación.

## Continuidad recomendada

| Componente | RPO | RTO | Condición |
|---|---:|---:|---|
| FileXferDb y metadatos | 15 minutos | 2 horas | Backups cifrados, logs frecuentes y restauración probada. |
| Storage de archivos confirmados | 0 después del acuse del servidor | 4 horas | Storage redundante; el acuse no debe emitirse antes de persistencia durable. |
| BuDi | 15 minutos | 2 horas | Alinear con el SLA corporativo de SQL Server. |
| Dashboard/alertas | 15 minutos | 4 horas | El flujo de transferencia tiene prioridad sobre la interfaz administrativa. |

Estos valores son la propuesta de fase 1. Deben validarse mediante un ejercicio real de restauración; un RTO escrito sin cronómetro es literatura fantástica.

## Monitoreo recomendado

Usar OpenTelemetry en servidor y Collector separado, exportando OTLP sobre TLS. Mantener el backend desacoplado del código. Si no existe plataforma corporativa, usar inicialmente Grafana + Prometheus + Loki + Tempo en staging. Alertas mínimas:

- readiness en error, fallos SQL/storage y espacio libre;
- outbox cerca del umbral, dead-letter mayor que cero o persistencia fallida;
- tasa de cargas/descargas fallidas, hash inválido, archivo bloqueado, omisión de verificación y reparación fallida;
- latencia p95/p99, reinicios del servicio y expiración de certificados.

## Secretos recomendados

Para un despliegue Windows/on-premises, usar cuenta de servicio **gMSA** y autenticación integrada hacia SQL para eliminar contraseñas de base de datos. Guardar llaves privadas y secretos restantes mediante certificado no exportable del almacén de certificados de Windows y protección DPAPI ligada a la cuenta de servicio, con ACL mínima y rotación documentada.

Si staging confirma que el servicio se ejecutará en Azure, sustituir esta decisión por Azure Key Vault + identidad administrada. No guardar secretos en `appsettings`, variables versionadas, scripts ni argumentos de consola.

## Firma de binarios

Adquirir un certificado Authenticode organizacional de una CA pública o PKI empresarial con EKU Code Signing, llave privada no exportable y preferentemente HSM/token. Firmar cliente, updater y servidor con SHA-256 y sellado RFC 3161 también SHA-256. El pipeline ya exige huella y URL de timestamp para declarar `ProductionReady=true`; hasta recibirlos, las liberaciones son únicamente de staging.

## Riesgo temporal sin antivirus

La validación estructural, reparación PDF, listas de extensiones, límites, hashes y almacenamiento sin ejecución reducen riesgo, pero **no detectan malware**. Mantener ACL exclusiva de servicio, negar ejecución en el volumen, no abrir documentos en el servidor y registrar todos los bypass. Incorporar Defender/EDR o un escáner ICAP antes de producción es la recomendación; mientras no exista, el riesgo debe constar en la aceptación de salida.

## Datos aún necesarios para desplegar

- servidor Windows y cuenta de servicio/gMSA;
- DNS del API, Dashboard y descargas;
- instancia/base FileXferDb y conexión integrada cifrada;
- instancia/base BuDi y confirmación de TLS 1.2+;
- ruta/volumen de storage, capacidad, redundancia, ACL y backup;
- SMTP: host, puerto, STARTTLS/TLS, remitente, destinatarios y método de autenticación (sin enviar contraseñas por chat);
- fecha, hora, duración y zona horaria de la ventana;
- huella del certificado Authenticode y URL RFC 3161 cuando estén disponibles.

Sin esos datos se puede compilar y probar localmente, pero no es responsable ejecutar migraciones ni publicar staging.
