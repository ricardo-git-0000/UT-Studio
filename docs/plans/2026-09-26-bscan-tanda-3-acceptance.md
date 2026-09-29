# Tanda 3 — Cierre y aceptación del primer incremento B-Scan

Estado: propuesta para aprobación. Base: `feature/bscan-first-increment` en
`b6e386ebc5031b2983a0314308b634e046951d07`.

## Objetivo y límites

La Tanda 3 no añade funcionalidad. Cierra el incremento existente mediante aceptación visual manual,
estabilidad, accesibilidad, validación automatizada final y evidencia diagnóstica de rendimiento. Un
fallo descubierto se clasifica y corrige en el contrato ya aprobado; cualquier capacidad nueva vuelve
a planificación y no forma parte de esta tanda.

Quedan expresamente fuera: archivos UT reales; encoder y eje físico definitivo; almacenamiento; GPU;
phased array; paquetes nuevos; discontinuidades localizadas sin metadata neutral autoritativa; y nuevas
modalidades C-Scan, S-Scan o D-Scan.

## Artefactos documentales

Durante la definición se modifican únicamente:

- este plan de aceptación;
- el [plan maestro](2026-09-22-bscan-first-increment.md), para enlazar la Tanda 3;
- el [resumen de Fase 5](../progress/2026-09-23-bscan-phase-5-wpf.md), para registrar que la aceptación
  queda pendiente.

Al ejecutar la tanda se creará `docs/progress/AAAA-MM-DD-bscan-first-increment-acceptance.md` con
entorno, resultados, incidencias, capturas autorizadas, métricas y dictamen. Solo se actualizarán ADR o
arquitectura si aparece una contradicción real; una medición no se convertirá por sí sola en decisión.

## Entorno y evidencia reproducible

Registrar antes de cada campaña: commit, árbol limpio, Windows y .NET SDK, CPU/RAM/GPU, resolución,
monitores, escalado DPI, configuración del simulador, duración y modo Release. Usar únicamente señal
sintética. Las capturas no deben contener datos reales ni secretos.

Cada caso manual se registra como `Pass`, `Fail` o `Blocked`, con hora, DPI, pasos, resultado observado
y referencia a incidencia. `Blocked` no cuenta como aprobado. Las métricas se guardan como resultado
experimental, separadas de cualquier baseline contractual.

## Aceptación visual manual reproducible

Preparación común:

1. Compilar Release, iniciar UT-Studio con el simulador y abrir `MainWindow`.
2. Iniciar una adquisición y abrir `BScanWindow`.
3. Confirmar visualmente que A-Scan RF y raster B-Scan se actualizan simultáneamente. La unicidad del
   lector de fuente no es observable en este recorrido y se acredita con `BScanSessionLifecycleTests`.
4. Repetir el recorrido a 100 %, 125 %, 150 % y 200 % de escalado de Windows. Reiniciar la aplicación
   tras cambiar el DPI cuando Windows o WPF lo requieran. Registrar DPI efectivo 96/120/144/192,
   monitor, transición per-monitor, tamaño en DIP y tamaño físico después de `OnDpiChanged`.

Recorrido por cada DPI:

1. Seleccionar en B una columna identificable y comprobar que A cambia a `Selected`, conserva la traza
   escogida mientras B sigue live y muestra identidad/estado coherentes.
2. Activar `ReturnToLive` y comprobar retorno inmediato al último A-Scan live compatible.
3. Aplicar zoom temporal desde A y verificar el cambio correspondiente en el eje temporal de B; repetir
   desde B hacia A. Comprobar ancla bajo puntero, límites y ausencia de bucles.
4. Aplicar zoom espacial B, pan temporal y espacial, reset parcial y reset total. Verificar cursores,
   prioridad selección/pan y cancelación con `Escape` o pérdida de captura.
5. Redimensionar ambas ventanas desde tamaño estrecho hasta el área disponible. Confirmar raster,
   overlays, ticks, labels y hit testing sin excepciones ni contenido esencial inaccesible.
