# Adquisición, caudal y memoria

Trazabilidad: R03–R07, R10, R13; ADR 0002, [ADR 0005](../adr/0005-initial-solution-structure.md),
[ADR 0006](../adr/0006-session-window-lifecycle.md) y [ADR 0015](../adr/0015-authoritative-inspection-data-store.md).
El store autoritativo RAM de Fase 1 está integrado con `ApplicationSession` y con A/B. B-Scan no
recibe ni conserva muestras desde adquisición: recibe identidad y revisión confirmadas y vuelve a
leer la autoridad mediante providers.

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

Flujo objetivo por fases: fuente → `ApplicationSession` → `IInspectionDataWriter` → store →
`IUtTraceProvider`/`IInspectionRangeProvider` → entregas visuales. `ApplicationSession` presta la
memoria fuente solo hasta completar `AppendAsync`; el store reserva presupuesto y copia/adquiere
ownership antes de completarlo. La visualización latest-only sucede después del commit. En Fase 1 RAM,
`WasAccepted` indica que el append fue admitido antes del cierre, `IsCommitted` que quedó visible
atómicamente y `Durable` es siempre false.

Hay un writer por run y lectores concurrentes. Commit de traza es todo-o-nada; identidad confirmada no
se sobrescribe. Duplicate, Conflict, CapacityExceeded, Cancelled y Faulted son resultados explícitos.
Cancelación precommit revierte; después del commit no deshace datos.

Flujo actual implementado: simulador → Channel → ApplicationSession (único lector) → commit autoritativo.
Tras `Committed`, A y B reciben solo `TraceIdentity`+revisión. A lee mediante `IUtTraceProvider`; B
reconcilia estado mediante `IInspectionStreamStateProvider` y lee páginas de hasta 256 posiciones con
`IInspectionRangeProvider`. Ambos proyectan bajo leases retenidos solo en Visualization.Core. Véanse
[ADR 0013](../adr/0013-bscan-bounded-live-visualization.md) y su refinamiento en
[ADR 0015](../adr/0015-authoritative-inspection-data-store.md).

ApplicationSession no presta muestras a las ramas visuales. `AScanVisualDelivery` retiene como máximo
una solicitud de identidad. B-Scan lee lotes de 256 posiciones como máximo, reduce cada lease a
extremos derivados y libera la página antes de continuar. Los owners raster pertenecen a
Visualization.Core; Presentation y WPF no reciben leases RF.

El presupuesto de reducción es configurable entre 4 y 1.024 puntos; conserva primero/último y extremos min/max de todos los grupos interiores en orden temporal. La publicación se limita a 30 Hz con TimeProvider monotónico, sin recuperar ticks. Ese límite no acota necesariamente el coste de proyección, que se paga por entrada interesada. Medir CPU, asignaciones, GC y backpressure y valorar una optimización previa al mailbox sigue pendiente.

`SessionSnapshot.VisualError` mantiene compatibilidad como primer error visual, pero A y B tienen
admisión, error y continuidad independientes. Un fallo de A no impide intentar B, ni a la inversa;
ambos se agregan con códigos estructurados sin alterar el commit autoritativo.

La futura distribución tendrá ramas separadas:

| Rama futura | Política aprobada |
| --- | --- |
| Procesamiento | Sin pérdida silenciosa; saturación, discontinuidades y fallos observables |
| Almacenamiento | Sin pérdida silenciosa; confirmaciones y recuperación conforme al ADR 0003 |
| Visualización | Latest-only de snapshots independientes; sustitución contabilizada exclusivamente en esta rama |
| Métricas | Muestreo de publicación/observación; no exige consumir cada payload |

Este cuadro fija políticas, no decide si almacenamiento recibe datos crudos, procesados o ambos, ni el orden entre procesamiento y persistencia (Q06/Q13). Sin pérdida silenciosa no significa garantía de cero pérdida ante saturación o fallo.

