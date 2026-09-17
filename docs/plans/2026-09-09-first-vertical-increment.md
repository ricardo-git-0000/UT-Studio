# Plan del primer incremento — Simulador y A-Scan WPF

Fecha inicial: 2026-09-09. Revisado: 2026-09-16.
Estado actual: recorrido vertical integrado hasta WPF; correcciones de la revisión final registradas al final. Los resultados fechados actualizan las previsiones históricas siguientes; los benchmarks siguen pendientes.

## Objetivo y fuentes vigentes

Simulador -> sesión Application -> A-Scan neutral -> ViewModel -> ventana WPF. Mantener los siete proyectos productivos y Core.Tests del [ADR 0005](../adr/0005-initial-solution-structure.md). El [ADR 0006 revisado](../adr/0006-session-window-lifecycle.md) sustituye la política anterior de cierre; [ADR 0007](../adr/0007-frame-source-ownership.md) fija fuente/ownership y [ADR 0008](../adr/0008-latest-only-visual-delivery.md) fija entrega visual.

No incluye hardware GigE/PCIe, phased array, almacenamiento, DSP, informes, otros scans, distribución de payloads a varios consumidores ni infraestructura genérica de scopes por ventana. No bandeja, OnExplicitShutdown ni ejecución sin ventanas. No seleccionar biblioteca gráfica, añadir paquetes, implementar código ni hacer commit en esta tarea.

## Scaffolding existente: resultado histórico del 2026-09-10

Ya existen UTStudio.sln, siete proyectos en src y Core.Tests, con nullable, implicit usings y warnings como errores. MSTest 4.0.2 fue autorizado para Core.Tests; no se aprobaron otros paquetes directos, mocking ni CI.

La validación histórica registró restore/build correctos sin advertencias ni errores y test con código 0 sin pruebas disponibles. No acredita funcionalidad. App/MainWindow siguen siendo scaffolding; el arranque visual y el cierre funcional no se han verificado. No recrear los proyectos en el siguiente paso. Estas comprobaciones no se han repetido en la tarea documental actual.

## Decisiones confirmadas y actualización de entrega visual del 2026-09-13

| Área | Decisión aceptada |
| --- | --- |
| Cierre | MainWindow solicita salir: parada ordenada, desconexión, buffers liberados, Host detenido/liberado y cierre definitivo. Secundarias futuras no detienen sesión al cerrar |
| Frame | ConventionalUtFrame propietario, exclusivo e idempotente; `ReadOnlyMemory<short>` RF bipolar. Rectificadas reales: representación binaria abierta |
| Fuente | Contrato neutral; Application único lector del Channel, canal nuevo por run, inicio/parada serializados y rollback de inicio |
| Simulador | ChannelCapacity=4 y BufferCount=8 internos configurables, sujetos a benchmarks; no parte del contrato general |
| Visualización | Préstamo síncrono por IConventionalFrameSink; mailbox de un snapshot independiente en Visualization.Core; min/max configurable entre 4 y 1.024 puntos |
| Cadencias | Publicación neutral y admisión de notificaciones limitadas a 30 Hz; proyección por entrada interesada. Refresco UI efectivo y métricas periódicas a 5 Hz pendientes |
| Señal | Simulador determinista: 2.048 muestras, 50 MHz, hasta 100 A-Scans/s, RF bipolar |
| Escalas | Tiempo en microsegundos; RF fija -100 % a +100 % |
| Timeout | Cinco segundos solo diagnóstico; mantener MainWindow y limpieza observada, sin aborto forzado |
| Ventanas | Una vista integrada en MainWindow; sin scopes genéricos. Definir propiedad y presupuesto al añadir primera secundaria |

La configuración fija por ejecución contiene SourceId, RunId, canal físico, N, fs, offset y origen UTC; cada frame añade secuencia desde cero y tiempo monotónico de disparo. No confundir muestreo con frecuencia de disparo ni inferir profundidad o voltios.

## Simulador y reproducibilidad

