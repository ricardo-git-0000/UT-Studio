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
| Q09 | Renderizador, formato neutral de píxel, FPS y presupuestos de CPU/GPU/memoria | ut_visualization |
| Q10 | Framework de pruebas/mocking y plataforma de CI; hardware y duración de benchmarks | quality_reviewer |
| Q11 | Instalación, actualización y plataformas Linux realmente requeridas | solution_architect |
| Q12 | Sesión por aplicación/ventana, inspecciones concurrentes y semántica/hilo de snapshots | wpf_mvvm_specialist |
| Q13 | Visualización de datos recibidos o ya persistidos; bifurcación y orden exacto del pipeline | solution_architect |

Q01–Q04 condicionan la adquisición real. Q06 condiciona el formato persistente. Q08 condiciona la selección PDF. No incorporar NuGet antes de justificarlo y obtener aprobación.
