# ADR 0001 — Núcleo portable y adaptación WPF

Fecha: 2026-09-08. Estado: Aceptado: restricciones del contexto.
Trazabilidad: R01, R02, R08, R13 en [requisitos](../requirements/baseline.md).

## Contexto
WPF inicial debe permitir evolución a Avalonia sin contaminar dominio ni ViewModels.

## Decisión
Mantener núcleo y Presentation sin WPF/Avalonia; usar contratos propios, DI por constructor y App.Wpf como composición. Solo .Wpf referencia WPF. SDK externos aislados.

## Alternativas
Acoplar ViewModels y hardware a WPF simplificaría accesos locales, pero incumple portabilidad y pruebas.

## Consecuencias y pendientes
Más adaptadores y disciplina de dependencias; Linux del hardware sigue sin garantizarse. El mapa físico de proyectos continúa propuesto.