Configuración de demostración aprobada: canal 0, offset 0, semilla 1; dos ecos sinusoidales de 5 MHz en 10 y 25 microsegundos, amplitudes 60 % y 30 % FS, envolvente gaussiana con desviación típica 0,35 microsegundos y ruido opcional hasta 1 % FS. Validar portadora menor que fs/2, parámetros finitos y límites de configuración.

Inyectar TimeProvider y usar reloj manual en pruebas. Definir algoritmo pseudoaleatorio explícito/versionado, redondeo y saturación. Primer disparo inmediato; tras cada entrega exitosa esperar un período antes del siguiente. Hasta 100/s es un máximo objetivo, no garantía de frecuencia exacta: generación/backpressure añaden retraso. Sin ráfagas para recuperar ticks ni huecos de secuencia por disparos no realizados.

Payload reproducible por configuración/semilla/secuencia en entorno fijo; timestamps también requieren mismo reloj y calendario de consumo. Comparaciones analíticas con tolerancia justificada, sin prometer identidad de funciones trascendentes entre plataformas. No fallos avanzados todavía.

## Componentes, DI y propiedad

Application posee sesión, único consumidor del Channel y estado. ApplicationSession recibe opcionalmente IConventionalFrameSink de Contracts/Presentation; un valor nulo conserva el comportamiento anterior. Accept presta `ReadOnlySpan<short>` síncronamente: el sink no puede retenerlo ni conservar aliases. La sesión libera siempre el frame en finally, también si falla transformación o entrega.

AScanVisualDelivery, en Visualization.Core, proyecta cada entrada válida con interés visual y mantiene un mailbox de un snapshot independiente. Sustituir el pendiente no devuelve memoria UT: solo descarta una referencia visual. El snapshot conserva run, canal, secuencia, versión y metadatos, y permanece válido al reutilizar el buffer original. Application no referencia Visualization.Core; Contracts tampoco. Fuente y sink son prestados, cerrados por la sesión y dispuestos por sus propietarios externos.

La pérdida latest-only pertenece exclusivamente a visualización y no condiciona las futuras ramas de procesamiento y persistencia sin pérdida silenciosa. Presentation, ViewModels, WPF y composición DI siguen pendientes; solo App.Wpf adaptará UI y compondrá implementaciones.

Previsión de composición, todavía sin implementar: singleton de aplicación para fuente, sesión, servicio AScanVisualDelivery con su mailbox/store encapsulado, planificador UI, reloj y coordinación de salida. Transformador sin estado reutilizable. Las interfaces de sesión resuelven la misma instancia. ViewModels Main/AScan independientes, creados explícitamente para MainWindow con suscripciones de vida explícita y liberación al cerrar; no infraestructura genérica de scopes. Pool, Channel, tokens y tareas pertenecen al run, fuera de DI.

Composición WPF con OnMainWindowClose: cancelar sincrónicamente el primer Closing, esperar asíncronamente limpieza y Host, autorizar/reemitir Close solo al terminar. Mantener ventana para diagnóstico. Futura secundaria: estado/suscripciones propios y cierre sin detener sesión; concretar diseño antes de incorporarla.

## Inicio y parada sin interbloqueo

Inicio neutral implementado: conectar -> validar/configurar -> Start de fuente -> abrir ejecución visual opcional -> instalar lector Application -> publicar operativo. Host/composición siguen pendientes. Si la fuente no entregó run, ella revierte productor/canal/buffers. Tras entregarlo, Application responde de consumo o rollback aunque se cancele la solicitud antes de instalar el lector. El token del solicitante no mantiene ligado un run exitoso.

Salida durante inicio: cerrar admisión y cancelar la operación vigente (Connect/Configure/Start) antes de esperar la exclusión de operaciones. Sincronizar registro de inicio y marca de salida para no perder cancelación; observar finalización/rollback y continuar limpieza serializada. La cancelación privada de un run exitoso sigue siendo independiente.

Parada: cerrar entrega latest y marcar Stopping; cancelar inmediatamente productor y sus esperas de reserva/reloj/WriteAsync. El único consumidor sigue drenando y liberando concurrentemente, sin cancelar su lectura. El productor libera su frame no entregado y completa writer al salir. Source.StopAsync no espera drenaje; Application observa productor y lector y espera AllFramesReleased tras sellado y cero reservas. CloseRun invalida y vacía los snapshots visuales; el mailbox no contiene owners UT.

