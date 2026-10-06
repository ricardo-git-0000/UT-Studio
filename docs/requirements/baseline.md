# Línea base de requisitos

Fuente: [contexto inicial](../../UT-STUDIO-CONTEXT.md). Los ID siguientes permiten trazar decisiones y pruebas sin sustituir el documento fuente. “Producto v1” y “fase documental actual” son alcances distintos.

| ID | Requisito del producto | Verificación prevista |
| --- | --- | --- |
| R01 | C#/.NET 10, Windows/WPF, MVVM; núcleo y ViewModels independientes de UI | Grafo de dependencias y pruebas de arquitectura |
| R02 | Servicios para casos de uso; interfaces por constructor; stores observables; Messenger solo UI | Pruebas de coordinación y revisión de referencias |
| R03 | Fuentes GigE, PCIe encapsulado, simulador y reproducción con contrato común | Suite de conformidad por fuente |
| R04 | UT convencional y PA; hasta 256 canales físicos o elementos, 65.535 muestras de 16 bits, RF/rectificada, muestreo hasta 100 MHz | Límites y unidades; IDs distintos de canal, elemento, beam y ley |
| R05 | Aproximadamente 1.000 A-Scans/s convencional y hasta 200 imágenes PA/s cuando sea viable; cada imagen PA contiene un A-Scan por cada ley focal activa | Validación de caudal por configuración; máximos no simultáneos |
| R06 | Canales acotados, memoria contigua y reutilizada; evitar arrays por ley y asignaciones por frame | Pruebas de carga, memoria y ownership |
| R07 | Detectar pérdidas de secuencia, overrun, imágenes incompletas y reordenación; almacenamiento sin pérdida silenciosa | Inyección determinista de fallos y contabilidad de frames |
| R08 | A/B/C/S/D-Scan y varias ventanas; UI limitada independientemente de adquisición, modelos neutrales | Pruebas de renderizado, descarte y vida de ventanas |
| R09 | C-Scan habitual 1 × 1 m, preparado para 3 × 10 m; tamaño y paso espacial distintos; C/B/D por tiles, caché acotada y multirresolución | Consultas de regiones y presupuesto de memoria |
| R10 | Inspecciones habituales de 15–29 GB; escritura incremental, lectura parcial, índices, recuperación, versión y previews | Interrupciones, lectura regional y conjuntos grandes generados fuera de Git |
| R11 | Importadores por interfaces/plugins; formatos externos por definir | Conformidad del importador cuando se priorice un formato |
| R12 | PDF en v1; modelo neutral separado del renderizador, biblioteca y plantilla abiertas | Pruebas del modelo y renderizado según requisitos futuros |
| R13 | Toda operación larga admite cancelación y cierre ordenado | Cancelación en cada etapa y ausencia de recursos retenidos |
| R14 | Datos reales fuera de Git; solo fixtures sintéticos pequeños documentados | Revisión del repositorio |

Fuera de v1 inicial: 3D, integración PLC Panasonic/Mewtocol, robot Universal Robots y encoders/sincronización avanzada. Reservar límites de adaptación, sin implementar SDK ni protocolos ahora. Linux/Avalonia son evolución posible, no compatibilidad del hardware garantizada.

## Alcance confirmado del primer incremento (2026-09-11)

Este alcance histórico introdujo RF bipolar `ReadOnlyMemory<short>`, sesión Application y A-Scan en MainWindow; esas capacidades y el cierre principal están implementados. Rectificadas y ventanas adicionales seguían entonces pendientes. Sin bandeja, OnExplicitShutdown, ejecución sin ventanas ni scopes genéricos. Diseño en [ADR 0006](../adr/0006-session-window-lifecycle.md), [ADR 0007](../adr/0007-frame-source-ownership.md) y [ADR 0008](../adr/0008-latest-only-visual-delivery.md).

## Alcance B-Scan confirmado (2026-09-22)

