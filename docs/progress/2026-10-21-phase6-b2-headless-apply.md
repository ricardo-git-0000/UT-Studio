# Fase 6 B2 — Apply headless agregado

## Objetivo

Incorporar el caso de uso `Apply` headless sobre el FIFO común de Application, sin abrir todavía CAS,
`StoreOnly`, reprocesado histórico, Presentation ni WPF.

## Resultado

Application dispone de una operación agregada que prepara un único plan compilado para epoch live,
desired-replacement y autoridad; cruza un punto de no retorno coordinado con el lifecycle; confirma en el orden
epoch, desired-replacement y autoridad; y publica la autoridad después de reconciliar el terminal global. La
autoridad es el último commit y su notificación diferida es el último efecto de una operación aceptada.

La reconciliación de ordinales pertenece al runner FIFO. Cada operación admitida termina exactamente una vez y
en orden, incluidos rechazos semánticos y fallos. RF, gap, transición de run y Apply comparten la misma secuencia.

## Decisiones confirmadas

- La admisión continúa acotada como en B1. No se añade backpressure ni cola de waiters.
- La saturación del FIFO se representa mediante `GatePlanApplySubmissionStatus.CoordinatorCapacityExceeded`,
  que es `internal`, no consume ordinal y no se convierte en `CapacityExceeded(DerivedStore)`. No cambia la
  superficie pública de Contracts. Los consumidores previstos de B2 son Application y sus pruebas internas;
  Presentation/WPF permanecen fuera de alcance.
- Un conflicto o una validación fallida detectados antes de solicitar admisión completan localmente sin ordinal.
  Una vez admitida, cualquier terminación se reconcilia en el runner.
- El token del caller solo puede cancelar antes de la admisión o su propia espera. No cancela una operación ya
  admitida. B2 no incorpora un CTS operativo adicional porque los prepares/commits son síncronos y el arbitraje
  de lifecycle cubre el punto cancelable necesario.
- Stop y Apply arbitran bajo el gate de lifecycle de `ApplicationSession`: Stop antes del PNR cierra admisión y
  obliga a abortar las reservas con outcome `Closed`; Apply que cruza el PNR confirma el agregado completo y Stop
  espera el drain del coordinador antes de retirar la run.
- Un fallo de contrato después del PNR es una violación de invariantes, no un rechazo ordinario. Conserva el
  error primario, cierra la admisión global, resuelve las completions admitidas restantes sin ejecutar sus cuerpos
  y evita continuar RF/gap sobre un agregado potencialmente parcial.
- El puente B2 consume únicamente el puerto preparado `IPreparedGateStoreControl`, expuesto desde Processing
  mediante `InternalsVisibleTo("UTStudio.Application")`; no depende de `InMemoryGateDerivedStore`. La factory
  productiva preexistente conserva su composición concreta fuera de este puente. El caso de uso y sus servicios
  permanecen `internal`.

## Archivos modificados

- `src/UTStudio.Application/ApplicationCommitCoordinator.cs`
- `src/UTStudio.Application/ApplicationSession.cs`
- `src/UTStudio.Application/GatePlanApplyUseCase.cs`
- `src/UTStudio.Application/PreparedGatePlanAuthority.cs`
- `src/UTStudio.Processing.Core/Properties/AssemblyInfo.cs`
- `tests/UTStudio.Core.Tests/Application/ApplicationCommitCoordinatorTests.cs`
- `tests/UTStudio.Core.Tests/Application/GatePlanApplyUseCaseTests.cs`
- `tests/UTStudio.Core.Tests/Application/InMemoryGatePlanAuthorityPreparedCommitTests.cs`
- `tests/UTStudio.Core.Tests/Processing/GateSignatureAndArchitectureTests.cs`

## Pruebas y validaciones

Las pruebas deterministas cubren reconciliación RF–Apply–RF, saturación interna sin ordinal, cancelación de la
espera, Stop antes del PNR, instalación y retirada de run mediante `ApplicationSession`, identidad del plan entre
los tres owners, capacidad de epochs, fallo de invariantes tras PNR, publicación ordenada y reentrada con
capacidad uno. Los resultados completos de build, tests y formato se registran en la entrega de B2.

## Riesgos y límites

La atomicidad de B2 es semántica bajo el escritor FIFO único: los owners se confirman sin callbacks y ninguna
otra ruta Application los modifica en paralelo; la autoridad publicada es el marcador final. No se afirma
atomicidad transaccional frente a CAS o scheduler, que siguen excluidos y deberán integrarse en bloques
posteriores antes de habilitar sus escritores.

## Pendientes

- Integrar `StoreOnly`, reprocesado histórico, CAS y lifecycle global de candidatos en sus bloques previstos.
- Componer la superficie de Presentation/WPF únicamente en el paso de composición aceptado.