Fallo de consumidor: su limpieza sigue siendo el único lector; cancela productor y drena. No esperar tareas bajo locks. No republicar los frames drenados durante parada. Una proyección síncrona en curso puede terminar, pero la generación invalidada impide confirmar su resultado; el lector Application libera el frame en finally. Un fallo del sink se registra como VisualError y deshabilita la rama visual sin convertirse automáticamente en fallo de adquisición. No nuevo run hasta limpiar el anterior.

Salir añade desconexión, liberación visual, parada/liberación Host y cierre definitivo. Timeout diagnóstico no cancela limpieza ni destruye recursos. Detalle normativo: ADR 0006/0007.

## Estado de implementación y siguientes pasos

1. Implementado: modelos/contratos UT, fuente simulada, pool/reloj, sesión con rollback, drenaje y barrera.
2. Implementado: puerto visual síncrono, proyección min/max por entrada interesada, snapshot independiente, mailbox latest-only y publicación neutral a 30 Hz. Received/Published/Replaced son contadores visuales acumulados consultables bajo demanda; Published cuenta confirmaciones al store, no callbacks. Dropped y ObserverCoalesced/ObserverErrors separan otros descartes y observación.
3. Pendiente de código: satisfacer el requisito estricto de callbacks. Uno que ya comenzó puede terminar después del cierre; no deben comenzar nuevos tras cancelar suscripción o completar entrega. Actualmente Pump admite bajo lock e invoca OnNext fuera: uno ya admitido puede comenzar después de invalidar. Corregir y probar esa intercalación; no darla por resuelta con pruebas de callbacks ya ejecutándose.
4. Pendiente de medición: CPU, asignaciones, GC y backpressure por proyectar cada entrada; valorar optimización previa al mailbox. Los 30 Hz acotan publicación, no necesariamente el coste de proyección ni comienzos efectivos de OnNext. Sin benchmark ni optimización seleccionada.
5. Pendiente: resolver adaptador de dibujo y aprobar paquetes adicionales necesarios; componer MainViewModel/AScanViewModel, puerto neutral de sesión y adaptación UI sin referencias entre ViewModels ni scopes genéricos. Integrar métricas periódicas a 5 Hz y refresco UI efectivo máximo 30 Hz.
6. Pendiente: integrar Closing/Host según ADR 0006, verificar timeout exclusivamente diagnóstico y probar WPF en Windows. No crear proyectos adicionales.

## Archivos existentes y trabajo previsto

| Proyecto | Archivos/responsabilidades orientativos |
| --- | --- |
| Domain | Identificadores, ConventionalAcquisitionConfiguration, ConventionalUtFrameMetadata y modo RF |
| Contracts | Existentes: IUtFrameSource, UtAcquisitionRun, ConventionalUtFrame e IConventionalFrameSink. Puerto de sesión para Presentation pendiente; no introducir ILatestFrameFeed propietario para la entrega actual |
| Application | Existentes: ApplicationSession, SessionPhase, SessionSnapshot (incluye VisualError), SessionSnapshotPublisher. Sin mailbox de owners UT |
| Acquisition.Simulator | SimulatorUtFrameSource, SimulatorOptions, SyntheticRfGenerator, BoundedSampleBufferPool |
| Visualization.Core | Existentes: AScanPoint, AScanSnapshot (escalas integradas), IAScanProjector, AScanProjector, AScanVisualDelivery y AScanDeliveryStatistics |
| Presentation | Pendientes: MainViewModel, AScanViewModel y adaptación/planificación UI sobre snapshots neutrales |
| App.Wpf | Actualizar App/MainWindow; composición, vista A-Scan, adaptador Dispatcher y coordinación de salida |
| Core.Tests | Suites neutrales por responsabilidad, reloj manual y dobles de fuente/planificador |

No crear AScanWindow ni gestores/scopes genéricos todavía.

## Validación prevista del incremento