6. Cerrar B durante live; confirmar que A y la sesión continúan. Reabrir B y confirmar bootstrap desde
   el store activo, consumidor nuevo y datos recientes.
7. Con 1.024 trazas y el ring lleno, reducir y aumentar repetidamente el tamaño B durante live. Confirmar
   que Stop, cierre, botones y ratón responden, que cada cambio admite una sola resincronización full y
   que después reaparece el desplazamiento incremental sin huecos negros.
8. Detener adquisición sin hacer zoom ni resize. Seleccionar inmediatamente una columna de cualquier
   parte del rango residente: el cursor y el A-Scan exacto deben aparecer en la misma interacción.
   Verificar después zoom, pan y resize sobre la imagen congelada y `ReturnToLive` sobre la última señal.
9. Ejecutar cierre normal desde `MainWindow`: primero se bloquea interacción, después se completa el
   orden contractual y ambas ventanas terminan cerradas.
10. El cleanup no confirmado no dispone hoy de una entrada diagnóstica en la aplicación —añadirla sería
   funcionalidad productiva fuera de alcance—. Su aceptación se obtiene con las pruebas WPF existentes
   `Productive_shutdown_waits_for_raster_owner_before_disposal_and_host` y
   `Failed_block_return_keeps_productive_cleanup_unconfirmed_and_shell_visible`, que controlan owner,
   Host y visibilidad de `MainWindow`. El usuario debe decidir si esa evidencia automatizada es
   suficiente o si autoriza un harness interactivo exclusivamente de test; hasta entonces el subcaso
   visual manual se registra `Blocked`, no `Pass`.

Resultado cualitativo exigido: no parpadeo persistente, raster corrupto, mezcla de runs, salto de cursor,
zoom divergente A/B, comandos tardíos, cierre prematuro ni error silencioso.

## Estabilidad

### Campañas

- Smoke: 10 minutos, una ventana B, interacción básica y un cierre normal.
- Continuada: duración propuesta de 60 minutos con simulador y actualización live constante.
- Ventanas: 50 ciclos propuestos de abrir B, interactuar, cerrar B y comprobar que A/sesión continúan.
- Selección bajo carga: alternar selección y `ReturnToLive` durante publicaciones y durante al menos dos
  wraps completos del ring.
- Interacción bajo wrap: mantener zoom/pan/cursor mientras el rango residente avanza y verificar full
  recovery cuando la base incremental deja de ser compatible.

No usar `Thread.Sleep`, retardos arbitrarios ni sondeo ilimitado en automatización. En manual, las
duraciones son ventanas de observación y no sincronización funcional.

### Medidas y balances

Muestrear cada minuto: working set, private bytes, heap administrado, Gen 0/1/2, CPU de proceso,
threads, handles, publicaciones full/incrementales, descartes y contadores B-Scan. Registrar mediana,
p95, máximo y tendencia; no interpretar una retención estable de `ArrayPool` como fuga.

Al finalizar deben ser cero: buffers de fuente pendientes, owners raster, leases, callbacks activos,
operaciones de región, consumidores temporales, timers vivos y ventanas/adaptadores B. Estos balances
se acreditan mediante diagnósticos y asserts de las suites existentes, no leyendo contadores inexistentes
en la UI productiva. Los bloques del ring deben presentar exactamente un intento de devolución y todas
las devoluciones confirmadas. La campaña manual registra memoria/CPU/handles de proceso por separado.

Criterios cuantitativos propuestos, pendientes de aprobación del usuario: tras 10 minutos de warm-up,
la regresión lineal de private bytes durante los últimos 30 minutos no superará 1 MiB/min y el valor
final no excederá en más de 10 % la mediana posterior al warm-up. Para CPU, ninguna secuencia de cinco
muestras consecutivas de un minuto superará el 90 % de CPU total de la máquina y no habrá degradación
visible de interacción. Se registran p50/p95/máximo. Sin aprobación o sustitución expresa de estos
umbrales, la salida de estabilidad queda `Blocked`; los resultados siguen siendo diagnósticos, no una
baseline contractual.

