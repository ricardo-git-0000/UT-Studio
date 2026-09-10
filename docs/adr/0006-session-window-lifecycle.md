# ADR 0006 — Sesión independiente de ventanas y cierre ordenado

Fecha: 2026-09-09. Estado: Aceptado por el usuario; implementación pendiente.
Trazabilidad: R02, R06–R08 y R13; concreta parcialmente Q12/Q13 de [cuestiones abiertas](../requirements/open-questions.md).

## Contexto

El primer incremento conecta una fuente simulada con una ventana WPF. Vincular la adquisición a esa ventana impediría cerrar vistas sin detener una sesión compartida. El flujo debe permanecer acotado incluso sin ventanas.

## Decisión

- La sesión activa pertenece a Application y su vida es independiente de las ventanas.
- Cada ventana posee su estado visual y suscripciones. Cerrarla libera esos recursos, pero no detiene adquisición ni destruye la sesión.
- Cerrar la última ventana tampoco constituye automáticamente una solicitud de cierre de aplicación. La composición WPF debe distinguir ambas acciones.
- Cerrar la aplicación solicita y espera una parada ordenada coordinada por Application antes de liberar los servicios y finalizar el proceso.
- Las operaciones largas son cancelables. La política concreta de aborto y timeout se definirá antes de implementar el cierre.

App.Wpf configura el host y las vidas de servicios, crea ventanas y transmite la solicitud de salida; no implementa la lógica de sesión. La parada ordenada detiene producción, completa el flujo, resuelve datos pendientes según la política de cada rama y libera recursos. No se detiene adquisición desde una desuscripción visual. Sin ventanas, el consumo/descarte visual mantiene memoria acotada.

En el incremento inicial no hay almacenamiento. Su futuro drenaje y cierre recuperable se incorporarán conforme al ADR 0003, sin equiparar datos recibidos, escritos y durables.

## Distribución de datos asociada

Un ChannelReader no se compartirá entre consumidores competidores. Las futuras ramas estarán separadas: procesamiento y almacenamiento sin pérdida silenciosa; visualización latest-only; métricas mediante muestreo. La publicación muestreada de métricas conserva los contadores exactos de integridad y no exige consumir cada payload.

El ownership compartido queda pendiente hasta introducir más de un consumidor y se resolverá antes de hacerlo. Esto no exime de definir propietario, transferencia y liberación del flujo inicial, incluidos sustitución latest-only, cancelación y fallo. El orden exacto entre procesamiento y almacenamiento no queda fijado. Véase [pipeline](../architecture/data-pipeline.md).

## Alternativas consideradas

- Hacer propietaria a una ventana de la sesión impediría cerrar vistas independientemente.
- Terminar la aplicación implícitamente al cerrar la última ventana confundiría dos acciones con efectos diferentes.
- Repartir un ChannelReader entre ramas distribuiría los frames entre lectores en lugar de difundirlos.

## Consecuencias y validación prevista

Las suscripciones visuales y el estado de sesión requieren vidas separadas. Se verificará que cerrar una ventana no detiene la fuente, que una vista lenta o ausente no aumenta memoria sin límite y que salir de la aplicación espera la parada y liberación. Core.Tests verifica la coordinación neutral; la integración real de cierre WPF requiere comprobación separada en Windows.

Complementa ADR 0001/0002 y [ADR 0005](0005-initial-solution-structure.md). No selecciona mecanismo de buffers compartidos ni garantiza cero pérdida ante saturación.

## Pendientes

Q12 queda parcialmente resuelta: propiedad y cierre aceptados; concurrencia de inspecciones, orden/versionado/hilo de snapshots y mecanismo accesible de salida/reapertura sin ventanas pendientes. Q13 queda parcialmente resuelta: políticas de ramas aceptadas; topología concreta, datos crudos/procesados, relación visualización/durabilidad y ownership compartido pendientes. También quedan por definir timeout, aborto y diagnóstico del cierre fallido.
