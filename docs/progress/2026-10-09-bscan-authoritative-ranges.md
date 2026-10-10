# Migración B-Scan a rangos autoritativos

## Objetivo

Migrar exclusivamente B-Scan a `IInspectionRangeProvider` e `IInspectionStreamStateProvider`, sin
C-Scan ni persistencia.

## Resultado

B-Scan recibe notificaciones sample-free, reconcilia por revisión y lee páginas de 256 posiciones.
No existe copia RF autoritativa visual: cada lease se reduce y libera dentro de su página; sólo los
extremos derivados permanecen durante el candidato. Las ausencias interiores son NoData provisional y
los gaps de `Sequence` no crean columnas.

## Decisiones confirmadas

Se aplicó íntegramente el refinamiento aceptado de Fase 4 del ADR 0015. `ResidentRange` conserva los
watermarks autoritativos y `RepresentedRange` describe la publicación. La selección usa `TraceIdentity`.

## Archivos modificados

ApplicationSession y composición WPF; proyector, delivery y modelos B-Scan; selección Presentation;
fixtures, benchmarks, load tests, arquitectura y plan de fases.

## Pruebas y validaciones

La corrección consolidada añade regresiones de ticket stale por revisión/cierre, callback durante
drain, reconciliación final que continúa ya en fase `Draining`, coalescencia de 10.000 notificaciones,
all-NoData confirmado, mapa de selección por píxel, `ulong.MaxValue`, máximo de 256 leases y orden
sellado→revisión final→drain→freeze. Los resultados validados son: build Release completo sin warnings,
Core completa 532/532, WPF completa 95/95 en tres pasadas consecutivas, cierre WPF 100/100 y smoke de
carga real sin errores ni buffers pendientes. Los benchmarks Dry y su
entorno están en [2026-10-10-bscan-authoritative-projection.md](../performance/2026-10-10-bscan-authoritative-projection.md).

## Riesgos

La proyección reserva primero el candidato y procesa cada página directamente hacia el raster o hacia
scratch neutral acotado por píxeles. El coste no crece con `SampleCount`; debe seguirse con benchmarks y
load tests porque los modos interpolados conservan scratch de la rejilla fuente dentro del mismo ledger.

## Cuestiones pendientes

Persistencia y C-Scan continúan fuera de alcance y conservan sus fases separadas.

## Trazabilidad de cobertura retirada

La matriz nominal completa está en
[`bscan-authoritative-migration-traceability.md`](../testing/bscan-authoritative-migration-traceability.md).
El resumen por responsabilidad es:

| Archivo anterior | Destino |
| --- | --- |
| `ConventionalFrameSinkFanOutTests` | Sustituido por `ApplicationSessionInspectionStoreTests.AScanNotifyFailureDoesNotPreventBScanNotification` y `BScanNotifyFailureDoesNotPreventAScanNotification`; el fan-out RF es obsoleto por contrato. |
| `SessionVisualDeliveryTests` y `BScanSessionLifecycleTests` | Portado a las pruebas de orden final de `ApplicationSessionInspectionStoreTests` y a las barreras de `BScanRangeProjectionTests`. |
| `BScanIngressTests`, `BScanTraceStoreTests` y `BScanTestPools` | El ring RF es obsoleto; invariantes vigentes portadas a `BScanRangeProjectionTests` sobre providers autoritativos. |
| `BScanVisualDeliveryTests`, `BScanVisualProtocolTests` y `BScanVisualPublicationTests` | Sustituidos por candidatos stale deterministas, revisión entre captura/commit, cierre, coalescencia y raster all-NoData en `BScanRangeProjectionTests`, más integración productiva WPF. |
| `BScanVisualMemoryTests` | Portado a máximo de 256 leases, balances exactos y cuotas/owners de las suites Core/WPF vigentes. |
| `BScanLanczosResponsivenessTests` | Cobertura vigente conservada en las pruebas Scan2D y productivas WPF; los fixtures dependientes del ring son obsoletos. |
| `BScanPresentationTests` | Cobertura Live/Selected, selección, `ReturnToLive`, pan/zoom y recovery conservada en Presentation Core y `BScanProductiveIntegrationTests`. |
| `BScanAdapterIntegrationTests` | Portado a `BScanProductiveIntegrationTests`, que atraviesa delivery→VM→adapter→control y desuscripción. |

Los casos que sólo afirmaban slots fijados, `Accept` con muestras, copias RF o tombstones del ring son
obsoletos por ADR 0015 y no se restauran bajo nombres nuevos.
