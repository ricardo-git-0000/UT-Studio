# ADR 0016 — Componentes WPF completos y reutilizables de scan

Fecha: 2026-10-13. Estado: Propuesto; pendiente de aprobación. Este ADR no autoriza código,
proyectos, referencias, paquetes ni C-Scan.

Trazabilidad: amplía [ADR 0009](0009-reusable-scan-visualization-controls.md), conserva
[ADR 0010](0010-ascan-manual-cursor-measurements.md), [ADR 0013](0013-bscan-bounded-live-visualization.md),
[ADR 0014](0014-bscan-selectable-resampling.md) y [ADR 0015](0015-authoritative-inspection-data-store.md),
y se ejecutaría mediante el [plan por fases](../plans/2026-10-13-reusable-scan-components.md).

## Contexto

`AScanControl` y `BScanControl` ya son renderizadores WPF reutilizables, pero no son componentes
funcionales completos. Para alojar hoy A-Scan o B-Scan hay que reconstruir ViewModel, adaptadores,
toolbar, bindings, comandos, interacción, coordinación y lifecycle que permanecen repartidos entre
`UTStudio.Presentation` y `UTStudio.App.Wpf`. Un consumidor externo no debe conocer
`ApplicationSession`, hardware ni la implementación `InspectionDataStore`, ni adquirir una segunda
autoridad de RF.

El objetivo confirmado es disponer, después de fases de código aprobadas por separado, de
`AScanView`, `BScanView` y un futuro `CScanView` insertables por composición. No se implementa ni se
diseña funcionalmente C-Scan en esta fase.

## Decisión propuesta

### Unidad de reutilización

La unidad pública de alto nivel será un `UserControl` autocontenido por scan. Cada View compondrá su
renderizador de bajo nivel; no habrá clase base universal A/B/C. La personalización compartida se
resolverá mediante contratos neutrales, servicios pequeños y composición. `AScanControl` y
`BScanControl` seguirán siendo APIs separadas para hosts que solo necesiten raster/interacción básica.

La View recibirá un contexto neutral preparado por el host y será dueña únicamente de su scope de
presentación: ViewModel, suscripciones, comandos y adaptadores. No abrirá hardware, no iniciará una
sesión, no creará ni dispondrá el store y no copiará RF autoritativa. La fuente autoritativa seguirá
siendo externa y se leerá exclusivamente mediante puertos neutrales; la selección exacta conservará
`TraceIdentity`.

### División mínima recomendada

No se aceptan todavía nombres de proyectos. La topología candidata mínima es:

1. un ensamblado neutral común de visualización, inicialmente el actual `Visualization.Core`, con
   snapshots, viewports, identidades visuales, raster 2D y puertos de fuente visual;
2. un ensamblado WPF común, inicialmente el actual `Visualization.Wpf`, con primitivas WPF,
   renderizadores de bajo nivel y recursos realmente compartidos;
3. una unidad distribuible por scan, A y B, que pueda agrupar un ensamblado neutral de presentación
   y otro WPF. ViewModel, estado y comandos permanecen neutrales; View, estilos y adaptación viven
   exclusivamente en WPF. Inicialmente pueden seguir en `Presentation` y solo se extraerán si la
   caracterización justifica la nueva frontera;
4. un futuro ensamblado C-Scan solo cuando existan requisitos, modelo neutral y ADR propios;
5. un metapaquete opcional posterior, sin código ni dependencias obligatorias entre scans.

No se recomienda crear ahora un nuevo `ScanVisualization.Contracts`: los puertos autoritativos ya
viven en `UTStudio.Contracts` y los contratos visuales en `Visualization.Core`. Solo se extraería un
ensamblado neutral adicional si la caracterización demuestra que un consumidor necesita esos
contratos sin el resto de Core y el beneficio supera el coste de otra frontera/versionado. Tampoco se
creará un `Presentation` común para todos los scans: el estado y comandos específicos viajarán con el
paquete del scan; únicamente coordinadores neutrales usados por dos o más scans permanecen comunes.

### Dependencias permitidas

```text
Host WPF ──> AScan.Wpf ──> AScan.Presentation neutral ─┐
Host WPF ──> BScan.Wpf ──> BScan.Presentation neutral ─┼──> Core neutral ──> Contracts ──> Domain
Host WPF ──> CScan.Wpf ──> CScan.Presentation (futuro) ┘
                  └──────> Wpf común ─────────────────────> Core neutral
Presentation por scan ──> coordinación neutral de grupo ─> Core neutral

Inspection.Core ──> Contracts ──> Domain
Application ──────> Contracts ──> Domain
```

Los paquetes por scan no se referencian entre sí. Un paquete puede agrupar varios assemblies, pero
ningún ViewModel reside en uno WPF. Ningún ensamblado neutral referencia WPF. Los
componentes no referencian `Inspection.Core`, `Application`, hardware ni `App.Wpf`; el host adapta y
compone instancias de puertos neutrales. Una dependencia de Core neutral hacia Contracts es admisible;
una dependencia hacia la interfaz agregada `IInspectionDataStore` no lo es: se inyectan solamente los
roles de lectura necesarios.