Los enlaces asíncronos de frames usan canales acotados con presupuesto tanto de elementos como de bytes;
la notificación visual actual es sample-free y sus mailboxes coalescen identidad/revisión. Incluir colas,
ensamblados parciales, buffers en procesamiento, leases y caches en el límite total. Presupuestar el
tiempo de absorción como bytes disponibles / exceso de bytes por segundo.
Un ChannelReader no se comparte entre consumidores competidores: dividirían frames. Application conserva
el único lector, confirma en el store y notifica A/B después del commit. Los leases de A/B protegen la
lectura autoritativa durante la proyección; no existe copia RF propietaria de B. Procesamiento y
persistencia futuros requerirán ramas separadas sin pérdida silenciosa y mantienen abierta su estrategia
de ownership.

La retención RF pertenece exclusivamente al store autoritativo. B mantiene sólo reducciones e
índices derivados bajo el presupuesto visual y coalesce notificaciones como estado latest-only:
última revisión objetivo, un worker y como máximo una solicitud pendiente. La publicación neutral y
la aplicación al bitmap se limitan a 30 Hz hasta 512 trazas y a 10 Hz por encima. Versiones saltadas,
cambio de revisión entre páginas o cambio de firma invalidan el candidato y solicitan una
resincronización full coalescida.

Cuando la ventana live está llena, Core produce una columna neutral por traza visible para cualquier tamaño admitido hasta 1.024. Las revisiones compatibles rasterizan solo las columnas entrantes y publican un desplazamiento horizontal entero; WPF desplaza el bitmap y escribe la franja derecha. El escalado nearest-neighbor ocupa el ancho físico sin introducir columnas negras. Cambiar la capacidad visible en live actualiza de forma coherente capacidad, viewport y petición antes de notificar layout, cancela trabajo geométricamente obsoleto y permite una sola recuperación full; después vuelve al desplazamiento incremental. Esta prioridad impide que el raster prive de atención a Stop, cierre o entrada del usuario.

El intervalo representado por un snapshot incremental describe únicamente su trabajo dirty. Selección, hit testing e identidades se validan contra el rango residente completo capturado por el store. Tras Stop, el run se sella y el store permanece de solo lectura: B conserva zoom, pan, resize y selección exacta, y A conserva la última señal live durante las fases de parada y en Idle. El siguiente run retira ese estado congelado; el cierre global libera sus recursos.
Memoria UT contigua reutilizable; evitar asignaciones innecesarias en adquisición. La implementación visual actual sí asigna puntos propios por entrada interesada; su coste está pendiente de benchmark. El contenido publicado es inmutable. Un propietario devuelve el buffer exactamente una vez; no reutilizarlo mientras un consumidor lo conserva. `Span<T>` no atraviesa esperas asíncronas; `Memory<T>` exige vida útil documentada.
ADR 0007 define ownership del frame y adquisición; la revisión de ADR 0008 sustituye para esta rama visual su antigua transferencia a mailbox/coordinador por préstamo síncrono y liberación en Application. Frame `ReadOnlyMemory<short>` RF bipolar; representación binaria de rectificadas reales abierta. ChannelCapacity=4 y BufferCount=8 son configurables internos del simulador sujetos a benchmarks, no propiedades del contrato general. Cancelar, fallar, descartar o drenar libera todos los buffers implicados. Los observadores lentos no retienen muestras UT. El snapshot A-Scan sí sobrevive a devolver y sobrescribir el buffer UT porque posee datos independientes. Presupuestar pendiente, actual, proyección en curso y estado por suscriptor; no afirmar 30 asignaciones/s ni cuatro snapshots como límite demostrado.

En live convencional, `ApplicationSession` confirma primero el store y luego notifica de forma
sample-free a A y B, aislando sus fallos. `BScanVisualDelivery` publica regiones latest-only;
`BScanViewModel` conserva el owner y App.Wpf copia por filas al `WriteableBitmap`. Cerrar una ventana B
retira sólo su consumidor.

