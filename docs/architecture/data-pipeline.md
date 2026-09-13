# Adquisición, caudal y memoria

Trazabilidad: R03–R07, R13; ADR 0002, [ADR 0005](../adr/0005-initial-solution-structure.md) y [ADR 0006](../adr/0006-session-window-lifecycle.md). Sección vertical y políticas de ramas aprobadas el 2026-09-09; mecanismos iniciales aceptados el 2026-09-11 en [ADR 0007](../adr/0007-frame-source-ownership.md) y [ADR 0008](../adr/0008-latest-only-visual-delivery.md). Revisado el 2026-09-13: fuente simulada, sesión y entrega A-Scan neutral implementadas; sin protocolos reales ni integración UI.

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

Flujo neutral implementado: simulador -> Channel -> ApplicationSession (único lector) -> IConventionalFrameSink.Accept síncrono -> proyección Visualization.Core -> snapshot independiente -> mailbox latest-only -> publicación observable. Application usa el puerto de Contracts/Presentation sin referenciar Visualization.Core. Presentation, ViewModels, WPF y composición DI siguen pendientes. No se introduce SignalProcessing ni persistencia.

ApplicationSession presta `ReadOnlySpan<short>` únicamente durante Accept. El sink no puede retener el span ni aliases a las muestras. Con interés visual, AScanVisualDelivery realiza reducción síncrona por entrada y crea datos propios; la sesión libera siempre el frame original en finally, también ante excepciones. Sin interés evita proyección y acumulación. El mailbox en Visualization.Core retiene solamente un snapshot independiente pendiente y sustituye el anterior. No retiene ConventionalUtFrame ni transfiere owners a Presentation.

El presupuesto de reducción es configurable entre 4 y 1.024 puntos; conserva primero/último y extremos min/max de todos los grupos interiores en orden temporal. La publicación se limita a 30 Hz con TimeProvider monotónico, sin recuperar ticks. Ese límite no acota necesariamente el coste de proyección, que se paga por entrada interesada. Medir CPU, asignaciones, GC y backpressure y valorar una optimización previa al mailbox sigue pendiente.

SessionSnapshot.VisualError informa del fallo de la rama visual sin convertirlo automáticamente en fallo de adquisición. La adquisición continúa drenando/liberando; los errores propios de adquisición y devolución de buffers mantienen su tratamiento independiente.

La futura distribución tendrá ramas separadas:

| Rama futura | Política aprobada |
| --- | --- |
| Procesamiento | Sin pérdida silenciosa; saturación, discontinuidades y fallos observables |
| Almacenamiento | Sin pérdida silenciosa; confirmaciones y recuperación conforme al ADR 0003 |
| Visualización | Latest-only de snapshots independientes; sustitución contabilizada exclusivamente en esta rama |
| Métricas | Muestreo de publicación/observación; no exige consumir cada payload |

Este cuadro fija políticas, no decide si almacenamiento recibe datos crudos, procesados o ambos, ni el orden entre procesamiento y persistencia (Q06/Q13). Sin pérdida silenciosa no significa garantía de cero pérdida ante saturación o fallo.

Los enlaces asíncronos de frames usan canales acotados con presupuesto tanto de elementos como de bytes; la entrada visual actual es síncrona y su mailbox de snapshots tiene capacidad uno. Incluir colas, ensamblados parciales, buffers en procesamiento, leases y cachés en el límite total. Presupuestar el tiempo de absorción como bytes disponibles / exceso de bytes por segundo.
Un ChannelReader no se compartirá entre consumidores competidores: dividirían los frames, no harían broadcast. La distribución futura será explícita y con ramas separadas. La estrategia exacta de ownership compartido (por ejemplo, leases o copias) queda pendiente hasta introducir más de un consumidor y deberá resolverse antes de ese cambio; ninguna alternativa está seleccionada.
Memoria UT contigua reutilizable; evitar asignaciones innecesarias en adquisición. La implementación visual actual sí asigna puntos propios por entrada interesada; su coste está pendiente de benchmark. El contenido publicado es inmutable. Un propietario devuelve el buffer exactamente una vez; no reutilizarlo mientras un consumidor lo conserva. `Span<T>` no atraviesa esperas asíncronas; `Memory<T>` exige vida útil documentada.
ADR 0007 define ownership del frame y adquisición; la revisión de ADR 0008 sustituye para esta rama visual su antigua transferencia a mailbox/coordinador por préstamo síncrono y liberación en Application. Frame `ReadOnlyMemory<short>` RF bipolar; representación binaria de rectificadas reales abierta. ChannelCapacity=4 y BufferCount=8 son configurables internos del simulador sujetos a benchmarks, no propiedades del contrato general. Cancelar, fallar, descartar o drenar libera todos los buffers implicados. Los observadores lentos no retienen muestras UT. El snapshot A-Scan sí sobrevive a devolver y sobrescribir el buffer UT porque posee datos independientes. Presupuestar pendiente, actual, proyección en curso y estado por suscriptor; no afirmar 30 asignaciones/s ni cuatro snapshots como límite demostrado.

