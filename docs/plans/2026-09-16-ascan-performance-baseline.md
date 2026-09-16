# Plan de línea base de rendimiento y estabilidad A-Scan

Fecha: 2026-09-16. Estado: propuesta documental pendiente de aprobación para implementación y ejecución. No contiene resultados experimentales ni presupuestos de rendimiento aprobados.

## Objetivo, fuentes y alcance

Establecer una línea base reproducible del pipeline convencional RF, localizar costes y verificar integridad bajo carga antes de optimizar. Requisitos aplicables: R01–R08, R13 y R14 de la [línea base](../requirements/baseline.md). Restricciones aceptadas en [ADR 0002](../adr/0002-bounded-pipeline.md), [ADR 0007](../adr/0007-frame-source-ownership.md), [ADR 0008](../adr/0008-latest-only-visual-delivery.md) y [ADR 0006](../adr/0006-session-window-lifecycle.md). Contexto: [arquitectura](../architecture/overview.md), [pipeline](../architecture/data-pipeline.md), [pruebas](../architecture/testing.md) y [primer incremento](2026-09-09-first-vertical-increment.md).

Esta tarea solo crea el plan y su enlace. El principal es único escritor; revisores en lectura: solution_architect, ut_acquisition, ut_visualization y quality_reviewer, en tandas de hasta tres. No autoriza código, proyectos, paquetes, instrumentación ni cambios de contratos. No incluye hardware, PA, DSP, persistencia, secundarias ni benchmark de transporte real. Los textos históricos de arquitectura/pipeline sobre integración pendiente y mailbox de owners no describen el código vigente; para esta medición prevalecen ADR 0008 revisado y las secciones finales del plan del incremento. Su sincronización general queda fuera de alcance.

## Límites del producto y configuración vigente

El producto contempla hasta 65.535 muestras de 16 bits, muestreo hasta 100 MHz y aproximadamente 1.000 A-Scans/s convencional cuando sea viable. Son límites/objetivos independientes: no se exige su combinación simultánea con el máximo de canales. Para cada configuración, sumar `N × 2 × A-Scans/s` de los flujos activos y añadir después metadatos, transporte y margen medido. Declarar si la cadencia es por canal o agregada. MHz describe muestras por segundo dentro del A-Scan, no A-Scans por segundo.

El [simulador actual](../../src/UTStudio.Acquisition.Simulator/SimulatorUtFrameSource.cs) admite un canal físico, RF short y N hasta 65.535. Configuración inicial: N=2.048, 50 MHz, offset cero. El generador exige muestreo superior a 10 MHz para su portadora de 5 MHz; toda la matriz propuesta usa 50 MHz. La aplicación elige 50/s; [SimulatorOptions](../../src/UTStudio.Acquisition.Simulator/SimulatorOptions.cs) tiene techo y valor predeterminado de 100/s. Su periodo se redondea a milisegundos y espera después de escribir, sin recuperar ticks: la generación y el backpressure reducen la tasa efectiva. No garantizar que una petición de 100/s entregue exactamente 100/s.

ChannelCapacity=4 y BufferCount=8 son internos configurables; conservarlos como baseline y respetar `BufferCount >= ChannelCapacity + 4`. No convertirlos en contrato general ni reducir esa reserva por interpretar la topología visual nueva. Payload del pool: `BufferCount × N × 2`, sin sumar otra vez la cola porque contiene referencias a esos mismos buffers. Con ocho buffers: 32.768 bytes para N=2.048 y 1.048.560 para N=65.535; no representan memoria total. Los arrays grandes pueden afectar al LOH; comprobarlo en el runtime medido.

ApplicationSession mantiene el único lector. Entrega un span prestado síncronamente al sink y libera el frame en finally. La proyección crea datos propios por cada entrada con interés visual; el mailbox conserva un snapshot pendiente y la publicación se limita a 30 Hz. Un observador lento tiene coalescencia propia y no retiene buffers UT. Sin suscriptores no se proyecta. El presupuesto visual es 4–1.024 puntos, inicialmente 1.024; publicación no equivale a dibujo ni a coste máximo de 30 proyecciones/s.

