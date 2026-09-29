# B-Scan, Fase 5 WPF

## Objetivo

Completar el primer incremento reutilizable B-Scan con control WPF, adaptación productiva y ventana.

## Resultado

Se implementaron control, paleta, raster BGRA32 full/incremental, interacciones, adaptador, ventana
reabrible, fan-out A/B y cierre ordenado. La selección exacta se resuelve por índice.

## Decisiones confirmadas

- Presentation retiene regiones pooled y WPF copia por fila mediante token.
- La visita neutral se convierte fuera del lock a staging BGRA32 pooled. Full usa candidato e incremental
  backup/rollback; un fallo conserva instancia, píxeles e identidad visibles.
- El pico activo WPF máximo a 1.024×1.024 es aproximadamente 12 MiB por control, separado del presupuesto
  neutral de 16 MiB.
- Visualization.Wpf solo depende de Visualization.Core.
- Cerrar B no cierra sesión, ingress, delivery, selección ni viewport compartidos.
- Un consumidor B nuevo recibe bootstrap desde el store activo; el defecto circular metadata→request
  quedó reproducido con Delivery real y cubierto por regresión.

## Archivos modificados

Core/Presentation, Visualization.Wpf, App.Wpf, pruebas WPF y documentación asociada.

## Pruebas y validaciones

Restore y build Release sin advertencias. Tras la Tanda 2, la suite completa alcanza 346 pruebas Core
y 75 WPF; las 24 Core y 37 WPF sensibles al cierre pasaron tres repeticiones consecutivas. Previamente
se ejecutaron tres pasadas
Core/WPF y 50 pasadas dirigidas de diez pruebas B-Scan transaccionales y de adaptador. El Dry diagnóstico BGRA32 observó 13,904 ms
para 1024×1024, 1,627 ms para 1×1024, 2,002 ms para 8×1024 y 1,986 ms para la LUT de 1.048.576
entradas, sin asignaciones administradas registradas. Son una única iteración fría en VM y no una
baseline. La inspección manual interactiva no estuvo disponible y se sustituyó por render off-screen a
96/144 DPI. `git diff --check` y las referencias arquitectónicas resultaron correctos.

Un Dry posterior sobre superficies WPF reales, ejecutado mediante host STA, observó 1,993 ms para full
1024×1024, 2,677 ms para incremental 1×1024, 2,743 ms para 8×1024, 2,683 ms para rollback y 7,091 ms
para bitmap con ejes, selección y aviso de gap. Incluye el cruce al host STA y sigue siendo diagnóstico,
frío y no contractual.

## Riesgos

No se declara todavía una configuración máxima productiva; depende de tamaño, dirty rate y DPI.

## Cuestiones pendientes

Persistencia, GPU, sincronización entre grupos, eje físico y máximos quedan fuera. La localización de
discontinuidades permanece aplazada hasta definir metadata neutral tipada y acotada; el aviso global
continúa siendo el contrato de este incremento.

Tras recuperar la VM se corrigió la captura no atómica de petición/generación en el publicador y se
evitaron capturas de bootstrap redundantes por columna, conservando el rebind automático al cambiar de run.

La Tanda 2 cerró el ciclo de vida contractual: bloqueo UI inicial también desde hilo no UI; cierre B y A
secuenciales antes de sesión; cleanup completo por etapas; Host no dispuesto sin confirmación; ventana
principal visible en diagnóstico; y errores principal/secundarios con ruta global e interna estructurada.

La Tanda 3 está [definida](../plans/2026-09-26-bscan-tanda-3-acceptance.md), pero no ejecutada. Su
propósito es aceptar el incremento existente mediante recorrido manual, estabilidad, accesibilidad,
matriz automatizada y rendimiento diagnóstico. Duración, ciclos, umbral de tendencia de memoria,
hardware y herramientas de medición permanecen como decisiones explícitas del usuario.

Las correcciones funcionales descubiertas posteriormente durante la aceptación manual —ring live,
redimensionado responsivo, rango residente, cursor independiente y run congelado— se consolidan en el
[resumen de correcciones de aceptación](2026-09-29-bscan-manual-acceptance-corrections.md).
