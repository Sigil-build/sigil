---
rfc: 002
title: Installer UI framework
status: Accepted
created: 2026-04-30
related: [decisions/D-011, architecture/adr-006-installer-ui-surface]
author: Sigil team
---

# RFC-002: Installer UI framework

## Decision

**Avalonia 12.0.2 with Native AOT.**

## Alternatives compared

| Option | AOT-ready | XAML | Cross-platform | Size (AOT, win-x64) | Verdict |
|---|---|---|---|---|---|
| **Avalonia 12** | First-class (compiled bindings on by default, generated `InitializeComponent`, trim-safe Fluent theme) | Yes | Yes | ~20 MB | **Chosen** |
| Avalonia 11.2 | Functional but requires `EnableAvaloniaXamlCompilation`, manual `AvaloniaXamlLoader.Load`, occasional IL2026 from Fluent theme | Yes | Yes | ~28 MB | Rejected (extra friction; ~30% larger binary) |
| WinUI 3 | Partial | Yes | No (Windows-only) | ~25 MB packaged | Rejected (locks us in) |
| Direct2D + hand-rolled | Yes | No | No | ~3 MB | Rejected (years of work to match Avalonia controls) |
| WPF | No (no AOT) | Yes | No | n/a | Rejected (AOT blocker) |

## Why Avalonia 12 specifically

Avalonia 12 is the first release where the **vanilla template publishes Native AOT with zero IL2026 / IL3050 warnings under `TreatWarningsAsErrors=true`**. That matters for us because Sprint 1 RFC-001 makes those warnings build-breaking errors. With 11.x we would have to ship a `Roots.xml` trimmer descriptor and a list of dynamic-dependency attributes — net ~150 LoC of plumbing per release. Avalonia 12 deletes all of that.

It also flips compiled bindings on by default. Every `{Binding Foo}` against an `x:DataType` resolves at build time and emits real IL — which means renaming a property in a ViewModel breaks the build instead of silently breaking at runtime.

## Spike results

- **Spike binary size (win-x64 AOT):** 18.0 MB (`avalonia-aot-spike.exe`); full publish folder ~36.7 MB (includes `libSkiaSharp.dll` ~11 MB, `av_libglesv2.dll` ~5 MB, `libHarfBuzzSharp.dll` ~2 MB as separate native DLLs). Main AOT binary is within the expected 18–25 MB envelope.
- **IL2026/IL3050 warnings:** Zero. Full `dotnet publish -c Release -r win-x64 -p:PublishAot=true` produced no IL trim warnings.
- **Trim warnings:** None. The Fluent theme in Avalonia 12.0.2 is fully trim-safe — no Fluent-specific IL2026 suppressions required.
- **Avalonia 12.0.x release date:** 12.0.2 stable confirmed on NuGet as of 2026-05-06.
- **Version pinned in Directory.Packages.props:** 12.0.2
- **Installer host binary (win-x64 AOT, full build):** 16.48 MB (`publish/installer-x64/installer.exe`); full publish folder includes `libSkiaSharp.dll` ~11 MB, `av_libglesv2.dll` ~5 MB, `libHarfBuzzSharp.dll` ~2 MB as separate native DLLs. Well within the 40 MB CI budget.
- **Installer host binary (win-arm64 AOT, full build):** AOT publish requires MSVC ARM64 build tools (`C++ ARM64 build tools` workload) — not installed locally. See Step 9.3 CI job for the intended result; CI (`windows-latest`) has the full MSVC toolchain.
- **IL2026/IL3050 warnings from installer host build:** Zero.

## Open issues

- Avalonia 12 pinned to 12.0.2 in `Directory.Packages.props`. Bump deliberately, never via floating versions.
- `AvaloniaUI.DiagnosticsSupport` (the Avalonia 12 equivalent of `Avalonia.Diagnostics`) is gated to `Debug` configuration only to avoid bloating the AOT binary.

## See also

- [Installer UI implementation plan](../superpowers/plans/2026-04-30-installer-ui_4.md)
