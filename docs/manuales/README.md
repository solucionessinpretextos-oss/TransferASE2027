# Manuales oficiales de Transfer ASE 2027

Esta carpeta reúne la documentación operativa de **todos los proyectos del ecosistema**. Los manuales están escritos para que una persona pueda completar una tarea sin conocer la arquitectura interna y, al mismo tiempo, incluyen el detalle que necesita soporte para diagnosticar un problema sin improvisar.

## Qué manual corresponde a cada proyecto

| Proyecto o componente | Manual | Úselo cuando necesite |
|---|---|---|
| `TransferClienteASE` | [Manual de usuario](01-Manual-Usuario-TransferClienteASE.md) | Iniciar sesión con e.firma, localizar requerimientos, cargar, reanudar, consultar y descargar archivos. |
| `FileXfer.AdminDashboard` | [Manual del Dashboard](02-Manual-Administrador-Dashboard.md) | Interpretar KPI, cambiar vistas, revisar estados y administrar políticas por extensión. |
| `FileXfer.Server.Full` | [Manual de operación del servidor](03-Manual-Operacion-Servidor.md) | Configurar, desplegar, validar salud, monitorear y ejecutar rollback. |
| `TransferClienteASEUpdater` | [Manual del actualizador](04-Manual-Actualizador.md) | Construir, verificar, publicar y recuperar una actualización del cliente. |
| `FileXfer.Pdf` | [Manual técnico de validación](05-Manual-Tecnico-Validacion-Archivos.md) | Entender inspección, contraseñas, reparación PDF, políticas y resultados de seguridad. |
| Todos los proyectos `*.SmokeTests` | [Manual de pruebas automatizadas](06-Manual-Pruebas-Automatizadas.md) | Ejecutar pruebas de PDF, formatos, seguridad, reanudación, carga integral y staging. |

## Versiones listas para entregar

La carpeta [`entregables`](entregables/) contiene cada manual en **Word** y **PDF**. Los archivos Markdown anteriores son la fuente navegable desde GitHub y deben actualizarse primero para que las tres versiones conserven el mismo contenido.

## Convenciones que aparecen en los manuales

- **IMPORTANTE**: condición que debe verificarse antes de continuar.
- **RESULTADO ESPERADO**: evidencia visible de que el paso terminó correctamente.
- **SI ALGO FALLA**: acciones concretas y datos que deben conservarse.
- Las capturas usan datos de desarrollo y pueden cambiar; los nombres de botones y estados sí corresponden al producto documentado.
- Ningún manual contiene contraseñas, llaves privadas, tokens o secretos productivos.

## Regenerar los entregables

Desde la raíz del repositorio:

```powershell
& 'RUTA_AL_PYTHON_CON_PYTHON_DOCX' docs/manuales/_build/generate_manuals.py
```

Después se deben renderizar los seis archivos Word y revisar visualmente todas sus páginas. La presencia de un `.docx` no demuestra que sea legible; solo demuestra que existe, un estándar que ya ha causado suficiente sufrimiento en la industria.