El primer B-Scan convencional usa RF bipolar, eje X por índice sintético adimensional y eje Y temporal.
Debe mantener memoria acotada, discontinuidades observables, selección exacta hacia A-Scan Live/Selected,
navegación temporal común y ventana secundaria cerrable sin detener sesión. Una selección confirmada
sobrevive a la expulsión de su columna; nueva run la invalida. Encoder, distancia física, archivo,
profundidad, gates y PA quedan fuera. Diseño en [ADR 0013](../adr/0013-bscan-bounded-live-visualization.md)
y ejecución en el [plan por fases](../plans/2026-09-22-bscan-first-increment.md).

## Remuestreo B-Scan aprobado e implementado (2026-09-29)

La petición vigente confirma como invariantes que los modos de presentación no modifican RF, store,
identidades, coordenadas ni selección exacta; `None` reproduce el comportamiento actual; `NoData` y
gaps permanecen autoritativos; la intensidad se procesa antes de LUT y los recursos/cálculos siguen
acotados y cancelables. `None`, `NearestNeighbor`, `Bilinear`, `Bicubic` y `Lanczos` están implementados
como remuestreo visual; `None` es el valor predeterminado. El selector UI y la preferencia versionada
también están implementados. Bilinear, Bicubic y Lanczos permanecen full-only; Lanczos se ofrece como
alta calidad con cadencia reducida. Los resultados de rendimiento son diagnósticos, no baseline ni
garantía. GP/Kriging no está implementado: continúa como reconstrucción avanzada futura, con geometría,
procedencia `SpatialEstimated`, incertidumbre y ADR propios. Véanse [ADR 0014](../adr/0014-bscan-selectable-resampling.md)
y el [plan](../plans/2026-09-29-bscan-interpolation-modes.md).

## Almacén autoritativo de inspección aprobado en Fase 0 (2026-10-05)

El flujo futuro tendrá una sola autoridad neutral entre `ApplicationSession` y los proveedores de
A/B/C/S/D. Controles, ViewModels y caches visuales no poseen la inspección; cerrar una ventana no
elimina datos. El store usa memoria, colas, chunks y leases acotados, escritura incremental, commits
atómicos, índice reconstruible y lectura parcial por identidad/rango. Distingue recepción, aceptación,
commit y durabilidad; almacenamiento no descarta silenciosamente y visualización puede ser latest-only.

El modelo separa `InspectionId`, `AcquisitionRunId`, fuente, canal físico, elemento, beam, ley focal,
`AcquisitionIndex`, `Sequence`, tiempo, posición, eje de muestra, RF/rectificada, gaps, procedencia y
calidad. No presupone cubo rectangular ni combina máximos como garantía. El diseño y sus decisiones
abiertas están en [ADR 0015](../adr/0015-authoritative-inspection-data-store.md) y el
[plan por fases](../plans/2026-10-05-inspection-data-store.md).

Fase 1 está implementada exclusivamente en RAM en `UTStudio.Inspection.Core`, con contratos en
`UTStudio.Contracts/Inspection` y sin `UTStudio.Inspection.Storage`. `TraceIdentity` incluye inspección,
run, fuente, canal físico, beam o ley focal opcionales y `AcquisitionIndex`; Sequence queda separado.
La memoria fuente se presta solo hasta completar `AppendAsync`, que reserva antes de copiar/adquirir
ownership y confirma cada traza todo-o-nada. `WasAccepted` indica admisión previa al cierre,
`IsCommitted` confirma visibilidad atómica y `Durable=false`; no hay expulsión, overwrite ni pérdida silenciosa. Capacidad insuficiente aplica
backpressure o `CapacityExceeded`. Lecturas usan lease inmutable o snapshot independiente.

## Aceptación de la fase documental inicial (histórico)
Configuración y once perfiles presentes; reglas de escritor único; documentación de requisitos, arquitectura y ADR enlazada; cinco áreas revisadas en paralelo por especialistas; pendientes explícitos; sin implementación, proyectos C#, paquetes ni commit.
