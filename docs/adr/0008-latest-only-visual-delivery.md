# ADR 0008 — Entrega A-Scan latest-only y publicación visual acotada

Fecha original: 2026-09-11. Revisado: 2026-09-16.
Estado: entrega neutral implementada; cancelación estricta por suscriptor y diagnóstico terminal independiente corregidos según la petición del usuario del 2026-09-15.
Trazabilidad: R01, R02, R06–R08 y R13; complementa [ADR 0005](0005-initial-solution-structure.md), [ADR 0006](0006-session-window-lifecycle.md) y [ADR 0007](0007-frame-source-ownership.md).

## Contexto y sustitución

La implementación conserva el ownership UT en Application y presta muestras síncronamente a la rama visual. Sustituye la propuesta anterior de transferir frames a un mailbox en Application y proyectarlos desde Presentation a 30 Hz. El mailbox actual contiene snapshots independientes en Visualization.Core; la proyección ocurre por entrada interesada y los 30 Hz limitan publicación, no necesariamente coste de proyección.

Esta revisión prevalece para la rama visual sobre las referencias anteriores a mailbox de frames, extracción de owners por Presentation y transformación a 30 Hz. ADR 0006/0007 y arquitectura general conservan referencias a esa propuesta; su sincronización queda pendiente fuera del alcance autorizado. Se mantienen las garantías de lector único, liberación UT y parada ordenada. La topología nueva no autoriza ajustar automáticamente ChannelCapacity=4 ni BufferCount=8 del simulador.

## Flujo implementado y ownership

`Simulador -> Channel -> ApplicationSession -> IConventionalFrameSink.Accept -> AScanProjector -> AScanSnapshot -> mailbox latest-only -> publicación observable`.

- ApplicationSession conserva el único lector de UtAcquisitionRun.Frames.
- La sesión recibe opcionalmente [IConventionalFrameSink](../../src/UTStudio.Contracts/Presentation/IConventionalFrameSink.cs); un puerto nulo conserva el comportamiento sin entrega visual. Application no referencia Visualization.Core.
- Accept recibe metadatos, secuencia, tiempo transcurrido y `ReadOnlySpan<short>` prestado durante la llamada síncrona. El sink no puede retener el span, aliases a su almacenamiento ni el frame propietario. Debe producir sus datos propios antes de retornar.
- ApplicationSession conserva el frame y lo libera en finally, también si falla la transformación o entrega. Ninguna espera de observadores conserva muestras UT.
- [AScanVisualDelivery](../../src/UTStudio.Visualization.Core/AScanVisualDelivery.cs) implementa el puerto y proyecta fuera del bloqueo del mailbox. Confirma el resultado solo si la ejecución y generación siguen vigentes. Sin suscriptores evita proyección y acumulación.
- El mailbox retiene solamente un snapshot independiente pendiente. Una entrada sustituye al pendiente anterior; descartarlo elimina una referencia visual, no devuelve un owner UT.
- Fuente y sink son prestados a la sesión. Esta abre/cierra la ejecución visual; sus propietarios externos disponen los servicios después. La integración Presentation/WPF existente se registra en el plan del incremento.

CloseRun invalida generación, pendiente y snapshot actual. Una proyección síncrona ya iniciada puede terminar, pero su resultado invalidado no entra al mailbox. La sesión mantiene drenaje y liberación, observa productor y consumidor y espera AllFramesReleased. La barrera no depende de callbacks visuales.

La pérdida latest-only se permite exclusivamente en la rama visual. No define ni condiciona la futura distribución a procesamiento o persistencia sin pérdida silenciosa.

## Snapshot, escalas y reducción

[AScanSnapshot](../../src/UTStudio.Visualization.Core/AScanSnapshot.cs) es inmutable y contiene puntos propios de tiempo/amplitud, metadatos de fuente/run/canal/configuración, secuencia, tiempo transcurrido y versión visual. Sobrevive a devolver y sobrescribir el buffer original. No contiene tipos gráficos ni memoria UT.

