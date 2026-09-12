# Cuestiones abiertas

No asumir respuestas ni bloquear la fase documental por estas incógnitas. Resolverlas antes de implementar la parte afectada.

| ID | Cuestión / evidencia necesaria | Responsable |
| --- | --- | --- |
| Q01 | Paquetes GigE: framing, secuencias, reloj, orden, integridad, MTU y control de flujo | ut_acquisition |
| Q02 | API PCIe: documentación, capacidades, memoria/DMA, cancelación, errores, licencia y plataformas | ut_acquisition |
| Q03 | Leyes focales habituales y máximo operativo; muestras, PRF, simultaneidad convencional y configuraciones reales | ut_domain_specialist |
| Q04 | Política del equipo ante saturación; pausa/parada segura, buffers y margen de caudal medido | ut_acquisition |
| Q05 | Paso espacial por eje, geometría, coordenadas y sincronización posición-tiempo; significado operativo de D-Scan | ut_domain_specialist |
| Q06 | Formato nativo, bloques, compresión, integridad, durabilidad y ventana recuperable; crudo/procesado a conservar | ut_storage_formats |
| Q07 | Formatos externos prioritarios y archivos de ejemplo autorizados | ut_storage_formats |
| Q08 | Informe: contenido, PDF/A, firmas, imágenes, plantilla corporativa y licencia | pdf_reporting |
| Q09 | Renderizador y benchmarks CPU/GPU/memoria pendientes; A-Scan de puntos, máximo 1.024/min-max y 30 Hz aceptados (ADR 0008) | ut_visualization |
| Q10 | MSTest 4.0.2 aprobado/presente desde 2026-09-10; mocking, CI, hardware/duración de benchmarks y paquetes adicionales pendientes | quality_reviewer |
| Q11 | Instalación, actualización y plataformas Linux realmente requeridas | solution_architect |
| Q12 | Sesión Application, cierre MainWindow=salida y snapshots iniciales resueltos (ADR 0006/0008); inspecciones concurrentes y propiedad/presupuesto de primera secundaria pendientes, sin scopes genéricos ahora | wpf_mvvm_specialist |
| Q13 | Flujo inicial recibido -> Application -> latest-only resuelto (ADR 0007/0008); fan-out, ownership compartido, orden y relación con persistencia pendientes | solution_architect |

La representación binaria de rectificadas reales sigue abierta; inicialmente solo RF bipolar `ReadOnlyMemory<short>`. Véanse [ADR 0007](../adr/0007-frame-source-ownership.md), [ADR 0008](../adr/0008-latest-only-visual-delivery.md) y [ADR 0006 revisado](../adr/0006-session-window-lifecycle.md).

Q01–Q04 condicionan la adquisición real. Q06 condiciona el formato persistente. Q08 condiciona la selección PDF. No incorporar NuGet antes de justificarlo y obtener aprobación.
