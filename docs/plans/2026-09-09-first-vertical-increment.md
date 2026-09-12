# Plan del primer incremento — Simulador y A-Scan WPF

Fecha inicial: 2026-09-09. Revisado: 2026-09-12.
Estado: diseño aprobado con correcciones; scaffolding existente, implementación funcional pendiente y no autorizada por esta tarea documental.

## Objetivo y fuentes vigentes

Simulador -> sesión Application -> A-Scan neutral -> ViewModel -> ventana WPF. Mantener los siete proyectos productivos y Core.Tests del [ADR 0005](../adr/0005-initial-solution-structure.md). El [ADR 0006 revisado](../adr/0006-session-window-lifecycle.md) sustituye la política anterior de cierre; [ADR 0007](../adr/0007-frame-source-ownership.md) fija fuente/ownership y [ADR 0008](../adr/0008-latest-only-visual-delivery.md) fija entrega visual.

No incluye hardware GigE/PCIe, phased array, almacenamiento, DSP, informes, otros scans, distribución de payloads a varios consumidores ni infraestructura genérica de scopes por ventana. No bandeja, OnExplicitShutdown ni ejecución sin ventanas. No seleccionar biblioteca gráfica, añadir paquetes, implementar código ni hacer commit en esta tarea.

## Scaffolding existente: resultado histórico del 2026-09-10

Ya existen UTStudio.sln, siete proyectos en src y Core.Tests, con nullable, implicit usings y warnings como errores. MSTest 4.0.2 fue autorizado para Core.Tests; no se aprobaron otros paquetes directos, mocking ni CI.

La validación histórica registró restore/build correctos sin advertencias ni errores y test con código 0 sin pruebas disponibles. No acredita funcionalidad. App/MainWindow siguen siendo scaffolding; el arranque visual y el cierre funcional no se han verificado. No recrear los proyectos en el siguiente paso. Estas comprobaciones no se han repetido en la tarea documental actual.

## Decisiones confirmadas el 2026-09-11

| Área | Decisión aceptada |
| --- | --- |
| Cierre | MainWindow solicita salir: parada ordenada, desconexión, buffers liberados, Host detenido/liberado y cierre definitivo. Secundarias futuras no detienen sesión al cerrar |
| Frame | ConventionalUtFrame propietario, exclusivo e idempotente; `ReadOnlyMemory<short>` RF bipolar. Rectificadas reales: representación binaria abierta |
| Fuente | Contrato neutral; Application único lector del Channel, canal nuevo por run, inicio/parada serializados y rollback de inicio |
| Simulador | ChannelCapacity=4 y BufferCount=8 internos configurables, sujetos a benchmarks; no parte del contrato general |
| Visualización | Mailbox latest-only, único coordinador, snapshot independiente, máximo 1.024 puntos con min/max |
| Cadencias | Refresco visual máximo 30 Hz; métricas exactas publicadas a 5 Hz |
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

Application posee sesión, consumidor, estado y mailbox. Presentation coordina la transformación de Visualization.Core y entrega snapshots; no referencia implementaciones Application ni Simulator. Contracts no referencia Visualization.Core. Solo App.Wpf adapta UI y compone implementaciones.

Singleton de aplicación: fuente, sesión, stores/mailbox encapsulados, coordinador visual, planificador UI, reloj y coordinación de salida. Transformador sin estado reutilizable. Las interfaces de sesión resuelven la misma instancia. ViewModels Main/AScan independientes, creados explícitamente para MainWindow con suscripciones de vida explícita y liberación al cerrar; no infraestructura genérica de scopes. Pool, Channel, tokens y tareas pertenecen al run, fuera de DI.

Composición WPF con OnMainWindowClose: cancelar sincrónicamente el primer Closing, esperar asíncronamente limpieza y Host, autorizar/reemitir Close solo al terminar. Mantener ventana para diagnóstico. Futura secundaria: estado/suscripciones propios y cierre sin detener sesión; concretar diseño antes de incorporarla.