- Tiempo: `offset + i / fs`, segundos; etiquetas UI en microsegundos pendientes.
- RF: `100 × sample / 32768`, escala fija -100 % a +100 %. El máximo positivo Int16 queda ligeramente por debajo de +100 %.
- [AScanProjector](../../src/UTStudio.Visualization.Core/AScanProjector.cs) acepta presupuesto M entre 4 y 1.024 puntos, predeterminado 1.024. Si N <= M, conserva todas las muestras.
- Para N > M, conserva primero/último y divide las N - 2 muestras interiores en `floor((M - 2) / 2)` grupos contiguos casi iguales. Cada grupo emite mínimo/máximo en orden de índice original; empates por primer índice y un solo punto si ambos coinciden. Cubre también el último intervalo interior. Un presupuesto impar puede dejar un punto sin utilizar.
- N=1 conserva un dato y un viewport nominal de una muestra centrado en su tiempo. Se rechazan ejes no representables para visualización.

Reducción O(N), exclusivamente visual: sin DSP, medidas, profundidad, calibración a voltios ni representación definitiva de rectificadas. No se selecciona biblioteca gráfica.

## Cadencia, versiones y contadores

Cada entrada válida con interés visual se proyecta síncronamente antes del mailbox. No se afirma un máximo de 30 transformaciones o asignaciones por segundo.

La publicación del mailbox a Current se limita mediante TimeProvider monotónico a intervalos de al menos 333.334 ticks de TimeSpan (33,3334 ms), conservador respecto a 30 Hz. Se comprueba el tiempo tras despertar y no se recuperan ticks atrasados. La admisión de notificaciones por suscripción también tiene ese límite. Esto no garantiza todavía la separación entre comienzos efectivos de OnNext ni implementa el refresco de una futura UI.

La versión visual crece durante la vida del servicio para entradas admitidas a proyección; sustituciones y descartes pueden dejar huecos. No se renumera la secuencia UT original. Run y generación invalidan resultados anteriores al cierre.

[AScanDeliveryStatistics](../../src/UTStudio.Visualization.Core/AScanDeliveryStatistics.cs) ofrece contadores acumulados durante la vida del servicio, consultables bajo demanda:

| Contador | Significado |
| --- | --- |
| Received | Llamadas recibidas por Accept, incluidas las descartadas sin interés o por cierre |
| Published | Confirmaciones del mailbox al store actual; no número de callbacks |
| Replaced | Snapshots aún pendientes sustituidos en el mailbox |
| Dropped | Entradas/resultados descartados por ausencia de interés, invalidación, cierre o fallo; agrupa causas |
| ObserverCoalesced / ObserverErrors | Notificaciones pendientes agrupadas y fallos de observadores, separados de la sustitución del mailbox |

HasPending y LastError completan el diagnóstico. Los contadores de sesión y visuales tienen ámbitos distintos. La publicación periódica de métricas a 5 Hz sigue pendiente; no confundirla con la consulta actual bajo demanda.

## Suscripciones y requisito de cierre

Cada suscripción conserva como máximo un callback en ejecución y un snapshot pendiente reemplazable. Un observador lento no bloquea Accept, CloseRun ni el despacho de sus pares; uno que lanza se desuscribe y registra diagnóstico sin detener adquisición. Ningún código externo se ejecuta bajo el bloqueo global del publicador.

**Contrato corregido:** cuando Dispose de una suscripción retorna, no puede comenzar otra invocación de OnNext en ella. Seleccionar o extraer un snapshot no significa que el callback haya comenzado. Su comienzo es la llamada síncrona a OnNext, tras la comprobación final de vigencia y bajo una puerta reentrante exclusiva de ese suscriptor. La puerta se conserva durante la llamada externa.

Dispose marca la suscripción cancelada y elimina su pendiente bajo el bloqueo global; después lo libera y cruza la puerta del suscriptor. Si la cancelación gana antes de la comprobación final, se descarta el snapshot extraído. Si la invocación gana, Dispose externo espera a que termine antes de retornar. La autocancelación desde OnNext es reentrante: retorna y permite terminar esa misma invocación, pero ninguna posterior. Todas las cancelaciones concurrentes cruzan la barrera, también las repetidas.

