# Fase 1 — contratos y compilador de planes de puertas UT

Fecha: 2026-10-16.

## Objetivo

Implementar exclusivamente la primera fase de ADR 0017: identidades, contratos neutrales inmutables y
compilación determinista del plan, sin procesar RF ni integrar store o UI.

## Resultado

- Identidades y revisiones validadas en `UTStudio.Domain/Processing`.
- Contratos públicos neutrales en `UTStudio.Contracts/Processing`.
- Nuevo proyecto net10.0 `UTStudio.Processing.Core`, sin paquetes y con referencias exclusivas a Domain y
  Contracts.
- Compilador stateless y reutilizable concurrentemente, con validación completa/fallida, DAG, ciclos,
  dependencias compiladas a índices y orden topológico con desempate por orden declarado.
- Firma persistible SHA-256 sobre codificación binaria canónica y versionada de todos los campos semánticos.
- Suite dirigida y pruebas arquitectónicas añadidas a `UTStudio.Core.Tests`.

## Decisiones confirmadas

Se conservan las quince decisiones cerradas de Q19. Para conciliar reutilización y capacidad de producto, el
compilador acepta desde una puerta; demuestra planes de al menos tres, usa máximo configurado 8 por defecto y
máximo absoluto 16.

## Archivos modificados

- `src/UTStudio.Domain/Processing/`
- `src/UTStudio.Contracts/Processing/`
- `src/UTStudio.Processing.Core/`
- `tests/UTStudio.Core.Tests/Processing/`
- `tests/UTStudio.Core.Tests/UTStudio.Core.Tests.csproj`
- `UTStudio.sln`
- ADR 0017 y plan de puertas UT.

## Pruebas y validaciones

- Formato limitado por proyecto: correcto. El intento de formato sobre toda la solución no pudo evaluar SDK
  WPF dentro del sandbox; los cuatro proyectos afectados se formatearon individualmente sin errores.
- Build Release completo: correcto, cero warnings y cero errores.
- Pruebas dirigidas Processing: 26/26 correctas.
- Core completa: 586/586 correctas en tres ejecuciones consecutivas.
- WPF completa: 133/133 correctas en la repetición final.
- `git diff --check`, enlaces, referencias, paquetes y artefactos: comprobados al cierre.

Se observaron fallos intermitentes ajenos al diff en pruebas de temporización de visualización; las pruebas
aisladas y las repeticiones completas posteriores pasaron. Se registran como riesgo P3 de flakiness
preexistente, no como regresión.

## Riesgos

- El evaluator, los detectores y la lectura RF siguen deliberadamente fuera de alcance.
- El presupuesto final continúa condicionado por benchmarks posteriores.
- La persistencia del store derivado y los requisitos PA siguen abiertos por separado.
- Dos pruebas históricas mostraron flakiness de temporización, con repeticiones posteriores verdes.

## Cuestiones pendientes

No quedan decisiones de compilación abiertas para Fase 1. Las cuestiones separadas de Q19 no cambian.