## Inicio y parada sin interbloqueo

Inicio: Host/composición -> conectar -> validar/configurar -> Start -> instalar lector Application -> publicar operativo. Si la fuente no entregó run, ella revierte productor/canal/buffers. Tras entregarlo, Application responde de consumo o rollback aunque se cancele la solicitud antes de instalar el lector. El token del solicitante no mantiene ligado un run exitoso.

Salida durante inicio: cerrar admisión y cancelar la operación vigente (Connect/Configure/Start) antes de esperar la exclusión de operaciones. Sincronizar registro de inicio y marca de salida para no perder cancelación; observar finalización/rollback y continuar limpieza serializada. La cancelación privada de un run exitoso sigue siendo independiente.

Parada: cerrar entrega latest y marcar Stopping; cancelar inmediatamente productor y sus esperas de reserva/reloj/WriteAsync. El único consumidor sigue drenando y liberando concurrentemente, sin cancelar su lectura. El productor libera su frame no entregado y completa writer al salir. Source.StopAsync no espera drenaje; Application observa productor y lector, vacía mailbox y espera AllFramesReleased tras sellado y cero reservas.

Fallo de consumidor: su limpieza sigue siendo el único lector; cancela productor y drena. No esperar tareas bajo locks. No republicar los frames drenados durante parada. El transformador que ya tenía un frame libera en finally antes de esperar UI; no publicar resultados obsoletos. No nuevo run hasta limpiar el anterior.

Salir añade desconexión, liberación visual, parada/liberación Host y cierre definitivo. Timeout diagnóstico no cancela limpieza ni destruye recursos. Detalle normativo: ADR 0006/0007.

## Secuencia de implementación futura

1. Autorizar implementación y resolver adaptador de dibujo WPF; justificar y aprobar paquetes adicionales necesarios para Hosting/MVVM. MSTest ya está aprobado. No crear proyectos adicionales.
2. Incorporar modelos y contratos del ADR 0007 con validaciones, estados y errores neutrales.
3. Implementar simulador/reloj/pool internos, sesión y rollback/parada; verificar canal lleno y ownership antes de integrar UI.
4. Incorporar mailbox, transformación min/max, snapshot y stores según ADR 0008; probar memoria y cadencias con tiempo controlable.
5. Componer MainViewModel/AScanViewModel y vista inicial sin referencias entre ViewModels ni scopes genéricos; adaptar hilo UI en WPF.
6. Integrar Closing y Host según ADR 0006; comprobar timeout y errores conservando MainWindow.
7. Ejecutar build/pruebas pertinentes, comprobación WPF separada y benchmarks reproducibles de buffers, asignaciones, GC, caudal y refresco. Registrar resultados reales, no supuestos.

## Archivos previstos para implementación posterior

| Proyecto | Archivos/responsabilidades orientativos |
| --- | --- |
| Domain | Identificadores, ConventionalAcquisitionConfiguration, ConventionalUtFrameMetadata y modo RF |
| Contracts | IUtFrameSource, UtAcquisitionRun, ConventionalUtFrame, capacidades/estados/errores, IApplicationSession, SessionSnapshot, ILatestFrameFeed |
| Application | ApplicationSession, LatestFrameMailbox, SessionStateStore |
| Acquisition.Simulator | SimulatorUtFrameSource, SimulatorOptions, SyntheticRfGenerator, BoundedSampleBufferPool |
| Visualization.Core | AScanPoint, AScanSnapshot, AScanScale, AScanProjector |
| Presentation | MainViewModel, AScanViewModel, coordinador/store visual, UiUpdatePump, IUiDispatcher |
| App.Wpf | Actualizar App/MainWindow; composición, vista A-Scan, adaptador Dispatcher y coordinación de salida |
| Core.Tests | Suites neutrales por responsabilidad, reloj manual y dobles de fuente/planificador |

No crear AScanWindow ni gestores/scopes genéricos todavía.

## Validación prevista del incremento