## Accesibilidad y acabado

- Recorrer sin ratón las acciones que ya tienen contrato de teclado: abrir/cerrar B, `ReturnToLive`,
  zoom, pan, reset y cierre. El orden de tabulación debe ser estable y no crear trampas de foco. La
  selección B→A continúa siendo por clic según ADR 0013; exigir selección por teclado sería una nueva
  capacidad y se mantiene fuera hasta una decisión explícita.
- Todo control accionable debe mostrar foco visible y nombre accesible descriptivo; validar nombre,
  rol, estado y valor con una herramienta de inspección UI Automation disponible en el entorno.
- Medir contraste de texto normal, texto grande, foco y componentes con la herramienta que apruebe el
  usuario. Criterio propuesto: 4,5:1 para texto normal y 3:1 para texto grande, componentes gráficos y
  foco. Evaluar fondo/primer plano de raster bipolar, cursores, selección, aviso de discontinuidad,
  labels/ticks, botones y foco en cada estado habilitado, deshabilitado, hover, seleccionado y con gap.
  Sin herramienta o sin medición reproducible el caso queda `Blocked`, nunca aprobado solo a ojo.
- En ventanas estrechas, ticks 1/2/5 y labels conservan unidad y extremos relevantes. Las etiquetas
  solapadas se suprimen o priorizan de forma determinista; no se permite alternancia entre frames con
  geometría estable.
- Repetir teclado, foco, hit testing y legibilidad en los cuatro DPI y después de resize.

## Matriz requisito/validación

| Requisito | Evidencia automatizada existente | Tanda 3 | Nueva prueba necesaria |
| --- | --- | --- | --- |
| A/B simultáneos y único lector | `BScanSessionLifecycleTests`, `BScanProductiveIntegrationTests` | Recorrido manual live | No; no duplicar combinaciones de integridad Core |
| Selección B→A y latest-wins | `BScanPresentationTests` | Selección visible y cambio durante live | Ninguna; la aceptación manual aporta la evidencia visual sin repetir Core |
| `ReturnToLive` | `AScanSelectedKeepsSignalDuringLiveAndReturnToLiveIsImmediate` | Confirmación visual/teclado | No |
| Zoom temporal A↔B | pruebas de viewport compartido y `BScanInteractionTests` | Ambas direcciones y ancla por DPI | No, salvo regresión descubierta |
| Zoom/pan/reset/cursor B | `BScanInteractionTests`, `BScanControlTests` | Gestos, prioridad y cancelación | No |
| Full/incremental, stale, wrap, recovery | `BScanVisualProtocolTests`, `BScanProductiveIntegrationTests` | Interacción durante wrap | No repetir matriz Core en E2E |
| Raster WPF transaccional | `BScanControlTests`, `BScanAdapterIntegrationTests` | Ausencia de corrupción visible | No |
| Ring lleno y cambio de tamaño live | `Reducing_full_live_ring_keeps_incremental_scroll_and_ui_work_bounded` y pruebas de protocolo/raster | 1.024↔tamaños menores, Stop/cierre y ratón responsivos | No; la regresión productiva reproduce el defecto |
| Selección congelada inmediata | `Incremental_snapshot_allows_selection_from_the_complete_resident_range`, `Frozen_productive_selection_updates_ascan_and_cursor_without_raster_refresh` | Stop, clic sin zoom/resize, cursor y A exacto | No |
| Resize, DPI y controles estrechos | `BScanControlTests` off-screen 96/144 DPI | 100/125/150/200 % real | Ninguna preventiva; una regresión real exige test dirigido |
| Cerrar/reabrir B con store activo | `BScanProductiveIntegrationTests`, `CompositionTests` | 50 ciclos propuestos | No; campaña manual/diagnóstica |
| Shutdown normal y no confirmado | `ShutdownTests`, `ShutdownRobustnessTests`, `CompositionTests` | Cierre real y ruta diagnóstica | No |
| Owners/leases/callbacks/timers | pruebas de ingress, delivery, publicación y composición | Balance final de campaña | No |
| Accesibilidad/acabado | bindings de navegación e interacción WPF existentes; render estrecho solo afirma ausencia de excepción | teclado, foco, inspector, contraste y labels | Sí: `BScanAccessibilityAcceptanceTests` para nombres UIA, foco/tab, comandos existentes y ejes estrechos a 96/120/144/192 DPI: extremos/unidades, no-solape y resultado estable |
| Memoria y CPU sostenidas | presupuestos y benchmarks existentes | campaña continuada con tendencia | Ninguna prueba funcional; herramienta externa pendiente de decisión y registro reproducible |
| Orden productivo 5→9 | `Productive_factory_observes_complete_shutdown_phase_order` | cierre real | No |

