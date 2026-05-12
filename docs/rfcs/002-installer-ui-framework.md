# RFC-002: Installer UI Framework Choice

**Status:** Accepted (2026-05-01). Backfilled retrospectively 2026-05-12.

## Context

Sigil's Windows installer wizard — the branded UI that end-users see when installing a customer's product — is a distinct runtime artifact shipped inside the MSIX package. This surface is separate from the Sigil-CLI, which remains CLI-only (ADR-006, D-011).

The installer host must satisfy these constraints simultaneously:

- **AOT-publishable** to a self-contained Windows executable at ≤ 40 MB (the MSIX payload limit for the sprint).
- **Supports brand theming** via design tokens (`BRAND_SLOT`, `HERO_SLOT`, brand color palette) loaded from a `brand-tokens.json` file supplied at `sigil pack` time.
- **Renders the six-screen installer flow** (Welcome, License, Install options, Installing, Finish, Custom template) in an 800×500 fixed window meeting WCAG-AA contrast requirements.
- **Runs on both win-x64 and win-arm64** — both architectures are first-class targets per D-004.

The design specification is finalized in `product-architect-package/design/installer-ui.md` and the linked Figma file.

## Options Considered

### WPF (Windows Presentation Foundation)

WPF provides a mature, well-documented Windows UI framework with full XAML support. It was rejected for this use case because WPF does not support Native AOT publication. Shipping a JIT-compiled host would bloat the MSIX payload with the .NET runtime and violate the binary-size targets, in addition to contradicting the AOT mandate established in ADR-001 and ADR-002.

### WinUI 3

WinUI 3 is Microsoft's current-generation Windows UI framework. Its AOT story remains incomplete: it relies on COM activation and runtime XAML compiler paths that are not fully AOT-compatible in .NET 10. Additionally, WinUI 3 requires the application to be packaged as MSIX itself; embedding a WinUI 3 host inside a separate MSIX package creates a packaging dependency loop. Rejected.

### Tauri / web-based renderer

Tauri embeds a WebView2 (Chromium Edge) instance for rendering. This adds the Edge WebView2 runtime as a deployment prerequisite — a hard blocker for air-gapped and restricted enterprise environments that Sigil specifically targets. Rejected.

### Avalonia 11 (chosen)

Avalonia is a cross-platform XAML UI framework for .NET. Version 11 (and the subsequent 12.x series) supports Native AOT publication for Windows via compiled XAML bindings (`x:CompileBindings="True"`) and an AOT-compatible rendering pipeline. Key fit:

- Avalonia's AOT mode produces a self-contained `.exe` well within the 40 MB cap.
- AXAML + MVVM is a natural fit for the six-screen navigation flow.
- The rendering pipeline (Skia-based on Windows) is consistent across win-x64 and win-arm64 without conditional code.
- Brand theming via `ResourceDictionary` and `DynamicResource` is idiomatic Avalonia.

## Decision

**Avalonia 11** with compiled bindings, AXAML, and CommunityToolkit.Mvvm for the MVVM layer.

Brand tokens are loaded at runtime from `brand-tokens.json` via a `System.Text.Json` source-gen context (`BrandTokensJsonContext`). This deviates from the Plan _4 specification, which called for a Roslyn source generator (`SigilBuild.Installer.BrandGenerator`) emitting `BrandTokens.g.cs` at compile time. That deviation is documented separately in [ADR-009](../sigil-docs/architecture/adr-009-brand-token-runtime-json-vs-source-gen.md).

WCAG-AA contrast validation is enforced at `sigil pack` time by `BrandTokenEmitter`, which runs the contrast check before the MSIX is assembled. If contrast fails, the build is blocked. This replaces the compile-time validation that the source-generator approach would have provided.

## Consequences

- AOT publication works for both win-x64 and win-arm64 without reflection suppression suppressions.
- Brand owners can update `brand-tokens.json` without rebuilding the installer host — only `sigil pack` needs to be re-run.
- No compile-time token validation in the installer host binary itself; the validation gate lives in the Sigil-CLI pipeline instead.
- The empty `SigilBuild.Installer.BrandGenerator` project is preserved as a placeholder for future compile-time validation if a requirement arises.

## Acceptance Gates

| Gate | Target |
|---|---|
| Cold-start (AOT, 800×500 window visible) | ≤ 800 ms |
| AOT publish size (win-x64) | ≤ 40 MB |
| win-arm64 AOT publish | Must succeed without warnings |
| WCAG-AA contrast check on default brand tokens | Must pass at `sigil pack` time |

## References

- [ADR-006 (product-architect-package): Installer UI Surface — Two-Surface Model](../../product-architect-package/architecture/adr-006-installer-ui-surface.md)
- [ADR-009: Brand Token Runtime JSON vs Source Generator](../sigil-docs/architecture/adr-009-brand-token-runtime-json-vs-source-gen.md)
- D-011: Installer UI in MVP scope