MSTest existente; sin paquetes nuevos de mocking. Core.Tests referencia solo los seis proyectos neutrales.

- Arquitectura: grafo permitido, sin WPF/Avalonia en núcleo/Presentation ni referencias entre ViewModels.
- RF: límites, cuentas digitales, ejes, determinismo, reloj monotónico y rechazo de modos no soportados.
- Inicio: fallo/cancelación antes y después de entregar run, sin tareas o buffers huérfanos.
- Concurrencia: Channel lleno con productor en WriteAsync al parar; cancelación observable mientras lector drena. Fallo del consumidor; carreras Publish/Take/Close y Rent/Seal.
- Ownership: devolución exactamente una vez, sin uso tras liberar, límite de buffers y barrera pendiente mientras transforma pero independiente de Dispatcher bloqueado.
- Visualización: N=1/1.024/1.025/máximo, extremos min/max en orden, snapshot válido tras reutilizar muestras, latest-only y errores visuales.
- UI: máximo 30 Hz efectivo sin recuperación de ticks, callback pendiente acotado, RunId/generación impiden actualización antigua tras reinicio; métricas a 5 Hz conservando contadores exactos.
- Cierre principal: salida con adquisición activa o inicio pendiente, solicitudes repetidas, desconexión y Host después de limpiar; cinco segundos diagnósticos sin cierre prematuro. Fuente doble con Connect/Configure/Start bloqueados: cancelación antes de esperar exclusión, rollback observado y carrera de registro de inicio/salida.
- Validación futura de secundarias: cerrar una no detiene sesión; no implementar su infraestructura ahora.

Bindings, dibujo y cierre real de WPF se comprueban por separado en Windows. Sin proyecto nuevo de pruebas en este incremento. Los benchmarks determinarán ajustes internos de capacidad/pool; los máximos del producto no son una configuración de rendimiento garantizada.

## Propiedad de la tarea documental actual

| Responsable | Escritura autorizada/responsabilidad |
| --- | --- |
| Principal, único escritor | ADR 0006/0007/0008, este plan; sincronización de índice ADR, arquitectura overview/pipeline/testing, estado de requisitos/cuestiones y referencia histórica de MSTest en ADR 0005 |
| solution_architect, lectura | Propuesta de actualización y revisión de contratos/orden de parada; sin escritura |
| quality_reviewer, lectura | Revisión del diff, concurrencia, memoria, consistencia y alcance; sin escritura |

Sin código, paquetes ni commit. Las decisiones duraderas quedan en estos documentos; no se guarda transcripción ni resumen duplicado.

## Pendientes y riesgos

- Renderizador/adaptador WPF sin biblioteca elegida; aprobación de paquetes adicionales. Mocking y CI siguen abiertos.
- Benchmarks y presupuestos medidos: buffers/envelopes/modelos visuales generan costes que no se han medido; no prometer cero asignaciones ni heap estrictamente constante.
- Representación de rectificadas reales, hardware, PA, almacenamiento y ownership compartido se resolverán en incrementos posteriores.
- Propiedad y presupuesto de ventanas secundarias antes de añadir la primera; no salida/reapertura sin ventanas.
- Riesgos a verificar: reutilización prematura, parada bloqueada, publicación tardía, callback de otro run y cierre del proceso antes de terminar Host.

## Validación de esta tarea documental

Comprobar diff y archivos nuevos, enlaces relativos y coherencia con requisitos/ADR. Compilación y pruebas funcionales no aplican: solo documentación. No presentar las pruebas o benchmarks previstos como ejecutados.

Revisión documental final del 2026-09-12: solution_architect confirmó coherencia arquitectónica; quality_reviewer detectó la cancelación de inicio pendiente antes de esperar exclusión, se incorporó y confirmó el hallazgo cerrado. Diff sin errores de whitespace, 11 archivos Markdown y 52 enlaces relativos comprobados sin roturas. Sin contradicciones vigentes detectadas; pendientes funcionales arriba identificados. Sin compilación, pruebas funcionales, paquetes ni commit.