Esto sustituye expresamente la cláusula anterior de disposición sin espera de código externo: esa cláusula no garantizaba el comienzo físico estricto solicitado. La disposición de una suscripción lenta puede tardar; no debe ejecutarse bloqueando el hilo UI. AScanViewModel ya libera suscripciones en un worker y mantiene su barrera UI. DisposeAsync del servicio cancela y espera fuera del bloqueo global las puertas de las suscripciones activas. Los cinco segundos del cierre de aplicación siguen siendo solo diagnósticos, sin aborto forzado. No esperar sincronizadamente desde un callback una tarea de cierre que necesite que ese callback termine.

CloseRun invalida generación y pendientes sin esperar observadores; no equivale a cancelar la suscripción, que puede recibir ejecuciones posteriores. No establece una barrera de comienzos efectivos al retornar: esa garantía corresponde a Dispose de suscripción y a la finalización de DisposeAsync del servicio. La comprobación previa a OnNext descarta selecciones cuya generación ya se invalidó. La prueba de regresión suspende después de extraer, completa Dispose y luego permite intentar la invocación: el observador no es llamado.

## Fallos y memoria

ApplicationSession registra fallos de apertura, entrega o cierre del sink en SessionSnapshot.VisualError y deshabilita la rama afectada. VisualError no convierte automáticamente un fallo visual en fallo de adquisición. La liberación del frame continúa en finally; los errores propios de adquisición/liberación conservan tratamiento independiente. El servicio visual registra sus fallos de proyección/publicación/observadores.

Un fallo terminal de PublishLoopAsync publica además AScanDeliveryStatus mediante StatusChanges, un observable independiente de los A-Scans con reproducción del estado vigente al suscribirse. Contiene versión y TerminalError; no retiene muestras ni crea un lector adicional. AScanViewModel recibe opcionalmente este observable por constructor, valida versiones, actualiza VisualStatus/VisualError mediante su despachador y retira la curva obsoleta. WPF utiliza el binding existente, sin sondeo de errores desde code-behind. La nueva suscripción se libera junto a las de sesión y A-Scan. No es necesario recibir otro frame ni convertir la sesión en Faulted para mostrar el error.

Snapshots con almacenamiento propio sin pooling visual: un pendiente, un actual y referencias acotadas por suscripción, además de proyección en curso. El presupuesto total depende de las suscripciones y de la retención externa. La cifra anterior de cuatro snapshots o 30 asignaciones/s no es una garantía actual. Referencias acotadas no equivalen a límite de heap antes del GC.

Queda pendiente medir CPU, asignaciones, GC y backpressure al proyectar cada frame interesado, y valorar una optimización previa al mailbox. El observador lento está aislado, pero la proyección síncrona sí afecta al lector. Cualquier optimización debe conservar el préstamo sin retención, ownership y separación visual; no hay alternativa seleccionada ni benchmark.

## Validación y siguientes pasos

Pruebas existentes: reducción y último intervalo, independencia tras devolver memoria, sustitución, reloj manual, observadores lentos/fallidos, cierre durante proyección y fallos de limpieza de temporizadores. La tarea de implementación precedente registró 109 pruebas correctas y build sin advertencias. No se repiten aquí ni acreditan la carrera pendiente de inicio de callbacks.

La corrección del 2026-09-15 añade pruebas deterministas de extracción/cancelación/intento, autocancelación, observador lento con pares independientes, cancelaciones concurrentes y diagnóstico terminal tras una primera curva sin snapshots posteriores, tanto en ViewModel como en WPF. El plan recoge resultados de build y ejecuciones repetidas.

Pendientes: benchmarks y posible optimización previa al mailbox; sincronización de documentos históricos fuera de alcance; presupuesto de futuras secundarias. La integración WPF, sus métricas a 5 Hz y su admisión de nuevos datos de dibujo a 30 Hz ya constan en el plan. El límite de admisión del observable no se presenta como medida del instante de dibujo.