## Matriz de escenarios

Primera campaña: canal 0 único, 50 MHz, RF, semilla 1, ruido 0,01, offset cero, pool 8, canal 4. Registrar toda variante explícitamente. MB/s siguientes son decimales, payload solicitado teórico sin overhead, no resultados.

| ID | Muestras | Cadencia solicitada | Payload MB/s | Ejecución propuesta |
| --- | ---: | ---: | ---: | --- |
| S1 | 2.048 | 50/s | 0,2048 | Simulador actual; referencia de aplicación |
| S2 | 2.048 | 100/s | 0,4096 | Simulador actual; medir tasa efectiva |
| S3 | 2.048 | 1.000/s | 4,096 | Experimental; simulador actual lo rechaza |
| S4 | 65.535 | 50/s | 6,5535 | Simulador actual; coste de señal larga |
| S5 | 65.535 | 100/s | 13,107 | Simulador actual; medir tasa efectiva |
| S6 | 65.535 | Máximo sostenible | Medir | Barrido experimental; no deducirlo del techo de 100/s |

S3/S6 requieren aprobar una fuente sintética de carga propia de tools, conforme a IUtFrameSource, con canal/pool acotados y mismo ownership. No elevar el techo productivo ni usar tiempo virtual acelerado para afirmar throughput real. Separar dos modos: generación RF en cada frame y reproducción/copia desde fixture RF preparado; el segundo aísla entrega y no mide generación. No copiar una implementación del pool/generador y atribuirle resultados del componente productivo.

En S6 aumentar tasa ofrecida por etapas, repetir alrededor del punto de saturación y registrar ofrecida, aceptada y completada, ocupación/esperas y latencias. Una cola acotada puede permanecer llena con throughput estable: eso no acredita sostener la tasa ofrecida. Publicar intervalo observado de tasas sostenidas, duración y variabilidad, no un máximo universal. La tolerancia de seguimiento y el margen se propondrán después del piloto. Separar el límite de generación del de entrega; no extrapolar a varios canales ni a GigE/PCIe.

Cruzar S1–S6 con visual ausente, observador rápido y observador lento controlado. Aplicar además parada con canal lleno, cierre durante proyección, cierre con callback activo y Start/Stop repetido. No hace falta multiplicar todos los parámetros en un único ensayo; mantener IDs de variantes y cambiar una dimensión cada vez.

## Nivel 1: microbenchmarks reproducibles

Preparar configuración, metadatos, fixtures y buffers fuera de la región cronometrada cuando no sean el objeto medido. Consumir resultados/checksums para evitar eliminación de trabajo. Separar estado frío (construcción de pool/run) de régimen estable. N=1, 1.024, 1.025, 2.048 y 65.535; presupuestos visuales 4, 256 y 1.024. Señales sintéticas RF, constante y alternancia de extremos para cubrir reducción con distinta salida.

| Operación | Región medida y límites |
| --- | --- |
| Generación RF | Fill sobre buffer existente; semilla, secuencia y ruido registrados; checksum fuera del tramo. Sin temporización de fuente |
| Alquiler/devolución | RentAsync + Dispose sin contención; medir asignación del owner aunque el array se reutilice. Agotamiento y espera son otro caso de carga, no mezclarlo |
| Frame | Construcción + Dispose con owner válido nuevo por operación; subcaso combinado con pool y subcaso con doble mínimo que identifique su overhead. No reutilizar un owner dispuesto |
| Proyección min/max | Project con datos preparados; incluye asignaciones reales de List/array/snapshot y preservación primero/último/extremos. No descontar asignaciones que ocurren en producción |
| Mailbox latest-only | Medir Accept sin interés, Accept con proyector real y caso con proyector doble de coste controlado que devuelva snapshot independiente preparado. Este último incluye admisión/locks/mailbox, no es coste puro del mailbox. Aislar reemplazo determinista del worker; publicación concurrente se mide en nivel 2 |
| Coordenadas WPF | Map con 0/1/256/1.024 puntos, dimensiones normales/cero, señal constante. Separar asignación de coordenadas de DrawingContext, Dispatcher, GPU, DPI y resize; no presentarlo como FPS |