La única clase de pruebas nueva identificada antes de ejecutar la tanda es
`BScanAccessibilityAcceptanceTests`; puede contener casos separados de UIA/foco y geometría de labels.
No se añade otro E2E del flujo feliz: la evidencia visual será manual. Solo una regresión descubierta
justifica otra prueba dirigida. Identidad, gaps, pairing, stale, wrap, ownership y combinaciones
full/incremental permanecen en Core, donde ya son deterministas y exhaustivas.

## Repetición y flakiness

- Comandos base reproducibles:

  ```powershell
  dotnet build UTStudio.sln -c Release
  dotnet test tests/UTStudio.Core.Tests/UTStudio.Core.Tests.csproj -c Release --no-build
  dotnet test tests/UTStudio.Tests.Wpf/UTStudio.Tests.Wpf.csproj -c Release --no-build
  dotnet test tests/UTStudio.Core.Tests/UTStudio.Core.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~BScanVisualProtocolTests|FullyQualifiedName~BScanVisualPublicationTests|FullyQualifiedName~BScanPresentationTests|FullyQualifiedName~BScanIngressTests"
  dotnet test tests/UTStudio.Tests.Wpf/UTStudio.Tests.Wpf.csproj -c Release --no-build --filter "FullyQualifiedName~BScanControlTests|FullyQualifiedName~BScanInteractionTests|FullyQualifiedName~BScanAdapterIntegrationTests|FullyQualifiedName~BScanProductiveIntegrationTests|FullyQualifiedName~CompositionTests|FullyQualifiedName~ShutdownTests|FullyQualifiedName~ShutdownRobustnessTests"
  ```

- Build y suites Core/WPF: una pasada inicial; WPF completa tres veces consecutivas.
- Ejecutar tres veces cada uno de los dos comandos con filtro. Cada filtro debe descubrir y ejecutar al
  menos una prueba; cero pruebas seleccionadas es fallo de la campaña.
- Cualquier fallo se reproduce de forma dirigida al menos diez veces antes de clasificarlo; no se
  acepta reintentar hasta verde ni elevar timeouts sin causa demostrada.
- Criterio de salida: cero fallos, cero skips inesperados y cero resultados distintos entre repeticiones.
  Un fallo no reproducible queda como riesgo abierto y bloquea aceptación hasta su clasificación.

## Rendimiento

Tres niveles, sin mezclarlos:

1. Smoke: una ejecución Release para detectar excepciones, owners no liberados o tiempos anómalos; no
   produce cifras comparables.