### API pública objetivo

La forma concreta queda pendiente de una prueba de caracterización, pero el contrato objetivo es:

```csharp
public sealed class AScanView : UserControl, IAsyncDisposable
{
    public AScanView();
    public AScanComponentContext? Context { get; set; } // DP asignable solo antes de activar
    public AScanViewState State { get; }                 // DependencyProperty de solo lectura
    public ScanComponentError? Error { get; }            // código neutral acotado
    public ValueTask CloseAsync(CancellationToken cancellationToken = default);
    public ValueTask DisposeAsync();
}

public sealed record AScanComponentContext(
    IObservable<AScanSnapshot> LiveVisuals,
    IObservable<AScanDeliveryStatus> DeliveryStatus,
    ScanPresentationGroup Group,
    SharedScanTimeViewport TimeViewport,
    AScanComponentOptions Options);

public sealed class BScanView : UserControl, IAsyncDisposable
{
    public BScanView();
    public BScanComponentContext? Context { get; set; } // DP asignable solo antes de activar
    public BScanViewState State { get; }                // DependencyProperty de solo lectura
    public ScanComponentError? Error { get; }
    public ValueTask CloseAsync(CancellationToken cancellationToken = default);
    public ValueTask DisposeAsync();
}

public sealed record BScanComponentContext(
    IBScanVisualSource Visuals,
    ScanPresentationGroup Group,
    SharedScanTimeViewport TimeViewport,
    IBScanResamplingPreferenceWriter? Preferences,
    BScanComponentOptions Options);
```

Los nombres y la granularidad son ilustrativos, no API aceptada. `ScanPresentationGroup` representa
el coordinador neutral live/selected y el selector autoritativo compartidos por un grupo; su ownership
es explícitamente prestado o procede de una factoría de grupo. Dos grupos no comparten estado. El
adaptador al Dispatcher se crea en cada assembly WPF y no se filtra al contrato neutral. No se expondrá
`DataContext` como contrato ni se requerirá que el host construya ViewModel o adaptador. La View nunca
sustituye el `DataContext` heredado; su raíz interna enlaza a una propiedad privada. Se
preferirán DependencyProperties para entradas/salidas enlazables y comandos públicos de solo lectura
para automatización. `Context` admite `null → contexto` antes de `Loaded`; el primer `Loaded` activa el
scope. Después, sustitución o `ClearValue` se rechazan mediante coerción y error tipado. Toda operación
pública tiene afinidad con el Dispatcher de la View. Una factoría/`AttachAsync` one-shot se preferirá
si resulta más clara tras caracterizar WPF.

## Host y componente

| Responsabilidad | Host | Componente |
| --- | --- | --- |
| Hardware, conexión, configuración, run y Start/Stop | Sí | No; no forman parte de su toolbar |
| `ApplicationSession` y único lector del canal | Sí | No |
| Crear/poseer/cerrar store autoritativo | Sí | No |
| Adaptar el store a puertos neutrales mínimos | Sí | Solo consumir |
| Commit RF, gaps, revisión y sellado | Sí | No |
| Elegir stream/inspección y compartir coordinadores | Sí | Consumir contexto |
| Crear/disponer ViewModel, bindings y adaptador del scan | No | Sí |
| Toolbar/comandos/interacción del scan y estados visuales | No | Sí |
| Raster derivado, coalescing y descartes visuales | Servicio neutral inyectado o scope documentado | Presentar y diagnosticar |
| Cierre global y orden entre componentes | Sí | Cooperar y drenar su scope |

El host puede usar servicios visuales ya creados por su composition root. Si en una fase posterior un
paquete ofrece factorías neutrales, su ownership debe declararse en el resultado de creación; nunca se
inferirá del contenedor DI.

## Lifecycle, ownership, errores y diagnósticos

Estado del componente: `Unbound → Prepared → Active → Closing → Closed`; un fallo parcial transita a
`Faulted → Closing`, con adquisición transaccional y rollback. Asignar contexto prepara el scope;
`Loaded` lo activa. `Unloaded` pausa recursos WPF reversibles pero
no dispone, porque puede deberse a reparenting o virtualización. El host debe esperar `CloseAsync`
antes de destruir la Window o apagar el Dispatcher; `DisposeAsync` es fallback idempotente. Ambos
cierran admisión UI, cancelan operaciones propias, desuscriben callbacks, esperan recursos propios y
liberan recursos WPF. Se probarán cierre antes de `Loaded`, remove/re-add, concurrencia, doble cierre y
Dispatcher en shutdown. Cancelar el token de `CloseAsync` cancela solo la espera del llamador: cleanup
continúa monotónico, no reabre admisión y `Closed` solo se publica al cruzar la barrera real; otra espera
puede observar su final. No detiene adquisición ni dispone objetos prestados.

