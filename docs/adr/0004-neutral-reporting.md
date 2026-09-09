# ADR 0004 — Informe neutral y decisiones tecnológicas diferidas

Fecha: 2026-09-08. Estado: Aceptado: separación requerida.
Trazabilidad: R12 en [requisitos](../requirements/baseline.md).

## Contexto
PDF debe existir en v1, pero faltan requisitos de contenido y conformidad.

## Decisión
Separar modelo Reporting y renderizador Reporting.Pdf. Posponer biblioteca/plantilla hasta conocer PDF/A, firmas, imágenes y formato corporativo.

## Alternativas
Generar desde controles WPF acopla informe y UI; elegir biblioteca ahora puede impedir requisitos futuros.

## Consecuencias y pendientes
Permite pruebas del modelo sin UI. Implementación PDF pendiente de Q08; no se excluye PDF de v1.