2. Benchmark diagnóstico: `BScanVisualBenchmarks` para raster neutral full/incremental, wrap, reducción
   y admisión; `BScanWpfBenchmarks` para LUT, commit full, incrementales 1/8 columnas, rollback y render
   con overlays. Ejecutar los targets exactos y registrar entorno, warm-up, iteraciones, distribución
   y asignaciones:

   ```powershell
   dotnet run --project benchmarks/UTStudio.Benchmarks/UTStudio.Benchmarks.csproj -c Release --framework net10.0 -- --filter "*BScan*" --join
   dotnet run --project benchmarks/UTStudio.Benchmarks/UTStudio.Benchmarks.csproj -c Release --framework net10.0-windows -- --filter "*BScanWpfBenchmarks*" --join
   ```
3. Baseline aprobada: solo existe cuando el usuario acepta hardware de referencia, configuración,
   métricas y umbrales. Hasta entonces ningún resultado diagnóstico promete caudal o latencia máximos.

Límites contractuales: 32 MiB para trazas RF y 16 MiB para visual neutral. Los aproximadamente 12 MiB
de pico activo WPF por control 1.024×1.024 son una estimación de diseño que debe confirmarse, no una
baseline aprobada. La campaña debe confirmar contadores dentro de los límites configurados; working set
del proceso y retención de pools se informan aparte. Interacción y overlays se evalúan tanto
en latencia percibida como con el benchmark WPF existente, sin convertir una iteración fría en baseline.

## Criterios de salida

La Tanda 3 termina únicamente cuando:

- no quedan hallazgos P1 ni P2; los P3 están resueltos o aceptados explícitamente;
- `dotnet build UTStudio.sln -c Release` termina sin warnings;
- todas las pruebas Core y WPF pasan con las repeticiones definidas y sin flakiness pendiente;
- todos los casos manuales designados tienen `Pass` y están registrados; el diagnóstico de cleanup no
  confirmado se satisface mediante la evidencia automatizada aceptada expresamente por el usuario o
  mediante el harness interactivo autorizado; sin una de esas decisiones queda `Blocked`;
- balances finales son cero y no existe crecimiento sostenido según el criterio aprobado;
- documentación, resultados experimentales y riesgos están sincronizados;
- `git diff --check`, enlaces y `git fsck --full` son correctos;
- el árbol queda limpio después de los commits autorizados;
- una revisión final de `main...HEAD` y del árbol completo no detecta cambios accidentales;
- `main` es ancestro de `HEAD` y la rama queda preparada para `git merge --ff-only`, sin ejecutar el
  merge dentro de esta tanda salvo autorización expresa.

## Decisiones requeridas del usuario

Antes de ejecutar la aceptación deben confirmarse:

1. hardware/VM y resolución de referencia;
2. duración de la campaña continuada —propuesta: 60 minutos—;
3. número de ciclos de ventana —propuesta: 50—;
4. criterios propuestos de memoria —1 MiB/min y +10 % tras warm-up— y CPU —ninguna secuencia de
   cinco muestras de un minuto por encima del 90 % total—;
5. herramienta autorizada para CPU/memoria y para inspección de accesibilidad;
   la decisión debe incluir la herramienta de contraste y aceptar o sustituir los umbrales 4,5:1/3:1;
6. si los benchmarks siguen siendo solo diagnósticos o se aprobará una baseline en hardware concreto;
7. formato y ubicación de capturas/evidencias, evitando datos sensibles;
8. si la evidencia WPF automatizada basta para el diagnóstico de cleanup no confirmado o se autoriza
   un harness interactivo exclusivamente de test.

## Propuesta de commits

Sujeta a autorización expresa y siempre después de validar cada grupo:

1. `docs(bscan): define final acceptance campaign` — este plan, enlace maestro y estado de Fase 5.
2. `test(bscan): cover final acceptance gaps` — solo pruebas/harness realmente necesarios tras revisar
   la matriz; omitir si no aparece ningún hueco.
3. `docs(bscan): record first increment acceptance` — resultados manuales, estabilidad, rendimiento,
   revisiones y dictamen final.

Si la ejecución no requiere código ni pruebas nuevas, los commits 1 y 3 son suficientes. Esta propuesta
no autoriza crear commits, preparar archivos ni iniciar la campaña.