MSTest existente; sin paquetes nuevos de mocking. Core.Tests referencia solo los seis proyectos neutrales.

- Arquitectura: grafo permitido, sin WPF/Avalonia en núcleo/Presentation ni referencias entre ViewModels.
- RF: límites, cuentas digitales, ejes, determinismo, reloj monotónico y rechazo de modos no soportados.
- Inicio: fallo/cancelación antes y después de entregar run, sin tareas o buffers huérfanos.
- Concurrencia: Channel lleno con productor en WriteAsync al parar; cancelación observable mientras lector drena. Fallo del consumidor; carreras de entrada/proyección/cierre visual y Rent/Seal.
- Ownership: devolución exactamente una vez, sin uso tras liberar, límite de buffers y barrera pendiente mientras transforma pero independiente de Dispatcher bloqueado.
- Visualización: N=1/1.024/1.025/máximo, presupuesto 4..1.024, extremos min/max y último intervalo, snapshot válido tras reutilizar muestras, latest-only, estadísticas y errores visuales. Estas responsabilidades ya tienen pruebas neutrales.
- UI: máximo 30 Hz efectivo sin recuperación de ticks, callback pendiente acotado, RunId/generación impiden actualización antigua tras reinicio; métricas a 5 Hz conservando contadores exactos.
- Cierre principal: salida con adquisición activa o inicio pendiente, solicitudes repetidas, desconexión y Host después de limpiar; cinco segundos diagnósticos sin cierre prematuro. Fuente doble con Connect/Configure/Start bloqueados: cancelación antes de esperar exclusión, rollback observado y carrera de registro de inicio/salida.
- Validación futura de secundarias: cerrar una no detiene sesión; no implementar su infraestructura ahora.

Bindings, dibujo y cierre real de WPF se comprueban por separado en Windows. Sin proyecto nuevo de pruebas en este incremento. Los benchmarks determinarán ajustes internos de capacidad/pool; los máximos del producto no son una configuración de rendimiento garantizada.

## Propiedad de la tarea documental actual

| Responsable | Escritura autorizada/responsabilidad |
| --- | --- |
| Principal, único escritor | Únicamente ADR 0008, docs/architecture/data-pipeline.md y este plan |
| solution_architect, lectura | Propuesta de actualización y revisión de contratos/orden de parada; sin escritura |
| quality_reviewer, lectura | Revisión del diff, concurrencia, memoria, consistencia y alcance; sin escritura |

Sin código, paquetes ni commit. Las decisiones duraderas quedan en estos documentos; no se guarda transcripción ni resumen duplicado.

## Pendientes y riesgos

- Renderizador/adaptador WPF sin biblioteca elegida; aprobación de paquetes adicionales. Mocking y CI siguen abiertos.
- Medir proyección O(N) y asignación por entrada interesada; valorar optimización previa al mailbox. No afirmar 30 asignaciones/s ni cuatro snapshots como límite demostrado: incluir pendiente, actual, proyección y estado por suscriptor, además de retención externa.
- Representación de rectificadas reales, hardware, PA, almacenamiento y ownership compartido se resolverán en incrementos posteriores.
- Propiedad y presupuesto de ventanas secundarias antes de añadir la primera; no salida/reapertura sin ventanas.
- Resolver la brecha entre admisión e inicio efectivo de callback. Verificar después callbacks de otro run y cierre del proceso antes de terminar Host.
- Sincronizar referencias antiguas de ADR 0006/0007 y arquitectura general fuera de este alcance. ADR 0008 revisado sustituye para la rama visual el mailbox propietario y la proyección a 30 Hz; los valores internos 4/8 del simulador no cambian.

## Validación de esta tarea documental

Comprobar diff y archivos nuevos, enlaces relativos y coherencia con requisitos/ADR. Compilación y pruebas funcionales no aplican: solo documentación. No presentar las pruebas o benchmarks previstos como ejecutados.

