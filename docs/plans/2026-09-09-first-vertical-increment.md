# Plan del primer incremento — Simulador y A-Scan WPF

Fecha: 2026-09-09. Estado: alcance y arquitectura aprobados; secuencia de ejecución propuesta, implementación no iniciada ni autorizada en esta tarea documental.

## Objetivo y alcance confirmado

Completar la sección vertical simulador -> sesión -> A-Scan neutral -> ViewModel -> ventana WPF con los siete proyectos productivos y UTStudio.Core.Tests del [ADR 0005](../adr/0005-initial-solution-structure.md). Aplicar el ciclo de vida del [ADR 0006](../adr/0006-session-window-lifecycle.md) y las políticas del [pipeline](../architecture/data-pipeline.md).

No incluye SignalProcessing, Storage, GigE, PCIe, Reporting, PLC, Robot, Avalonia, 3D, otros scans ni distribución de payloads entre múltiples consumidores. Los requisitos posteriores de producto siguen vigentes.

## Propiedad y coordinación de esta tarea documental

| Propietario | Archivos o responsabilidad |
| --- | --- |
| Agente principal, único escritor | docs/architecture/overview.md; docs/architecture/data-pipeline.md; docs/adr/0005-initial-solution-structure.md; docs/adr/0006-session-window-lifecycle.md; este plan |
| solution_architect, solo lectura | Redacción propuesta y revisión de estructura y contratos compartidos |
| quality_reviewer, solo lectura | Revisión de límites, ciclo de vida, memoria y capacidad de pruebas |

La futura implementación tendrá una asignación explícita de propietarios antes de editar. Este plan no delega escritura de código.

## Secuencia propuesta para una tarea posterior

1. Concretar contratos mínimos por Acquisition, Application, Presentation y Diagnostics, sin clases o servicios futuros. Precisar capacidades del simulador, metadatos/unidades, snapshots y ownership de un consumidor, incluidos consumo, sustitución, cancelación y fallo. Revisión de contratos compartidos por solution_architect.
2. Resolver herramientas de pruebas/CI, representación/renderizado A-Scan y dependencias necesarias; justificar y obtener aprobación antes de añadir NuGet. Definir salida/reapertura tras cerrar la última ventana y política de timeout/aborto.
3. Crear posteriormente los siete proyectos bajo src/ y tests/UTStudio.Core.Tests; validar referencias y objetivos definidos en ADR 0005. No crear proyectos aplazados.
4. Implementar simulador determinista y sesión Application con inicio/parada/fallo, cancelación, canal acotado y propiedad explícita. El único consumidor del canal debe seguir resolviendo datos aunque no haya ventanas; ningún ViewModel compite por leerlo.
5. Incorporar modelo y transformación A-Scan en Visualization.Core, selección latest-only, estado/ViewModel en Presentation y adaptación visual/composición en App.Wpf. Mantener trabajo por frame fuera del Dispatcher y limitar la frecuencia visual. No introducir DSP adicional.
6. Integrar cierre de ventana con liberación de suscripciones y cierre explícito de aplicación con parada esperada. Verificar también funcionamiento sin ventanas y diagnóstico de fallo de cierre.
7. Ejecutar build y pruebas pertinentes; revisar diff, referencias y archivos nuevos. Registrar decisiones adicionales en el documento o ADR correspondiente y mediciones reproducibles cuando existan.

## Validación prevista del incremento

UTStudio.Core.Tests agrupará suites de Domain, Contracts, Application, Acquisition.Simulator, Visualization.Core, Presentation y arquitectura. Solo referenciará los seis proyectos neutrales. No elegir ahora framework ni mocking.

- Grafo acíclico, referencias permitidas y ausencia de WPF/Avalonia incluso transitiva en el núcleo; ningún ViewModel referencia otro.
- Señales sintéticas reproducibles y correspondencia de muestras, unidades y ejes del A-Scan neutral, sin hardware.
- Inicio, parada, fallo y cancelación de sesión con tiempo controlable; evitar verificaciones dependientes solo de sleeps.
- Sustitución latest-only con contabilidad de descartes, memoria acotada y liberación exactamente una vez, sin reutilización prematura.
- Desuscripción de una vista sin detener fuente; ausencia de vistas sin acumulación; solicitud de salida que espera parada y liberación antes de destruir servicios.
- Contadores de integridad exactos aunque su publicación sea muestreada.

La composición, representación A-Scan y cierre real de ventanas WPF se comprobarán por separado en Windows; Core.Tests no cubre esa integración. No se crea otro proyecto de pruebas en este incremento sin ampliar el plan. Las pruebas hardware, persistencia y rendimiento de esas futuras funciones quedan aplazadas.

## Pendientes y riesgos

- Antes de implementar el flujo: ownership de un consumidor, topología de entrega visual, representación y vida de memoria del A-Scan, semántica/hilo/versionado de snapshots, presupuestos de memoria y frecuencia visual.
- Antes de integrar el cierre: salida/reapertura sin ventanas, timeout, aborto y diagnóstico observable.
- Q10: framework de pruebas, mocking y CI; aprobación de paquetes necesarios. Q09: renderizado y presupuestos, sin seleccionar biblioteca todavía.
- Antes de varios consumidores: estrategia exacta de ownership compartido; no seleccionar leases/copias ahora. Q13/Q06 conservan orden procesamiento/persistencia, datos a conservar y relación con durabilidad.
- Para incrementos futuros: protocolos/SDK y capacidades reales (Q01–Q04), geometría (Q05), formatos (Q06–Q07), informes (Q08), Linux/despliegue (Q11) y concurrencia de inspecciones (resto de Q12).

Los riesgos principales son uso de buffers después de liberar, retención por UI lenta, detención accidental por cierre de ventana y confusión entre muestreo de métricas y pérdida de contabilidad. Los casos de validación anteriores deben demostrar su control, sin inventar SLA ni afirmar resultados antes de ejecutar pruebas.

## Validación de esta entrega documental

Comprobar enlaces locales, consistencia entre los dos ADR y arquitectura, diff y alcance exclusivo de cinco archivos Markdown. Compilación y pruebas funcionales no aplican: no se crea solución, proyectos C#, código ni paquetes. No realizar commit.
