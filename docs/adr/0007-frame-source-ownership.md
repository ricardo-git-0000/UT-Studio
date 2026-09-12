# ADR 0007 — Contrato de fuente y ownership exclusivo de frames RF

Fecha: 2026-09-11. Estado: Aceptado por el usuario; implementación pendiente.
Trazabilidad: R02–R07 y R13; concreta [ADR 0002](0002-bounded-pipeline.md) y [ADR 0005](0005-initial-solution-structure.md). Cierre: [ADR 0006](0006-session-window-lifecycle.md).

## Contexto

El primer incremento necesita una fuente simulada, un único lector Application y transferencia auditable de muestras reutilizables. No diseña adquisición real ni fija representación de rectificadas reales. Los límites internos del simulador no son constantes del contrato general.

## Modelo aceptado

`ConventionalUtFrame`, en Contracts/Acquisition, es un objeto sellado con liberación idempotente, ownership exclusivo y `ReadOnlyMemory<short>` de exactamente SampleCount muestras RF bipolares. No es un struct propietario copiable. Tras publicar no se modifica su contenido. Memoria y vistas dejan de ser válidas al liberar; no pueden conservarse para uso posterior.

Domain define identidades, configuración y metadatos inmutables compartidos durante cada ejecución:

| Dato | Semántica |
| --- | --- |
| UtSourceId | Identidad estable de fuente lógica |
| AcquisitionRunId | Nueva por inicio, proporcionada por Application y controlable en pruebas |
| PhysicalChannelId | Canal físico tipado; inicialmente 0, sin inferir elemento, beam o ley |
| Modo | RF bipolar; representación binaria de rectificadas reales pendiente |
| SampleCount | Positivo, hasta 65.535 como límite de producto; capacidades/configuración concretas adicionales |
| SampleRateHz | Positiva y finita; límite de producto 100 MHz, sin garantía de máximos simultáneos |
| FirstSampleOffsetSeconds | Tiempo finito respecto al disparo; inicial 0 |
| RunStartedAtUtc | Origen civil informativo, no gobierna orden ni duración |

Cada frame añade Sequence de tipo ulong desde cero por ejecución y ElapsedSinceRunStart, tiempo monotónico del disparo. La secuencia se asigna al producir y no se renumera por descarte visual. Cambiar configuración exige parar e iniciar otra ejecución.

Muestras en cuentas digitales Int16 de -32.768 a 32.767, sin calibración a voltios. Tiempo de muestra: `offset + i / fs`; último punto: `offset + (N - 1) / fs`. Sin profundidad, geometría ni phased array. El contrato inicial no afirma que short represente futuras rectificadas ni fija su codificación, rango o conversión.

## Contrato IUtFrameSource

El puerto en Contracts expone identidad, capacidades reales y estado neutral. Operaciones largas cancelables:

- ConnectAsync: establece conexión lógica.
- ConfigureAsync: conectado, en reposo y sin recursos de run pendientes; validación íntegra antes de aceptar. Rechazar conserva la configuración anterior.
- StartAsync: recibe identidad de ejecución y devuelve UtAcquisitionRun sin esperar al primer frame ni al consumidor.
- El run proporciona un ChannelReader exclusivo, finalización del productor y barrera neutral AllFramesReleased.
- StopAsync: cancela las esperas del productor, espera su finalización y la terminación del writer; no espera al consumidor ni a AllFramesReleased.
- DisconnectAsync: desconecta tras limpiar la ejecución, coordinado por Application.
- DisposeAsync: cierre definitivo; no sustituye el drenaje del dueño del reader ni libera memoria prestada.

Channel nuevo por ejecución; nunca reutilizar uno completado. Application es el único lector. Fuente y sesión serializan transiciones; Start repetido se rechaza, Stop y Disconnect son idempotentes.

Conexión: Disconnected, Connecting, Connected, Disconnecting, Faulted. Adquisición: Idle, Starting, Running, Stopping, Faulted. Running exige Connected y configuración válida; los estados transitorios de conexión no admiten Running. Un fallo de adquisición puede conservar conexión, pero no autoriza reinicio: resolver recursos, desconectar y volver a conectar/configurar. No publicar Idle ni permitir configurar durante limpieza. Conservar error primario y errores de limpieza por separado.

## Inicio transaccional y cancelación

