# ADR 0003 — Persistencia incremental recuperable

Fecha: 2026-09-08. Estado: Aceptado: propiedades; formato pendiente.
Trazabilidad: R09–R11 en [requisitos](../requirements/baseline.md).

## Contexto
Inspecciones de varios GB y adquisición continua impiden cargar todo en memoria.

## Decisión
Escritura incremental, acceso por bloques/regiones, índices, versión y previews multirresolución. Diseñar integridad, confirmaciones y recuperación observable.

## Alternativas
Guardar/cargar una inspección monolítica en RAM incumple escala y recuperación.

## Consecuencias y pendientes
Añade complejidad de índices y cierre; contenedor, compresión, bloques y durabilidad quedan abiertos. No seleccionar formatos externos sin prioridad del usuario.
