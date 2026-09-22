# Navegación temporal compartida del A-Scan

## Objetivo

Completar el primer incremento de zoom y navegación temporal del A-Scan manteniendo el control WPF
reutilizable y preparando sincronización bidireccional con futuros B-Scan.

## Resultado

Se añadió viewport temporal compartido por grupos, zoom anclado, pan, reset por doble clic y comandos,
transformación neutral reversible, asociación run/versión con el snapshot mostrado y cierre explícito.
El A-Scan conserva RF fija en ±100 % y no modifica adquisición ni entrega latest-only.

## Decisiones confirmadas

Las decisiones duraderas, límites y aplazamientos están en
[ADR 0012](../adr/0012-shared-scan-time-navigation.md).

## Archivos modificados

- Core de visualización, Presentation, control WPF y adaptación de MainWindow.
- Pruebas Core y WPF del viewport, transformaciones, sincronización e identidad.
- ADR, índice, arquitectura y plan del primer incremento.

## Pruebas y validaciones

La validación incluye build Release, suite Core/WPF, repeticiones de suites y pruebas dirigidas,
`git diff --check`, enlaces Markdown y comprobaciones de arquitectura. Los resultados finales se
registran en la entrega de la tarea y no se duplican aquí.

## Riesgos

El B-Scan todavía no existe; su integración deberá registrar dominio y orientar el mismo tiempo físico
sin incorporar píxeles al contrato. El zoom amplía únicamente los puntos visuales ya reducidos.

## Cuestiones pendientes

Implementación del B-Scan, zoom de amplitud, tacto, selección de área, profundidad, velocidad de
material, gates y validación visual prolongada en monitores DPI adicionales.