Acceso pendiente: SyntheticRfGenerator, BoundedSampleBufferPool y AScanCoordinates son internos. Proponer acceso de ensamblado amigo restringido en una futura tarea; no hacer públicos estos tipos, usar reflexión en el tramo medido ni duplicar algoritmos. Si no se autoriza acceso, declarar el microbenchmark individual bloqueado y medir únicamente la API pública compuesta. El mailbox no ofrece operación aislada pública; cualquier seam adicional requiere revisión previa.

Coordenadas viven en App.Wpf: no referenciarlo desde ejecutables neutrales. Diferir este caso a un runner opcional `UTStudio.Benchmarks.Wpf` net10.0-windows, sujeto a aprobación de proyecto y acceso interno; si no se aprueba, dejar medición matemática pendiente y conservar pruebas funcionales WPF existentes. No extraer código productivo solo para medirlo.

## Nivel 2: carga del pipeline

Ejecutable propio, proceso separado y tiempo real monotónico para rendimiento. Las pruebas de corrección usan reloj manual y señales. Nunca interpretar una ejecución de reloj manual como medición de CPU/latencia del sistema real.

1. Simulador -> ApplicationSession -> AScanVisualDelivery, primero sin interés y después con observador rápido. Mismas configuraciones y ventanas de medida; confirmar proyecciones cero sin interés y distinguir Drop visual de pérdida UT.
2. Observador lento: retener un callback con señal controlada, dejar avanzar fuente/sesión y observador sano, medir coalescencia y buffers. Soltar en finally. En carga temporal usar una duración configurada y registrada, no sleeps como condición de corrección. No esperar Dispose del suscriptor retenido antes de soltarlo: es una barrera que puede esperar código externo.
3. Backpressure: doble de sink retenido dentro de Accept, sin conservar span tras retornar, con señal de entrada/salida. Llenar Channel y observar productor esperando WriteAsync; cancelar antes de esperar productor y dejar que el lector drene. El observador visual lento por sí solo no debe llenar el Channel.
4. Pool agotado: ensayo de componente fuente/pool, separado del pipeline integrado. Allí el harness es el único lector en lugar de Application y retiene préstamos hasta BufferCount; esperar reserva cancelable y devolver todos en finally. En pipeline normal registrar high-water sin exigir que alcance ocho: la cola y sus propietarios pueden limitarlo antes.
5. Stop/shutdown bajo carga: detener durante generación, reserva, escritura bloqueada, proyección y callback. Medir hitos separados: solicitud, ProducerCompletion, consumidor drenado, AllFramesReleased, desconexión y disposición. ProducerCompletion no implica buffers devueltos. Para forzar separación usar el ensayo de fuente con préstamo retenido.
6. La herramienta neutral mide disposición sesión -> fuente -> visual, y lo etiqueta como cierre neutral. Cierre completo ViewModel/suscripciones -> sesión -> fuente -> visual -> Host -> MainWindow se verifica en escenario WPF separado, con Dispatcher vivo; no fingir que el ejecutable neutral mide el Host o UI.

Cinco segundos del cierre WPF siguen siendo exclusivamente diagnósticos. Un watchdog del arnés detecta falta de progreso, registra fase, balances y tareas pendientes, y declara ejecución fallida/inconclusa; no devuelve préstamos a la fuerza ni comunica cierre correcto. Si se termina un proceso colgado por operación externa, registrar esa terminación como fallo y preservar evidencias.

## Nivel 3: pruebas prolongadas

Duraciones propuestas de campaña, no requisitos de producto: piloto de 60 s de calentamiento + 5 min medidos, tres procesos independientes por caso; soak de 1 h y después 8 h en máquina dedicada; 1.000 ciclos Start/Stop como campaña separada. Aprobar duración/coste antes de ejecutar. Adaptar warmup según estabilización observada y registrar exclusiones. No obligar a ejecutar toda la matriz durante ocho horas.

