---
name: aot-safety
description: Review code for Native AOT / trim safety before committing or in PR review. Use when checking reflection usage, IL2026/IL3050 warnings, JSON serialization, AOT publish failures, or when the post-edit guard hook flags a pattern.
---

# AOT safety review for Sigil

Every shipped binary is Native-AOT-published. Reflection that survives to
publish time fails the build (best case) or crashes an end user's installer
(worst case, if it slips past the analyzer via dynamic patterns).

## Why your local check may be lying

- The trim/AOT analyzer (`EnableTrimAnalyzer`) only runs in **Release**
  (`Directory.Build.props`). Debug builds are always clean. Verify with
  `dotnet build Sigil.slnx -c Release`.
- `IL2026` and `IL3050` are errors via `.editorconfig` — never downgrade them,
  never `#pragma`-suppress without a comment explaining why the call is provably
  safe under AOT.
- The definitive check is the CI `aot-publish` job. Locally (Windows only):
  `dotnet publish src/SigilBuild.Cli -c Release -r win-x64 -p:PublishAot=true`.

## Checklist

1. **No runtime reflection**: `Activator.CreateInstance`, `Type.GetType`,
   `Assembly.Load*`, `DynamicMethod`, `MakeGenericType/Method`, expression
   trees + `.Compile()`.
2. **JSON**: only source-generated contexts. The wrapper blob uses
   `WrapperBlobJsonContext` + hand-rolled discriminators in
   `SerializableInstallStep` / `SerializableRollbackRecord`. New (de)serialized
   types MUST be added as `[JsonSerializable]` on the context — a miss is a
   *runtime* failure in the installer, not a compile error.
3. **No `JsonDerivedType` polymorphism** in the blob types — the hand-rolled
   discriminator is a deliberate ADR-level decision; don't refactor it away.
4. **Codegen**: use source generators (see `SigilBuild.Localization.Generator`).
   Note that generator project is netstandard2.0 and analyzer-only-referenced;
   global `-p:PublishAot=true` leaks are handled by static-graph restore
   (comment in `Directory.Build.props`) — don't "fix" restore settings blindly.
5. **Globalization**: `InvariantGlobalization` is intact — don't introduce
   culture-dependent APIs that pull ICU into the host (that budget headroom is ~3 MB).
6. **Size**: if the publish smoke test reports growth near a gate
   (15 MB CLI / 45 MB host), identify the dependency that grew — don't bump the gate.
7. **Platform guards**: Win32 calls need CA1416-clean guards (`[SupportedOSPlatform]`
   or runtime checks); CA1416 is an error.

## When the post-edit hook flags you

The guard greps for the patterns in item 1. If the usage is provably AOT-safe,
mark the line with `// aot-reviewed: <reason>` and mention it in the PR.