El remuestreo B-Scan propuesto no altera ese flujo ni el store. Tras la formación B específica se
separarán intensidad neutral, máscara `NoData` autoritativa y procedencia; un servicio 2D neutral podrá
remuestrear antes de la LUT. Modo, parámetros, geometría fuente/destino y política de máscara formarán
parte de la firma. Los kernels no cruzarán segmentos ni tratarán el sentinel cero como intensidad. Un
cambio de firma cancelará el candidato y causará una sola recuperación full; los incrementales deberán
incluir halo o degradar explícitamente a full. Véanse [ADR 0014](../adr/0014-bscan-selectable-resampling.md)
y su [plan](../plans/2026-09-29-bscan-interpolation-modes.md).

GP/Kriging no forma parte de esta etapa visual: estimaría datos ausentes, exigiría coordenadas físicas,
incertidumbre y procedencia `SpatialEstimated`, y tendrá una rama/módulo posterior si se aprueba. La
selección B→A continúa consultando el rango residente y el proveedor de trazas adquiridas, nunca el
raster remuestreado o reconstruido.

## Saturación e integridad
Visualización sustituye snapshots independientes pendientes mediante latest-only y cuenta esas sustituciones; no descarta frames del Channel de adquisición. Las futuras ramas de procesamiento y almacenamiento no descartan silenciosamente. Si el dispositivo permite pausar de forma segura, la coordinación puede usarlo; si se desconoce, no usar bloqueo del receptor como política de backpressure; emitir fallo observable y coordinar parada conforme a capacidades. Saturación/disco lleno produce estado de fallo observable, discontinuidad registrada y parada conforme a capacidades reales.
Acotar ventana y plazo de reordenación/ensamblado. Detectar pérdidas, duplicados, overrun, paquetes fuera de orden e imágenes incompletas. Decidir si estas últimas se rechazan o conservan marcadas; nunca tratarlas como completas.
Cerrar sólo BScanWindow cancela su ViewModel/solicitudes y retira su consumidor temporal sin detener
sesión. El cierre de run B es monotónico: `Open → AdmissionClosed → Draining → Frozen → Disposed`.
`StopAdmission` no fuerza leases ni callbacks admitidos; `WaitForDrainAsync` espera reconciliación,
páginas, reducción, raster, candidatos y callbacks; `FreezeRun` conserva navegación sobre la revisión
sellada. Cinco segundos siguen siendo sólo diagnóstico.

Métricas: bytes/frames recibidos y confirmados; pérdidas y descartes por causa; leases RF activos y
máximos; bytes visuales; y tiempos separados de lectura, reducción, raster, interpolación y commit.
Los contadores de integridad se actualizan por evento. La publicación de métricas a 5 Hz sigue
pendiente; la saturación sostenida no se resuelve aumentando indefinidamente memoria.

Un callback admitido puede terminar durante `Draining`, pero ninguno comienza tras completar esa
barrera. Cerrar MainWindow bloquea nuevas ventanas B, drena sesión y entregas, y después libera store,
coordinadores y Host.

Las referencias históricas de ADR 0007 sobre owners en el mailbox visual quedan sustituidas por ADR 0008 para esta rama. ADR 0006 y la arquitectura general ya reflejan el ciclo de vida y la composición A/B actuales.

Para Fase 1, la admisión se limita por bytes realmente poseídos y nunca usa drop ni expulsión. Si no
puede reservar una traza completa, aplica backpressure cancelable o devuelve `CapacityExceeded`.
El cierre bloquea starts, detiene fuente, drena, cierra admisión, resuelve appends, sella chunks/run,
espera commits y leases y libera. Un timeout solo diagnostica. Disco lento y persistencia siguen
abiertos para fases posteriores.

Payload puro por stream: 2.048 muestras de 16 bits son 4.096 B/traza y 0,2048/0,4096/4,096 MB/s a
50/100/1.000 trazas/s; 65.535 son 131.070 B y 6,5535/13,107/131,07 MB/s. Multiplicar por streams solo
para una configuración concreta validada. Las tablas completas están en ADR 0015.