Seleccionar S1, el caso largo más exigente admisible y un caso experimental sostenible si se autoriza. Registrar CPU/GC/memoria periódicamente (propuesta 1 s), progreso, high-water, balances y diagnósticos. Repetir con interés visual presente/ausente y suscripción/desuscripción; terminar durante carga sostenida. Observar estado tras cada Stop y tras cierre final; cada ejecución usa RunId nuevo.

Examinar pendientes de memoria administrada, heap vivo después de GC naturales, LOH y working set en ventanas sucesivas tras calentamiento. No confundir heap reservado, asignación acumulada y memoria retenida ni exigir working set decreciente inmediato. Una tendencia persistente requiere investigar raíces/owners antes de declarar estable. No forzar GC en el tramo medido; una colección/volcado posterior es diagnóstico separado y etiquetado. Comparar número de ciclos también: pool por run y cachés iniciales no son una fuga demostrada.

Para bloqueos, watchdog basado en falta de progreso cuando hay demanda, no en inactividad esperada sin suscriptores o durante una pausa controlada. Capturar fase y balance; observar explícitamente todas las tareas del harness. Un contador UnobservedTaskException complementa pero no demuestra por sí solo ausencia de excepciones no observadas.

## Métricas, instrumentación y unidades

Las siguientes sondas son propuestas, no telemetría disponible completa. [SessionSnapshot](../../src/UTStudio.Contracts/Application/SessionSnapshot.cs) publica contadores en transiciones, no cada frame; no derivar throughput instantáneo sondeándolo. [AScanDeliveryStatistics](../../src/UTStudio.Visualization.Core/AScanDeliveryStatistics.cs) sí permite lectura acumulada bajo demanda. WaitReason y OutstandingBuffers son internos; el contador exacto de escrituras/generaciones y sus marcas temporales no está expuesto. Antes de implementar aprobar sondas internas mínimas, sin cambiar IUtFrameSource ni introducir otro lector.

| Métrica | Definición / origen propuesto |
| --- | --- |
| Generados y producidos | G: frames construidos; P: escrituras aceptadas por Channel, llamados producidos entregados. U: generados no transferidos y liberados por productor. G=P+U al finalizar; separar reservas canceladas antes de construir frame |
| Consumidos y liberados | C: frames leídos por Application, incluido drenaje; L: Dispose correcto por ese lector. Al finalizar P=C=L; devoluciones totales del pool incluyen también préstamos nunca publicados, no exigir que sean iguales a P |
| Buffers | Rent/return exitosos, pendientes y máximo simultáneo; balance final cero y AllFramesReleased observado tras sellado. Distinguir buffer físico, lease/owner y frame |
| Visual | Deltas Received, Published, Replaced, Dropped, ObserverCoalesced/ObserverErrors; Published es commit al store, no OnNext ni render. Contar callbacks y dibujos separadamente si se instrumentan |
| Throughput | ΔP/Δt y ΔC/Δt, A-Scans/s; bytes de payload efectivos Σ(N×2)/Δt. Misma ventana monotónica; tasa ofrecida y efectiva separadas |
| Latencia | Media/p50/p95/p99 para generación, espera de reserva, espera de escritura, cola, proyección y hasta entrada en callback; identificar los extremos de cada medida |
| Proyección | Inicio/fin de Project mediante decorador IAScanProjector; llamadas, ns/frame y ns/muestra. No incluye espera del mailbox; medir también Accept completo |
| Asignación | Bytes por operación micro; en proceso multihilo Δbytes asignados/ΔC y /Δproyecciones, denominador explícito. Medir fase fría aparte y coste del colector |
| GC/LOH | Gen0/1/2, pausas, tamaño y asignación/fragmentación LOH mediante runtime/traza; LOH no es una generación adicional. Normalizar conteos por tiempo/frames cuando proceda |
| Memoria | Heap administrado usado/reservado, working set actual/máximo y private bytes cuando estén disponibles; series con unidades bytes/MiB explícitas |
| Stop/shutdown | Duración monotónica desde solicitud hasta cada hito; éxito/error y fase, repetición idempotente, diagnóstico de cinco segundos sin SLA de finalización |
| CPU | Δtiempo CPU proceso/Δtiempo pared: equivalentes de núcleo; además porcentaje normalizado por procesadores lógicos. Media y máximo de intervalos de 1 s, no supuesto pico instantáneo |

