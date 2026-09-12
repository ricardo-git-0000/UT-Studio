# ADR 0006 — Cierre de MainWindow y parada ordenada de aplicación

Fecha original: 2026-09-09. Revisado: 2026-09-12.
Estado: Aceptado por el usuario con correcciones; implementación funcional pendiente.
Trazabilidad: R02, R06–R08 y R13; Q12/Q13 en [cuestiones abiertas](../requirements/open-questions.md).

## Contexto y sustitución

La revisión del usuario del 2026-09-11 sustituye expresamente la decisión anterior de mantener la aplicación funcionando al cerrar la última ventana: cerrar MainWindow significa salir de UT-Studio. La sesión sigue perteneciendo a Application, no a un ViewModel. App.Wpf transmite la solicitud de salida y coordina el ciclo de vida del Host.

## Decisión

- Cerrar MainWindow solicita parada ordenada, desconexión, liberación de recursos, parada y liberación del Host y cierre de la aplicación, también con adquisición activa.
- No implementar bandeja, `OnExplicitShutdown` ni ejecución sin ventanas.
- La composición usa `OnMainWindowClose`. En el primer evento Closing cancela el cierre sincrónicamente y observa una única tarea de salida asíncrona. MainWindow permanece abierta durante la limpieza; solicitudes repetidas se unen a esa tarea. Solo tras completarla se autoriza y reemite Close; el segundo Closing permite terminar WPF. No bloquear el Dispatcher con esperas síncronas.
- Una futura ventana secundaria liberará sus suscripciones y estado al cerrarse sin detener la sesión. Cerrar MainWindow seguirá significando salir aunque haya secundarias abiertas.
- No crear infraestructura genérica de scopes por ventana. La primera secundaria exigirá concretar propiedad, suscripciones y presupuesto antes de implementarse.

## Orden de parada y salida

Application serializa inicio y parada. La solicitud de salida impide nuevos inicios y mantiene el diagnóstico accesible en MainWindow.

Antes de esperar la exclusión de operaciones, la salida marca atómicamente que no se admiten nuevos inicios y cancela la operación de inicio vigente, incluidas ConnectAsync, ConfigureAsync y StartAsync. El registro de esa operación y la marca de salida se sincronizan para no perder una cancelación concurrente. Se observa su finalización y rollback según quién posea el run (fuente antes de entregarlo, Application después); entonces se continúa la limpieza serializada. No esperar la exclusión retenida por un inicio bloqueado para poder cancelarlo. Esta cancelación de inicio es distinta de la cancelación privada del productor de un run ya iniciado.

1. Marcar Stopping y cerrar atómicamente la entrega latest de la ejecución. Las publicaciones posteriores liberan sus frames; no se permiten nuevas extracciones. Una transformación ya iniciada conserva la obligación de liberar.
2. Solicitar inmediatamente cancelación del productor, incluidas esperas de reloj, reserva de buffer y escritura sobre Channel lleno. No esperar al productor antes de cancelar, ni cancelar el único lector.
3. Mantener el consumidor Application drenando y liberando mientras termina el productor. El productor libera su frame local no transferido y completa el writer en su salida normal o excepcional. `IUtFrameSource.StopAsync` espera al productor, no al drenaje ni a buffers retenidos por consumidores.
4. Observar la finalización del productor y del consumidor. Tras completar el writer, el consumidor resuelve todos los frames aceptados y observa el error de finalización si existe. Si falla su procesamiento, ese mismo responsable pasa a drenaje de limpieza; no se crea otro lector competidor.
5. Vaciar definitivamente el mailbox y esperar `AllFramesReleased`: pool sellado frente a nuevas reservas y todos los buffers devueltos. El coordinador visual libera en finally antes de esperar UI. Application espera esta barrera neutral sin depender de Presentation.
6. Desconectar la fuente. Invalidar callbacks y liberar suscripciones y recursos visuales. Detener y liberar el Host; finalmente permitir el cierre de MainWindow y la terminación WPF.

El sellado del pool y las reservas se sincronizan; no se concede una reserva tras sellar. No esperar tareas manteniendo bloqueos de mailbox/pool. El [ADR 0007](0007-frame-source-ownership.md) define ownership y rollback; el [ADR 0008](0008-latest-only-visual-delivery.md), la entrega visual.

Parar desde UI limpia la ejecución y deja la aplicación abierta; desconexión y parada del Host forman parte de salir. Una desuscripción visual no solicita por sí sola parada. Liberar sin interés visual durante arranque, fallo o cierre no habilita funcionamiento sin ventanas.

## Timeout, errores y cancelación

Cinco segundos es un umbral exclusivamente diagnóstico. Si se supera, MainWindow sigue abierta e informa la fase pendiente; la limpieza continúa observada. No matar el proceso, devolver buffers aún en uso, destruir el Host prematuramente ni declarar completada la salida. Una solicitud repetida no duplica limpieza. Si una fase falla y no se acredita su resolución, se mantiene cierre pendiente y diagnóstico.

Cancelar la espera del solicitante no interrumpe la limpieza interna. El token de Start no gobierna el run tras el éxito. Conservar error primario y errores de limpieza por separado; observar todas las tareas. No hay aborto forzado automático.

## Alternativas y consecuencias

La ejecución sin ventanas de la versión anterior queda sustituida por decisión explícita del usuario. La ventana solicita salida; Application conserva la lógica de sesión. Esperar al productor bloqueado antes de cancelar su escritura o detener primero al lector puede bloquear el cierre: cancelación del productor y drenaje avanzan concurrentemente.

## Validación prevista y pendientes

Probar Channel lleno, productor bloqueado, transformación activa, error del consumidor, inicio pendiente y solicitudes repetidas. Usar fuente doble con Connect/Configure/Start bloqueados para verificar que salida cancela antes de esperar exclusión y observa rollback; cubrir la carrera entre registrar inicio y solicitar salida. Verificar devolución exactamente una vez, barrera independiente del Dispatcher y diagnóstico a cinco segundos sin terminar proceso ni permitir reinicio. Integración real de Closing/Host/MainWindow en Windows; coordinación neutral en Core.Tests.

Quedan pendientes renderizado, paquetes adicionales y benchmarks. La propiedad de ventanas secundarias se concretará al incorporarlas. Persistencia y ownership compartido quedan fuera. No se han ejecutado pruebas funcionales por esta decisión documental.
