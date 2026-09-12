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

Varias ventanas y rectificadas siguen siendo requisitos de producto, no capacidades de este incremento. Inicialmente RF bipolar `ReadOnlyMemory<short>`, sesión Application y A-Scan en MainWindow. Cerrar MainWindow solicita salida ordenada; secundarias futuras no detendrán sesión. Sin bandeja, OnExplicitShutdown, ejecución sin ventanas ni scopes genéricos. Diseño en [ADR 0006](../adr/0006-session-window-lifecycle.md), [ADR 0007](../adr/0007-frame-source-ownership.md) y [ADR 0008](../adr/0008-latest-only-visual-delivery.md); implementación pendiente según [plan](../plans/2026-09-09-first-vertical-increment.md).

## Aceptación de la fase documental inicial (histórico)
Configuración y once perfiles presentes; reglas de escritor único; documentación de requisitos, arquitectura y ADR enlazada; cinco áreas revisadas en paralelo por especialistas; pendientes explícitos; sin implementación, proyectos C#, paquetes ni commit.