Tiempos locales con Stopwatch/TimeProvider real del mismo proceso; UTC solo correlación externa. El elapsed del frame marca disparo después de obtener buffer, no incluye espera de reserva ni tiempo ofrecido perdido por backpressure. Añadir reloj de demanda del harness para exponer esa espera y evitar ocultar saturación (omisión coordinada). No sumar percentiles de etapas para estimar p99 total ni promediar p99 de repeticiones como si fuese un percentil agregado; conservar cada distribución y su población.

Latencia visual solo existe para frames seleccionados: informar tamaño de población, sustituciones, descartes y edad de la curva observada. No asignar latencia cero a reemplazados, ni presentar esa distribución como latencia de todos los adquiridos. Publicación al store requiere sonda de commit; callback requiere marca distinta. La versión visual puede tener huecos; correlacionar SourceId/RunId/canal/Sequence y Version sin exigir secuencias contiguas en visualización.

Los contadores visuales son de vida del servicio, los de sesión por run: tomar deltas y snapshot terminal tras barreras. No imponer `Received = Published + Replaced` porque hay descartes, pendientes y carreras de cierre; verificar balances por eventos/categorías instrumentados en ensayo quiescente. Fallos visuales siguen siendo VisualError/StatusChanges, no fallo de adquisición automático.

Contadores de integridad exactos por evento; publicación de métricas muestreada. Usar histogramas/reservorios acotados con algoritmo, resolución y error declarados; percentiles con pocas observaciones se etiquetan insuficientes, no conclusiones. No acumular cada frame o snapshot durante soak. Comparar corrida instrumentada con mínima instrumentación para medir perturbación; no añadir logging/JSON por frame al camino crítico.

## Herramientas y separación de mediciones

| Origen | Adecuado para | No demuestra |
| --- | --- | --- |
| BenchmarkDotNet propuesto | Distribución de tiempo por operación, repetición/warmup, bytes/op y GC mediante MemoryDiagnoser | Latencia individual extremo a extremo, LOH retenido, working set sostenido, FPS o ausencia de fugas |
| Contadores/sondas del pipeline | Balances, secuencias, esperas, high-water, throughput, histogramas por frame/hito, shutdown | Uso global de CPU/GPU sin medición externa |
| Runtime/proceso/SO | Asignación total multihilo, GC/pausas/LOH, CPU, working set; dotnet-counters/dotnet-trace/PerfView/WPR si están disponibles y autorizados | Integridad UT solo por observar memoria plana |
| MSTest determinista | Propiedad exacta, cancelación, min/max, admisión visual, barreras y errores | Presupuesto de rendimiento de una máquina |
| Soak y sesión WPF manual | Tendencias, cierre real, Dispatcher/dibujo, variaciones térmicas/DPI | Prueba universal de estabilidad o transporte real |

