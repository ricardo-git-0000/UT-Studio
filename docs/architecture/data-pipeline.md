# Adquisición, caudal y memoria

Trazabilidad: R03–R07, R13; ADR 0002, [ADR 0005](../adr/0005-initial-solution-structure.md) y [ADR 0006](../adr/0006-session-window-lifecycle.md). Sección vertical y políticas de ramas aprobadas el 2026-09-09; mecanismos iniciales aceptados el 2026-09-11 en [ADR 0007](../adr/0007-frame-source-ownership.md) y [ADR 0008](../adr/0008-latest-only-visual-delivery.md). Sin código ni protocolos reales.

Fuentes GigE, PCIe, simulador y reproducción entregarán datos bajo el mismo contrato propio. El adaptador PCIe encierra el SDK y sus reglas de memoria. Separar canal físico, elemento, beam y ley focal; no inferir leyes del número de elementos.

## Presupuesto por configuración
Payload PA: bytesPorImagen = leyesFocales × muestrasPorAScan × 2.
Payload por segundo = bytesPorImagen × imágenesPorSegundo.
Convencional: sumar A-Scans/s × muestras × 2 de todos los flujos activos. Determinar si la tasa anunciada es agregada o por flujo.
100 MHz describe muestreo, no tasa sostenida de imágenes. Usar cálculos de 64 bits comprobados; incluir transporte, metadatos, procesamiento, disco y margen medido antes de admitir una configuración.

| Ejemplo hipotético | Bytes por imagen/A-Scan | MB/s | Mbit/s |
| --- | ---: | ---: | ---: |
| Convencional, 65.535 muestras, 1.000 A-Scans/s totales | 131.070 | 131,07 | 1.048,56 |
| PA, 128 leyes, 2.048 muestras, 200 imágenes/s | 524.288 | 104,8576 | 838,8608 |
| PA, 256 leyes hipotéticas, 65.535 muestras, 200 imágenes/s | 33.553.920 | 6.710,784 | 53.686,272 |

Unidades decimales: MB = 10^6 bytes. GigE nominal representa 1.000 Mbit/s antes de overhead: el primer caso ya lo excede; el segundo requiere medición y margen. El tercero NO establece 256 como límite de leyes. Ocho frames de ese tamaño retienen 268.431.360 bytes de payload, sin contar otras etapas.

## Flujo y propiedad

Primer incremento aprobado: simulador -> sesión en Application -> A-Scan neutral de Visualization.Core -> ViewModel de Presentation -> ventana WPF. Application consume la fuente mediante Contracts; Presentation coordina la transformación visual neutral sin añadir una dependencia de Application hacia Visualization.Core. No se introduce SignalProcessing ni persistencia en este incremento.

El flujo inicial tiene un único consumidor del canal de adquisición y entrega visual latest-only: se conserva el dato más reciente pendiente de visualizar, sustituyendo y liberando el anterior. La actualización UI tiene frecuencia limitada e independiente de adquisición. Cerrar MainWindow solicita salida; no se mantiene la aplicación funcionando sin ventanas. Sin interés visual temporal se consume/libera sin acumular. Application publica en mailbox de capacidad uno; coordinador único Presentation toma ownership, transforma fuera de UI y libera antes de esperar Dispatcher. Snapshot visual con memoria independiente, sin ownership compartido UT.

La futura distribución tendrá ramas separadas:

| Rama futura | Política aprobada |
| --- | --- |
| Procesamiento | Sin pérdida silenciosa; saturación, discontinuidades y fallos observables |
| Almacenamiento | Sin pérdida silenciosa; confirmaciones y recuperación conforme al ADR 0003 |
| Visualización | Latest-only; sustitución de datos antiguos con liberación y contabilidad de descartes |
| Métricas | Muestreo de publicación/observación; no exige consumir cada payload |

Este cuadro fija políticas, no decide si almacenamiento recibe datos crudos, procesados o ambos, ni el orden entre procesamiento y persistencia (Q06/Q13). Sin pérdida silenciosa no significa garantía de cero pérdida ante saturación o fallo.

Cada enlace usa canal acotado con presupuesto tanto de elementos como de bytes. Incluir colas, ensamblados parciales, buffers en procesamiento, leases y cachés en el límite total. Presupuestar el tiempo de absorción como bytes disponibles / exceso de bytes por segundo.
Un ChannelReader no se compartirá entre consumidores competidores: dividirían los frames, no harían broadcast. La distribución futura será explícita y con ramas separadas. La estrategia exacta de ownership compartido (por ejemplo, leases o copias) queda pendiente hasta introducir más de un consumidor y deberá resolverse antes de ese cambio; ninguna alternativa está seleccionada.
Memoria contigua reutilizable; evitar arrays por ley y asignaciones por frame. El contenido publicado es inmutable. Un propietario devuelve el buffer exactamente una vez; no reutilizarlo mientras un consumidor lo conserva. Span<T> no atraviesa esperas asíncronas; Memory<T> exige vida útil documentada.
ADR 0007 define ownership inicial en consumo, sustitución, cancelación y fallo. Frame `ReadOnlyMemory<short>` RF bipolar; representación binaria de rectificadas reales abierta. ChannelCapacity=4 y BufferCount=8 son configurables internos del simulador sujetos a benchmarks, no propiedades del contrato general. Cancelar, fallar, descartar o drenar libera todos los buffers implicados. La UI lenta no puede retener sin límite datos de adquisición, ni el modelo A-Scan sobrevivir a la validez de su memoria de respaldo.

## Saturación e integridad
Visualización sustituye frames antiguos mediante latest-only, liberándolos y contabilizando descartes. Las futuras ramas de procesamiento y almacenamiento no descartan silenciosamente. Si el dispositivo permite pausar de forma segura, la coordinación puede usarlo; si se desconoce, no usar bloqueo del receptor como política de backpressure; emitir fallo observable y coordinar parada conforme a capacidades. Saturación/disco lleno produce estado de fallo observable, discontinuidad registrada y parada conforme a capacidades reales.
Acotar ventana y plazo de reordenación/ensamblado. Detectar pérdidas, duplicados, overrun, paquetes fuera de orden e imágenes incompletas. Decidir si estas últimas se rechazan o conservan marcadas; nunca tratarlas como completas.
Cerrar MainWindow solicita salida; futuras secundarias liberan suscripciones sin detener sesión. Application cancela inmediatamente productor y sus esperas de escritura mientras el único consumidor drena/libera. Productor completa writer al salir; observar ambos, vaciar mailbox y esperar buffers devueltos antes de desconectar y detener/liberar Host. En el primer incremento no hay persistencia que drenar. Cuando exista, se incorporará drenaje y cierre recuperable; los detalles de durabilidad siguen abiertos. Timeout de cinco segundos solo diagnóstico: MainWindow permanece abierta y continúa limpieza observada, sin aborto forzado.

Métricas: bytes/frames recibidos y, cuando exista almacenamiento, persistidos; pérdidas y descartes por causa, ocupación en bytes, buffers vivos, latencia y velocidad de escritura. Los contadores de integridad se actualizan por evento; el muestreo afecta a su publicación/observación, no elimina eventos del recuento ni añade un lector competidor de frames. Métricas a 5 Hz; visual máximo 30 Hz y min/max a un máximo de 1.024 puntos según ADR 0008. La saturación sostenida no se resuelve aumentando indefinidamente memoria.
