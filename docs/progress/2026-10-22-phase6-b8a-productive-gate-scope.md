# Fase 6 B8A — scope productivo headless de gates

Fecha: 2026-10-22.

## Objetivo

Componer un único grafo por inspección para autoridad editable, procesamiento live, store derivado, Apply,
reprocesado histórico, geometría exacta y budgets, sin activar todavía WPF ni `DesktopRuntime`.

## Resultado

- Preview compila el candidato exacto `N+1`. Su identidad distingue `BaseRevision=N`,
  `CandidateRevision=N+1` y `PreviewIdentity`; la revisión candidata no se presenta como committed.
- La revisión máxima termina como `RevisionExhausted`, sin overflow ni artefacto candidato.
- `GatePlanPreviewCandidate` transporta el mismo `CompiledGatePlan` desde Preview a geometría y Apply. Apply no
  contiene compilador ni retargetea el candidato.
- La procedencia se prueba con un HMAC emitido por la instancia inspection-scoped. El material usa codificación
  binaria canónica e incluye autoridad, revisiones, identidad Preview, firma, schema, algoritmo y el draft
  completo. La firma identifica contenido; no sustituye la revalidación de autoridad.
- Apply valida coherencia/procedencia antes de admitir. Un artefacto discordante devuelve
  `InvalidPreviewArtifact`; un candidato auténtico stale entra en el FIFO y termina `Conflict` con ordinal y
  reconciliación global.
- Los contratos neutrales de admisión Apply separan saturación/cierre/cancelación preadmisión del
  `GatePlanEditResult`. Cancelar el waiter tras `Admitted` no cancela la operación ni su terminal.
- Geometría committed usa la autoridad inspection-scoped. Preview presta por petición un guard propiedad del
  workspace; el evaluator no retiene una vista ni un slot mutable global. Dos vistas conservan guards e
  identidades independientes.
- `ProductiveGateInspectionScope` posee store, live, autoridad, scheduler, coordinator histórico y evaluator.
  La sesión creada por el scope los toma prestados; el scope dispone primero la sesión y después el owner
  histórico, que drena observers y cierra live/store/autoridad una sola vez.
- El rollback de construcción conserva el error primario, continúa cleanup independiente y agrega fallos
  secundarios. Las pruebas observan rollback en etapas anteriores y posteriores a crear el owner histórico.
- El factory productivo anterior permanece sin cambios de comportamiento. B8A no modifica WPF,
  `DesktopRuntime` ni activa bindings/comandos.

## Frontera B8B

B8B debe crear el editor/overlay por vista con los puertos prestados del scope, publicar el snapshot compuesto,
conectar intenciones y sustituir el fallback WPF de forma atómica. También conectará `DesktopRuntime`. No debe
crear otra autoridad, recompilar Preview ni disponer owners inspection-scoped desde la vista.

## Archivos y validaciones

Los cambios afectan Contracts de edición/geometría, Application (Preview, Apply, sesión y nuevo scope),
Presentation portable y pruebas Core/arquitectura.

- Focalizadas B8A/editor/overlay/geometría/arquitectura: 79/79.
- Core completa Release: 1062/1062.
- WPF completa Release: una ejecución terminó 138/139 por un fallo de cleanup B-Scan ajeno al diff, con un
  owner visual todavía activo; la repetición completa posterior pasó 139/139. No se atribuye causa ni se
  considera resuelta la incidencia por esa repetición correcta.
- Build Release: correcto, 0 warnings y 0 errores.
- `dotnet format UTStudio.sln --no-restore --verify-no-changes`: correcto.
- `git diff --check`: correcto.

## Riesgos e incidencias separadas

- Siguen abiertas, sin atribuir causa ni declarar resolución, las incidencias lifecycle paralelas documentadas
  en B7A y el timeout de cleanup B-Scan ajeno a los diffs de gates.
- La prueba STA de DPI de B7B conserva su limitación física: valida la transición WPF y el rectángulo devuelto
  por WPF, no una conversión observada en un monitor físico real.