BenchmarkDotNet está justificado para comparaciones aisladas repetibles, no para encapsular el soak. Proponer referencia exclusivamente en `benchmarks/UTStudio.Benchmarks` (y solo si se aprueba, su runner .Wpf); nunca en src, Contracts o tests normales. Versión estable compatible con .NET 10 por verificar y aprobar al implementar; ningún paquete añadido ahora. Los [diagnosers oficiales](https://benchmarkdotnet.org/articles/configs/diagnosers.html) incluyen memoria/GC; sus cifras no sustituyen telemetría multihilo del pipeline. Para medición usar Release sin debugger y controlar entorno, conforme a las [buenas prácticas oficiales](https://benchmarkdotnet.org/articles/guides/good-practices.html).

Alternativa sin paquete: ejecutable con Stopwatch.GetTimestamp, calentamiento, lotes, checksum, repeticiones en procesos separados, GC.CollectionCount y mediciones de asignación del runtime. Hay que mantener manualmente metodología, exportación y análisis; no atribuir bytes de otros hilos a GetAllocatedBytesForCurrentThread ni tratar promedio de lote como percentil por frame. Esta alternativa permite empezar, con menor automatización y más riesgo metodológico.

## Organización y comandos propuestos

Nada de lo siguiente existe por autorización de este documento. Propuesta de archivos futuros:

- `benchmarks/UTStudio.Benchmarks/`: csproj ejecutable net10.0, Program, Generation/Pool/Frame/Projection/Delivery benchmarks, SyntheticFixtures y configuración de jobs. Referencias solo a Domain, Contracts, Simulator y Visualization.Core que cada caso necesite.
- `tools/UTStudio.LoadTests/`: csproj ejecutable net10.0, Program, ScenarioConfiguration, PipelineRunner, LoadFrameSource experimental, MetricsCollector, ResultWriter y fixtures; referencias neutrales incluyendo Application. No referencias WPF, ViewModels ni Host productivo. No dependencias inversas desde src.
- Runner matemático/UI .Wpf opcional separado y pendiente, sin importar WPF transitivamente en herramientas neutrales. No crear una abstracción general de benchmarking en producción.

No usar Microsoft.NET.Test.Sdk ni atributos de test en estos ejecutables; no llamarlos desde targets de build/test ni desde MSTest. Preferir dejarlos fuera de UTStudio.sln inicialmente y ejecutarlos por ruta. Soak exige selección explícita de perfil/duración, nunca valor predeterminado al ejecutar `dotnet test`. CI compartida solo pruebas de corrección y smoke explícito corto; rendimiento y soak en jobs opt-in dedicados.

Sintaxis futura ilustrativa; no ejecutar estos comandos ahora. Los flags propios se implementarán y validarán antes de documentarlos como operativos:

```powershell
# Debug: corrección y smoke, nunca resultados comparables de rendimiento
dotnet build UTStudio.sln -c Debug
dotnet test UTStudio.sln -c Debug
dotnet run -c Debug --project tools/UTStudio.LoadTests -- --profile smoke --scenario S1 --duration 10s --output <directorio-externo-unico>

# Release: primero build; luego medir sin debugger en proceso dedicado
dotnet build benchmarks/UTStudio.Benchmarks/UTStudio.Benchmarks.csproj -c Release
dotnet run -c Release --no-build --project benchmarks/UTStudio.Benchmarks -- --filter '*Projection*' --artifacts <directorio-externo-unico>
dotnet build tools/UTStudio.LoadTests/UTStudio.LoadTests.csproj -c Release
dotnet run -c Release --no-build --project tools/UTStudio.LoadTests -- --profile baseline --scenario S2 --warmup 60s --duration 5m --output <directorio-externo-unico>
dotnet run -c Release --no-build --project tools/UTStudio.LoadTests -- --profile soak --scenario S4 --warmup 60s --duration 8h --output <directorio-externo-unico>
```

S3/S6 solo seleccionables al existir la fuente experimental aprobada; rechazar perfiles incompatibles con mensaje explícito. Excluir resultados Debug de comparaciones. Las pruebas largas son automatizables técnicamente, pero su lanzamiento/interpretación serán deliberados, no parte implícita de CI.

## Salidas y comparación con baseline

Directorio absoluto externo al checkout y único por ejecución (fecha + GUID). No versionar resultados brutos, capturas, binarios, fixtures grandes, dumps o trazas. Futuro `manifest.json` con esquema/versionado de formato, commit y estado dirty, escenario/parámetros, semilla/algoritmo, reloj, OS/CPU/RAM, SDK/runtime/GC, arquitectura, build, energía, warmup/duración/repeticiones, herramientas y frecuencia de muestreo. `summary.json`, series `metrics.csv`, histogramas con límites/conteos, eventos de ciclo de vida `events.jsonl`, exportaciones BDN y trazas opcionales; distinguir éxito, fallo e inconcluso. No serializar variables de entorno completas ni secretos del Host.

Fixtures exclusivamente sintéticos, semilla/algoritmo/hash descritos; generar fuera de Git, no datos reales. Usar datos preparados solo cuando el escenario lo declare. Retención/volumen de artefactos pendiente de aprobación; liberar temporales en finally y conservar explícitamente resultados solicitados. No modificar .gitignore en esta tarea; futura implementación validará que la ruta no cae dentro del checkout antes de medir.

La primera campaña establece baseline, no un aprobado por cifras inventadas. Repetir referencia y candidato en la misma máquina/OS/runtime/configuración/energía; alternar orden para detectar deriva térmica. Comparar medianas y dispersión entre procesos, deltas absolutos/relativos y distribución de latencias, además de balances. No mezclar cambios de runtime con cambios de código sin etiquetarlos. Una regresión fuera del ruido observado requiere investigación/repetición; no fallar CI compartida por límites temporales rígidos. Versionar posteriormente solo un informe sintético revisado en docs/performance, con manifest resumido y ubicación de artefactos, en otra tarea autorizada.

## Criterios funcionales obligatorios y aceptación

- Cero frames aceptados sin consumir/liberar al terminar; cero buffers prestados y barrera AllFramesReleased confirmada después de ProducerCompletion y drenaje. Ninguna devolución doble ni uso posterior a Dispose.
- Cero pérdidas silenciosas en adquisición: orden/secuencias por run y balances exactos. Un frame local no aceptado por cancelación es liberación del productor contabilizada, no pérdida oculta. Bajo fallo inyectado el error debe ser observable y la limpieza verificable.
- Sustituciones/coalescencia/descarte permitidos solo en visualización y contabilizados; sin extrapolar esa política a futura persistencia. Máximo de puntos, invariantes min/max y límites de publicación conservados.
- Memoria y número de préstamos estabilizados en carga sostenida: investigar crecimiento persistente tras warmup; sin criterio numérico absoluto hasta disponer de series y variabilidad. Una campaña corta o ruidosa se declara inconclusa, no éxito de soak.
- Stop/cierre completados y diagnosticados, o fallo explícito con recursos pendientes identificados; no declarar éxito por expirar un timeout. Cancelación mientras hay backpressure funciona sin cancelar al único lector antes del drenaje.
- Ningún callback nuevo tras cancelar la suscripción/completar disposición; callback activo puede prolongar la espera externa. No retener muestras UT. Error terminal visual visible sin otro A-Scan y sin convertir automáticamente la adquisición en Faulted.
- Ninguna excepción no observada; tareas observadas, fallos primarios preservados, diagnósticos separados. Los errores del temporizador de cierre no sustituyen la limpieza.

La corrección funcional bloquea aceptación aunque los números sean rápidos. CPU, memoria, latencia y tasa sostenible son resultados a medir; no se aprueban presupuestos absolutos en esta fase.

## Secuencia y decisiones pendientes

1. Aprobar estructura de herramientas y BenchmarkDotNet o alternativa sin paquete; después concretar versión y acceso interno/sondas, con revisión de contratos si resultara imprescindible.
2. Implementar smoke e integridad determinista antes de medir; sin fuente de carga superior, ejecutar solo escenarios admitidos y declarar S3/S6 pendientes.
3. Piloto Release, medir overhead de instrumentación, guardar línea base de la máquina y ajustar duración/resolución; después carga y soak dedicados.
4. Revisar resultados y aprobar presupuestos/reglas de comparación. Solo entonces proponer optimizaciones previas al mailbox o cambios de capacidad/pool. Ninguna optimización queda seleccionada por este plan.

Requieren aprobación: paquete/versión, proyectos y accesos internos futuros; fuente de carga experimental y sus modos; runner .Wpf opcional; máquina de referencia, duración/coste de campañas y retención de artefactos. Los umbrales de CPU/memoria/latencia y tolerancias de tasa se decidirán tras la primera medición. No se requiere cambiar ahora ADR de ownership ni los límites productivos.

Validación de esta tarea: documental únicamente (enlaces, consistencia, alcance y whitespace). No se ejecutan build, tests funcionales, microbenchmarks ni carga; no hay cifras medidas en este documento.