Resultado histórico de la revisión documental del 2026-09-12: solution_architect confirmó coherencia arquitectónica; quality_reviewer detectó la cancelación de inicio pendiente antes de esperar exclusión, se incorporó y confirmó el hallazgo cerrado. Diff sin errores de whitespace, 11 archivos Markdown y 52 enlaces relativos comprobados sin roturas. En aquella revisión no se detectaron contradicciones; no acredita la sincronización de la implementación posterior. Sin compilación, pruebas funcionales, paquetes ni commit.

La tarea de implementación visual precedente registró build sin advertencias y 109 pruebas correctas. Esta revisión del 2026-09-13 contrasta código y documentación en lectura; no repite build/tests y no considera resuelta la carrera de inicio de callbacks. Validar enlaces relativos, diff y alcance de los tres documentos; no modificar código ni realizar commits.

Resultado de la sincronización del 2026-09-13: solution_architect y quality_reviewer conformes en lectura; 17 enlaces relativos válidos y diff sin errores de whitespace. Únicamente se modifican los tres documentos autorizados. Compilación y pruebas funcionales no aplican; sin cambios de código ni commit. La brecha de callbacks y los benchmarks siguen pendientes.

## Resultado de integración WPF — 2026-09-14

La petición de implementación posterior autoriza App.Wpf, pruebas Windows separadas, proyectos/solución necesarios y este registro final. Sustituye para esta etapa las prohibiciones históricas de implementación y de proyecto adicional de pruebas que aparecen arriba. El principal es el único escritor; solution_architect, wpf_mvvm_specialist y quality_reviewer revisan en lectura. No se modifican proyectos neutrales ni se hace commit.

### Composición y paquete