Antes de entregar exitosamente el descriptor, la fuente posee productor, canal y buffers: si Start falla/cancela, cancela producción, completa writer, drena el canal privado, devuelve buffers y observa tareas. No entrega un run parcialmente iniciado.

Tras la entrega exitosa, Application asume el reader incluso si se cancela la solicitud antes de instalar consumo. Debe consumir o ejecutar rollback/drenaje. El token de Start no gobierna el run tras el éxito; existe cancelación privada de ejecución. Cancelar la espera de Stop no interrumpe limpieza interna.

Application registra una cancelación para la operación de inicio completa, incluidas conexión y configuración. La salida cierra admisión y solicita esa cancelación antes de esperar la exclusión que pudiera retener el inicio. Registro y cierre de admisión se sincronizan; observar rollback antes de continuar la parada serializada, conforme al ADR 0006. Si el run ya se entregó, Application lo limpia mediante su cancelación privada y lector exclusivo.

## Transferencias y barrera

1. Productor reserva y genera.
2. Escritura aceptada transfiere al Channel. Si no se acepta, libera el productor. No decidir ownership solo por un token cancelado después de escribir exitosamente.
3. Application adquiere al leer; libera o transfiere al mailbox latest.
4. Sustituir libera el pendiente; extraer transfiere al único coordinador Presentation.
5. El coordinador transforma a almacenamiento visual independiente y libera en finally antes de esperar UI.

Publicar/sustituir/extraer/cerrar mailbox son atómicos. Cerrar rechaza y libera publicaciones tardías e impide nuevas extracciones; el frame ya extraído sigue perteneciendo al transformador. Sin interés visual se libera, no se acumula.

El pool se sella frente a reservas durante parada, sincronizado con reservas concurrentes. AllFramesReleased solo termina tras sellado y cero reservas vivas; no termina porque el pool esté vacío antes del primer frame. La barrera no exige acceso a Presentation/Dispatcher. No iniciar otro run/pool mientras la limpieza anterior esté pendiente.

## Saturación y presupuesto del simulador

`ChannelCapacity = 4` y `BufferCount = 8` son valores predeterminados configurables internos de Acquisition.Simulator, sujetos a benchmarks. No son propiedades, constantes ni garantías de IUtFrameSource.

El simulador usa un productor, un lector, continuaciones no síncronas y FullMode.Wait: puede ralentizarse de forma segura. Escritura y reserva observan la cancelación privada del productor. No extrapolar esta política a hardware futuro.

Para la topología actual, validar conservadoramente `BufferCount >= ChannelCapacity + 4`: cola más productor, lector, mailbox y transformación. Cada propietario retiene como máximo uno fuera de cola y la sustitución devuelve inmediatamente el anterior. Presupuestar con aritmética comprobada `BufferCount × SampleCount × 2` bytes de payload y `ChannelCapacity × SampleCount × 2` en cola. Valores iniciales: 32.768 bytes de muestras con N=2.048; no es memoria total del proceso.

Los buffers de longitud exacta se reutilizan. Envelopes/modelos visuales pueden asignarse: medir asignaciones, GC, ocupación, esperas y caudal antes de ajustar valores o afirmar rendimiento. Sin descarte en el Channel del simulador; descartes visuales contabilizados por separado.

## Parada, fallos y validación

El ADR 0006 fija cancelación inmediata del productor mientras el único lector drena. El productor completa writer al salir; el lector libera los aceptados hasta finalización. Nunca esperar al productor bloqueado sin cancelar su escritura ni cancelar prematuramente al lector. Si falla el consumidor, solicita cancelación y pasa a drenaje como único dueño del reader.

Completar con error conserva la causa y no evita liberar pendientes. Tras observar productor y lector, vaciar mailbox y esperar barrera. Timeout no autoriza devolver memoria usada ni simular finalización.

Se aplazan ownership compartido y representación de rectificadas; no se adopta el buffer ushort de la propuesta previa. Probar rollback a ambos lados de entrega del run, escritura exitosa concurrente con cancelación, Channel lleno, fallo consumidor, Publish/Take/Close, Rent/Seal, transformación activa y reinicios. Instrumentar devolución exactamente una vez, ausencia de uso tras liberar y máximo de reservas. Validar configuración interna y medir posteriormente. No se han ejecutado pruebas ni benchmarks en esta tarea documental.
