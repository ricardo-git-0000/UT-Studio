# Fase 6 B6 — anclas exactas y geometría neutral

Fecha: 2026-10-22.

## Objetivo

Obtener geometría de puertas committed o Preview desde una traza RF exacta, sin usar puntos A-Scan reducidos,
sin recompilar el plan y sin filtrar leases, muestras, stores o evaluators a Presentation/visualización.

## Resultado

- `GateGeometrySourceIdentity` discrimina committed de Preview. Committed conserva revisión, generación y
  secuencia autoritativas; Preview conserva la revisión base y una identidad/generación propia, y nunca aparenta
  una nueva revisión committed.
- `ExactGateGeometryEvaluator` recibe transitoriamente el `CompiledGatePlan` ya producido por autoridad o
  Preview. Comprueba plan, firma y revisión; no compila, registra ni persiste resultados.
- La admisión de lecturas geométricas es inmediata y acotada. El límite predeterminado es dos evaluaciones;
  one-over devuelve `CapacityExceeded` sin leer RF. El evaluator usa además un `GateScratchPool` dedicado y
  acotado.
- Application copia la traza a un buffer temporal, libera el lease antes de evaluar y devuelve el buffer en
  toda salida. Cancelación pertenece únicamente a la consulta geométrica y no alcanza procesamiento productivo.
- El evaluator existente produce en un único batch los eventos exactos y la causalidad A→B→C. El snapshot copia
  solo identidades, estados, tiempos, límites y umbrales necesarios.
- La fuente se valida antes de leer y de nuevo antes de entregar. El snapshot conserva todas sus identidades para
  que un consumidor posterior descarte resultados que queden obsoletos después de esa comprobación.
- `GateGeometryProjector` usa `ScanTimeViewport` y `LinearAxisTransform`, no recorta límites fuera del viewport y
  conserva ausencias como `null`.
- La escala de umbral se comparte con `GateEvaluator`. En RF: `RawSigned` es firmado, `Positive` positivo,
  `NegativeMagnitude` negativo y `Absolute` produce dos niveles simétricos. El contexto rectificado admite
  dominios de magnitud en 0..100 y rechaza `RawSigned` como incompatible. Esto no introduce almacenamiento de
  trazas rectificadas; el store actual continúa siendo RF bipolar.

## Decisiones y límites

- `GatePlanEditorSession`, `AScanViewModel`, WPF y composición productiva de Apply no cambian en B6.
- `GatePlanPreviewCompiler` ya devuelve el plan compilado, pero `GatePlanEditorSession` no lo conserva. B6 deja
  un puerto neutral que acepta ese artefacto y una identidad Preview; la transferencia desde Presentation se
  resolverá en su bloque sin caché paralela, segunda autoridad ni recompilación.
- El guard neutral de fuente permite revalidar committed o Preview sin que Application dependa de Presentation.
- No se crean epochs, ledger, jobs históricos, resultados derivados ni lifecycle adicional.

## Archivos modificados

- `src/UTStudio.Contracts/Processing/GateGeometryContracts.cs`
- `src/UTStudio.Application/ExactGateGeometryEvaluator.cs`
- `src/UTStudio.Processing.Core/GateEvaluator.cs`
- `src/UTStudio.Visualization.Core/GateGeometryProjector.cs`
- `tests/UTStudio.Core.Tests/Application/ExactGateGeometryEvaluatorTests.cs`
- `tests/UTStudio.Core.Tests/Visualization/GateGeometryProjectorTests.cs`
- `tests/UTStudio.Core.Tests/Architecture/GateGeometryArchitectureTests.cs`
- este informe.

## Pruebas y validaciones

- Focalizadas B6: 11/11 correctas.
- Dos ejecuciones previas de Core terminaron con un timeout de cleanup en
  `BScanRangeProjectionTests.NewRevisionImmediatelyBeforeCandidateValidationRejectsStaleRaster`, prueba ajena
  al diff. No se atribuye causa ni se declara resuelto.
- Repetición Core completa posterior: 1043/1043 correctas.
- WPF completa: 134/134 correctas.
- Build Release: correcto, 0 advertencias y 0 errores.
- `dotnet format UTStudio.sln --no-restore --verify-no-changes`: correcto.
- `git diff --check`: correcto.

## Riesgos y pendientes

- Paso 7 debe integrar estos snapshots en un estado visual indivisible con nonce/generación, selección, captura,
  accesibilidad e invalidación por identidad/viewport.
- Paso 8 debe componer lifetimes, el guard de fuente y la transferencia del `CompiledGatePlan` de Preview, además
  de conectar Apply productivo. No debe guardar planes Preview indefinidamente ni introducir otra autoridad.