- [DesktopRuntime](../../src/UTStudio.App.Wpf/Composition/DesktopRuntime.cs) compone Generic Host y conserva un propietario explícito desde la primera asignación, incluso durante construcción parcial. Devuelve InitializationError junto al propietario si falla la inicialización.
- Una instancia externa de SimulatorUtFrameSource, AScanVisualDelivery, ApplicationSession y AScanViewModel. Registros DI por instancia, incluidos aliases, evitan disposición automática duplicada. MainWindow se resuelve como singleton por constructor y recibe el ViewModel como DataContext. No hay scopes por ventana ni referencias entre ViewModels.
- El puerto IApplicationSession y SessionSnapshot/SessionPhase ya pertenecen a Contracts/Application; Presentation no referencia Application. WpfUiDispatcher adapta el puerto neutral sin cambiarlo.
- Único paquete productivo directo añadido: [Microsoft.Extensions.Hosting 10.0.12](https://www.nuget.org/packages/Microsoft.Extensions.Hosting/10.0.12), estable de la línea .NET 10. Aporta las dependencias oficiales de DI, configuración y logging; no se añaden referencias directas redundantes ni biblioteca gráfica. CommunityToolkit.Mvvm 8.4.2 sigue exclusivamente en Presentation.
- Configuración inicial: canal 0, 2.048 muestras, 50 MHz, semilla 1, 50 A-Scans/s como máximo configurado. Simulator:Seed y Simulator:AScansPerSecond admiten configuración estándar del Host; el simulador valida el límite de 100/s. BufferCount=8 y ChannelCapacity=4 conservan sus valores internos. Sin adquisición automática al abrir.

### Ventana y dibujo

App retira StartupUri, inicia Host y resuelve/muestra MainWindow con OnMainWindowClose. Start/Stop enlazan los comandos del ViewModel. Se muestran fase, canal, secuencia, errores y contadores visuales. El hilo UI no realiza adquisición ni reducción.

[AScanControl](../../src/UTStudio.Visualization.Wpf/Controls/AScanControl.cs) consume snapshots independientes y dibuja mediante DrawingContext/StreamGeometry, sin elemento visual por punto. Desde la extracción aceptada en [ADR 0009](../adr/0009-reusable-scan-visualization-controls.md), el control y AScanCoordinates viven en `UTStudio.Visualization.Wpf`; App.Wpf solo los aloja desde XAML. Escala horizontal al tamaño disponible, etiquetas en microsegundos, escala vertical RF fija ±100 %, línea de cero, clipping y recursos congelados.

La adopción de **nuevos datos de señal en OnRender** queda separada por al menos 333.334 ticks, medidos con reloj monotónico en el momento de dibujar. Un tick retrasado no recupera actualizaciones. Los repintados por redimensionamiento pueden reutilizar los mismos datos; borrar una ejecución invalidada es inmediato. Se conserva una referencia pendiente y una mostrada. La consulta visible de métricas usa intervalos mínimos de 200 ms, sin confundir Published con callbacks o frames persistidos. La cadencia no es una garantía de rendimiento sostenido.

### Cierre y fallos

Primer Closing cancela sincrónicamente, desactiva interacciones y observa una sola tarea. Se solicita primero DisposeAsync del ViewModel, que invalida callbacks; inmediatamente después, sin esperar callbacks de cancelación que puedan depender de Stop, se solicita DisposeAsync de sesión. Se observan ambas tareas antes de disponer dependencias. La sesión mantiene su lector único, cancelación del productor, drenaje, ProducerCompletion, AllFramesReleased y desconexión existentes.

Después: disponer fuente, disponer entrega visual, detener Host, disponer Host y autorizar/reemitir Close. Dispatcher y ventana siguen vivos hasta completar la barrera UI. No hay recursión ni disposición repetida. HostOptions.ShutdownTimeout es infinito; los cinco segundos del coordinador son exclusivamente diagnósticos, sin token de aborto ni devolución forzada.

Un error histórico de adquisición no impide el cierre si sesión acredita Disposed y la fuente queda Disconnected después de su disposición. Un fallo sin confirmación detiene las fases dependientes y mantiene la ventana diagnóstica; no se repite automáticamente toda la secuencia. El diagnóstico de cierre no depende del ViewModel ya liberado ni del logger del Host ya dispuesto.

Si falla la construcción o el arranque, StartupFailureWindow permanece visible mientras se limpia el propietario parcial. Solo permite cerrar tras confirmación; no se usa un diálogo efímero que deje un proceso sin ventanas. La excepción inicial y el diagnóstico de limpieza permanecen accesibles.

### Validación y límites

- Resultado histórico previo a la extracción: el nuevo proyecto [UTStudio.Tests.Wpf](../../tests/UTStudio.Tests.Wpf/UTStudio.Tests.Wpf.csproj) quedó separado de Core.Tests y reutilizó MSTest 4.0.2 sin framework ni mocking nuevos. Desde ADR 0009, App.Wpf, Visualization.Wpf y Tests.Wpf son los únicos proyectos que usan WPF.
- Pruebas de composición/aliases/DataContext, Dispatcher STA bombeado, cancelación/excepciones, coordenadas/resize/señal constante/dimensiones cero, admisión 30 Hz y métricas 5 Hz con reloj manual, orden/repetición/fallo de cierre, diagnóstico a cinco segundos, construcción parcial, fallo histórico del productor y ventana de diagnóstico.
- Prueba de integración del simulador hasta bitmap WPF: snapshot independiente de 2.048 muestras reducido a un máximo de 1.024 puntos, curva detectada y cero hijos visuales por punto. Captura PNG temporal inspeccionada; no se guarda fixture ni captura en Git. Pruebas reales de Closing con adquisición activa.
- Arranque externo comprobado desde el directorio del ejecutable: MainWindow visible y proceso terminado mediante CloseMainWindow, sin finalizarlo a la fuerza. Esto es una comprobación automatizada de arranque/cierre, no una sesión de aceptación manual prolongada.
- Los tres revisores confirman corregidos los hallazgos de construcción parcial, ventana diagnóstica, error histórico de fuente y admisión en el dibujo efectivo. Formato limitado, restore/build/tests, diff y referencias se validan al finalizar; el resultado numérico se registra debajo.

Pendientes conservados: brecha estricta de admisión/inicio de callbacks de AScanVisualDelivery en ADR 0008 (fuera del alcance WPF), benchmarks de proyección/asignaciones/GC y capacidad, sincronización histórica de otros ADR, aceptación visual prolongada/DPI y futuras ventanas secundarias. El ViewModel invalida callbacks tardíos para que no alteren bindings tras el cierre, sin afirmar que corrige el servicio neutral. No se implementan hardware, almacenamiento, DSP, PA ni composición de secundarias.

Resultado histórico de validación previo al ADR 0009: formato limitado y restore correctos; build con 0 advertencias y 0 errores; 149 pruebas correctas (128 neutrales y 21 WPF), ninguna omitida. Diff sin errores de whitespace, incluidos archivos nuevos; en ese momento las referencias WPF estaban limitadas a App.Wpf y Tests.Wpf, y se comprobaron los enlaces relativos del plan. Desde la extracción, Visualization.Wpf es también un proyecto WPF. Sin commit.

## Correcciones de la revisión final — 2026-09-15

Alcance: Visualization.Core, Presentation para diagnóstico, WPF para enlazarlo, pruebas afectadas y ADR 0008/este plan. El principal es único escritor; ut_visualization, wpf_mvvm_specialist y quality_reviewer revisan en lectura. Sin paquetes, commits ni funcionalidades adicionales.

1. Cancelación estricta: una puerta reentrante por suscriptor protege la comprobación final y la llamada OnNext. Dispose marca cancelación sin esperar bajo el bloqueo global y después cruza esa puerta; al retornar no puede comenzar otro callback. Una cancelación externa puede esperar al callback activo; la autocancelación es reentrante. Accept, CloseRun y los pares siguen independientes. Esta semántica sustituye la cláusula histórica de cancelación sin espera de código externo: véase ADR 0008 corregido. La aplicación sigue disponiendo fuera del hilo UI, con timeout exclusivamente diagnóstico.
2. Diagnóstico terminal: AScanDeliveryStatus/StatusChanges entrega y reproduce el fallo del publicador independientemente de nuevas curvas. AScanViewModel integra el estado por dispatcher, muestra VisualError y descarta la curva obsoleta; WPF usa binding. La sesión continúa en Running si no hay fallo de adquisición.
3. Esperas de pruebas: watchdogs diagnósticos de diez segundos identifican la condición no alcanzada. Continúan usándose señales y TimeProvider manual, sin retardos para ocultar carreras. Los préstamos se liberan mediante using/finally, incluso si falla una aserción o termina tarde una reserva; el harness devuelve sus frames aunque falle Stop.
4. Captura WPF: nombre temporal con GUID por ejecución, stream cerrado antes de eliminar y eliminación en finally aun si falla renderizado o limpieza. Las ventanas de prueba se cierran mediante su evento y se espera Closed antes de terminar su Dispatcher; las capturas no comparten rutas entre procesos.

Pruebas nuevas: intercalación extracción -> Dispose terminado -> intento de callback; autocancelación; dos cancelaciones que alcanzan su barrera mientras el observador está retenido; continuidad de un observador sano; error del publicador después de una primera curva, sin otro snapshot, con actualización de ViewModel/binding WPF y adquisición intacta; reproducción del error para suscriptores tardíos; liberación de la suscripción de estado y descarte de estados tardíos/antiguos. No se da por corregida una carrera únicamente porque tareas aún no planificadas aparezcan pendientes.

El cambio no realiza benchmarks ni modifica protocolos, ownership UT, sesión, hardware, procesamiento o composición ajena al diagnóstico. No cambia el criterio de cinco segundos ni autoriza detener código externo a la fuerza.

Validación final del 2026-09-16: formato limitado correcto; build completo con cero errores y advertencias. Tres ejecuciones completas consecutivas correctas, con 156 casos por ejecución (134 neutrales y 22 WPF), sin omisiones. Dos ejecuciones WPF adicionales simultáneas: 22/22 correctas cada una, sin colisiones ni capturas temporales GUID restantes. Diff y archivos nuevos sin errores de whitespace. Alcance limitado a los componentes y pruebas citados; sin cambios de paquetes, proyectos, contratos UT ni commits. Revisores en lectura: ut_visualization y wpf_mvvm_specialist conformes; quality_reviewer confirmó barreras y watchdogs y pidió precisar que CloseRun no es la barrera de cancelación, distinción incorporada en ADR 0008.