## Saturación e integridad
Visualización sustituye snapshots independientes pendientes mediante latest-only y cuenta esas sustituciones; no descarta frames del Channel de adquisición. Las futuras ramas de procesamiento y almacenamiento no descartan silenciosamente. Si el dispositivo permite pausar de forma segura, la coordinación puede usarlo; si se desconoce, no usar bloqueo del receptor como política de backpressure; emitir fallo observable y coordinar parada conforme a capacidades. Saturación/disco lleno produce estado de fallo observable, discontinuidad registrada y parada conforme a capacidades reales.
Acotar ventana y plazo de reordenación/ensamblado. Detectar pérdidas, duplicados, overrun, paquetes fuera de orden e imágenes incompletas. Decidir si estas últimas se rechazan o conservan marcadas; nunca tratarlas como completas.
Cerrar MainWindow solicita salida; futuras secundarias liberan suscripciones sin detener sesión. Application cancela inmediatamente productor y sus esperas de escritura mientras el único consumidor drena/libera. La sesión cierra la entrega visual e invalida sus snapshots pendientes; una proyección concurrente no confirma resultados obsoletos. El productor completa writer al salir; observar productor y consumidor y esperar AllFramesReleased antes de desconectar y, en la futura integración, detener/liberar Host. El mailbox visual no contiene buffers UT. En el primer incremento no hay persistencia que drenar. Cuando exista, se incorporará drenaje y cierre recuperable; los detalles de durabilidad siguen abiertos. Timeout de cinco segundos solo diagnóstico: MainWindow permanece abierta y continúa limpieza observada, sin aborto forzado.

Métricas: bytes/frames recibidos y, cuando exista almacenamiento, persistidos; pérdidas y descartes por causa, ocupación en bytes, buffers vivos, latencia y velocidad de escritura. Los contadores de integridad se actualizan por evento; el muestreo afecta a su publicación/observación, no elimina eventos del recuento ni añade un lector competidor de frames. AScanDeliveryStatistics es actualmente una consulta bajo demanda con contadores visuales acumulados: Received cuenta llamadas a Accept; Published, confirmaciones del mailbox al store; Replaced, pendientes sustituidos. Dropped agrupa descartes y ObserverCoalesced/ObserverErrors separan coalescencia y fallos de observadores. No son contadores de persistencia ni número de callbacks. La publicación de métricas a 5 Hz y el refresco UI efectivo siguen pendientes. Los 30 Hz actuales acotan publicación y admisión de notificaciones, no necesariamente el inicio efectivo de OnNext. La saturación sostenida no se resuelve aumentando indefinidamente memoria.

Requisito confirmado de suscripciones: un callback que ya comenzó puede terminar después del cierre; no deben comenzar nuevos callbacks tras cancelar la suscripción o completar la entrega. Brecha actual: admisión bajo lock e invocación posterior de OnNext están separadas, por lo que uno ya admitido podría comenzar después de invalidar. La corrección y su prueba determinista quedan pendientes; véase [ADR 0008 revisado](../adr/0008-latest-only-visual-delivery.md). Esta tarea documental no cambia código. Cerrar MainWindow seguirá solicitando salida; la integración real de ese comportamiento continúa pendiente.

Las referencias antiguas de ADR 0006/0007 y arquitectura general sobre owners en el mailbox visual quedan sustituidas por ADR 0008 para esta rama; sincronizar esos documentos requiere otro alcance.
