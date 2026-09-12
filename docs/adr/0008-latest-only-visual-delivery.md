# ADR 0008 — Entrega A-Scan latest-only y refresco visual acotado

Fecha: 2026-09-11. Estado: Aceptado por el usuario; implementación pendiente.
Trazabilidad: R01, R02, R06–R08 y R13; complementa [ADR 0005](0005-initial-solution-structure.md), [ADR 0006](0006-session-window-lifecycle.md) y [ADR 0007](0007-frame-source-ownership.md).

## Contexto y flujo

La adquisición puede producir más frames que los necesarios para dibujar. Su consumidor debe ser independiente de la velocidad UI, sin dependencia Application hacia Visualization.Core.

Flujo aceptado: simulador -> Channel -> sesión Application (único lector) -> mailbox latest-only -> coordinador Presentation -> transformación Visualization.Core -> snapshot visual -> AScanViewModel -> vista WPF.

## Decisión y ownership

Application mantiene un mailbox de capacidad uno expuesto mediante Contracts. Publicar sustituye y libera el pendiente anterior. Solo un coordinador Presentation toma frames con transferencia exclusiva; ventanas no compiten por reader ni mailbox.

El coordinador transforma fuera del hilo UI y libera en finally antes de cualquier espera de Dispatcher. El snapshot A-Scan posee almacenamiento independiente e inmutable, válido tras devolver y sobrescribir muestras originales. Sin pooling visual ni ownership compartido de buffers UT.

Sin interés visual se libera inmediatamente. Al parar se cierra atómicamente la entrega de la ejecución; una transformación ya iniciada termina/libera y no publica un resultado invalidado. Esto cubre arranque, desuscripción y fallo visual, no ejecución sin ventanas: cerrar MainWindow solicita salir conforme al ADR 0006.

## Modelo, escalas y reducción

AScanSnapshot, en Visualization.Core, contiene identidad de ejecución/canal, secuencia, timestamp, modo RF, cantidad original de muestras, escalas y puntos neutrales de tiempo/amplitud. Sin tipos WPF ni referencias al frame adquirido.

- Tiempo: `offset + i / fs`, segundos con etiquetas en microsegundos.
- RF: `100 × sample / 32768`, escala fija -100 % a +100 %. El máximo positivo Int16 queda ligeramente por debajo de +100 %.
- Sin autoscale por frame, profundidad, calibración a voltios ni representación definitiva de rectificadas.
- Máximo de 1.024 puntos; hasta ese tamaño conservar todas las muestras.
- Para N mayor, conservar primero/último; repartir interior en 511 grupos contiguos casi iguales y emitir mínimo/máximo en orden de índice original. Empates por primer índice; si ambos coinciden, emitir un punto. Salida de hasta 1.024 puntos.
- N=1: un dato; viewport con intervalo nominal de una muestra centrado en ese tiempo, sin inventar otro dato.

Reducción min/max visual O(N), no DSP ni datos para medidas UT. No se selecciona biblioteca gráfica.

## Cadencia, stores y suscripciones

El coordinador transforma como máximo a 30 Hz, sin solapamiento ni recuperar ticks atrasados. El mailbox sigue sustituyendo mientras transforma; el store visual conserva solo el snapshot reciente.

Un planificador neutral Presentation agrupa invalidaciones y permite un callback visual UI pendiente como máximo. El callback consulta el último snapshot al ejecutarse, sin capturar uno por publicación. Separar inicios efectivos de refresco al menos 1/30 s incluso tras bloqueo del Dispatcher. Limitar transformación no sustituye limitar refresco efectivo.

Snapshots de sesión inmutables, coherentes y versionados crecientemente. Suscripción inicial y actualizaciones ordenadas por versión; notificaciones serializadas fuera de locks y del productor. Stores latest-state: pueden agrupar versiones intermedias, no son registro de eventos. Observadores lentos/fallidos no bloquean adquisición ni generan colas ilimitadas.

Contadores de integridad y descartes actualizados por evento; publicación periódica a 5 Hz. Cambios de estado/resultados de comandos pueden notificarse inmediatamente sin refrescar curva fuera del límite. Distinguir descarte latest, ausencia de interés, coalescencia visual y discontinuidad de adquisición.

MainViewModel consume casos de uso/estado; AScanViewModel consume feed visual. Independientes entre sí, sin fuente hardware ni reducción en ViewModels. Propiedades observables cambian mediante puerto UI Presentation adaptado en App.Wpf. Desuscripción invalida generación; callbacks comprueban vigencia y RunId para no repintar ejecución anterior.

## Memoria y vida inicial

Una vista A-Scan en MainWindow. Arrays visuales propios asignados a frecuencia visual, máximo 30/s, no por frame adquirido. Presupuesto conservador: cuatro snapshots vivos de hasta 1.024 puntos de dos doubles, 65.536 bytes de puntos más objetos/metadatos. Referencias vivas acotadas no equivalen a límite duro de heap antes del GC. Sin históricos ni modelos retenidos en callbacks.

ViewModels y suscripciones iniciales tienen propiedad explícita desde composición de MainWindow; no crear scopes genéricos por ventana. La primera secundaria exigirá definir propiedad/suscripción y presupuesto global. Podrá compartir snapshots inmutables; cerrarla no detendrá adquisición ni añadirá lectores UT.

## Fallos, alternativas y validación

Fallo visual devuelve el buffer e informa/desactiva entrega afectada, sin detener automáticamente adquisición. Tareas/excepciones observadas. Compartir memoria UT con UI o procesar cada frame en Dispatcher incumpliría ownership y cadencia.

Probar min/max con orden de extremos, empates y N=1/1.024/1.025/máximo; independencia tras reutilizar buffer; sustitución/fallo de proyección; UI bloqueada sin acumulación; 30 Hz efectivos con reloj manual; contadores exactos publicados a 5 Hz; callback antiguo tras reiniciar y cierre concurrente. Renderizado real WPF se valida por separado. Pendientes: adaptador gráfico, benchmarks y presupuesto de ventanas secundarias. No hay pruebas ejecutadas en esta tarea documental.