En salida global, el host bloquea interacción/admisión UI, completa Stop, drenado y commits de sesión,
sella y entrega `FinalRevision`, solicita la reconciliación B y espera freeze/barrera; después cierra y
dispone Views, scopes visuales según ownership, store y Host. Cerrar solo una ventana B cierra su
consumer/scope de presentación sin detener sesión ni la fuente visual compartida.

Hay dos modalidades excluyentes: fuente visual prestada (la View posee subscription, consumer ID,
operaciones y regiones adquiridas) o scope creado por factoría (el resultado enumera servicios,
presupuesto y barrera que la View posee). Nunca se infiere ownership desde DI. Todo lease RF se
adquiere a través del puerto neutral y se libera antes de publicar resultados
derivados. Los snapshots/raster conservan los presupuestos actuales; el componente no mantiene otra
copia RF. Las callbacks tardías se invalidan por generación. Una invalidación explícita cancela o
sustituye raster obsoleto y causa una sola resincronización full; el live ordinario se coalesce. Stop,
cierre y comandos UI tienen prioridad observable sobre render.

Los errores de entrada/configuración se exponen con códigos acotados y estado; excepciones inesperadas
se registran por el mecanismo inyectado y no se convierten en texto contractual. Un fallo A no detiene
B y viceversa. Los diagnósticos distinguen componente/consumer ID, fuente visual agregada y store.
Mínimos: estado lifecycle, identidad/revisión presentada, trabajo pendiente,
actualizaciones coalescidas/sustituidas, full refresh por causa, leases activos propios, duración de
cierre y último código de fallo. No se duplican contadores autoritativos del store.

## Infraestructura 2D para B y futuro C

`Scan2D` es hoy candidato demostrado solo para remuestreo de grids regulares B: geometría uniforme sin
unidades, intensidad/máscara/procedencia/segmento byte y firma aún cercana a B. No se presume que sirva
para C. No se comparte un ViewModel ni un control universal. B conserva como
semántica columnas `TraceIdentity`, tiempo de muestra vertical, `ResidentRange` autoritativo y
`RepresentedRange` parcial. Un futuro C aportará su modelo de celda, ejes físicos, cobertura,
identidad y proveedor regional tras ADR propio. Solo reutilizará el subconjunto que sobreviva a validar
unidades, extensión de celda, grids no uniformes, cobertura/identidad, rango dinámico, más de 255
segmentos y una firma no ligada al wrap B.

Antes de extraer 2D se caracterizarán dependencias B y se separarán nombres/semántica verdaderamente
genéricos de los específicos. Dirty rectangle solo limita raster; hit testing, selección e identidad
se resuelven contra el rango residente/snapshot mostrado. No se anticipan tiles, GPU, PA, gates,
persistencia ni modelo C.

## Alternativas consideradas

| Alternativa | Ventajas | Costes/riesgos | Dictamen |
| --- | --- | --- | --- |
| Una librería WPF con A/B/C | Instalación simple, pocos proyectos | Acopla versiones y dependencias; arrastra scans no usados; favorece abstracción universal | Rechazada como unidad principal; solo metapaquete posterior |
| Tres librerías totalmente independientes | Aislamiento y despliegue selectivo | Duplica ejes, lifecycle, adaptadores, recursos y 2D; divergencia | Rechazada |
| Paquete por scan + comunes mínimos | Instalación selectiva, evolución independiente, reutilización comprobada | Más fronteras y disciplina de versionado | Recomendada |

## Compatibilidad y versionado

Hasta publicar paquetes, las APIs son internas al repositorio y los cambios siguen versionado de la
solución. En NuGet posterior: SemVer por paquete; tipos públicos, XAML resource keys, DependencyProperties,
comandos, códigos de error y semántica de ownership forman contrato. Los paquetes por scan versionan
independientemente y declaran rangos compatibles exactos para comunes. El metapaquete no contiene
código. Se mantiene compatibilidad conductual de la aplicación mediante pruebas de caracterización,
incluidos selección exacta, navegación, cierre, accesibilidad, DPI y presupuestos.

## Consecuencias y decisiones pendientes

La recomendación minimiza duplicación sin obligar a instalar B/C al consumidor de A. Exige mover
presentación específica hoy situada en `UTStudio.Presentation` y UI situada en `App.Wpf`, pero ese
movimiento será incremental y con fachadas temporales si son necesarias.

Pendiente de aprobación: nombres definitivos; si se crea Contracts visual separado; forma exacta de
contextos/factorías; ownership de fuente, consumer ID, presupuesto y viewport; superficie atómica del
estado B; theming, accesibilidad, atajos y resource keys públicos; logging/métricas y baseline
reproducible de respuesta; targets y estrategia NuGet. C-Scan requiere requisitos y ADR
independientes.
