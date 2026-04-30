# Contributing to Sigil

Thanks for considering a contribution! Sigil is in pre-MVP — the surface area
changes weekly. Before opening a non-trivial PR, please open a discussion or
issue so we can align on direction.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (10.0.100 or newer)
- Git 2.40+
- Optional: `gitleaks` for the local pre-commit hook

## Build & test

```bash
dotnet restore
dotnet build
dotnet test
```

To exercise the Native AOT publish (Windows only in Sprint 1):

```bash
dotnet publish src/SigilBuild.Cli -c Release -r win-x64 -p:PublishAot=true
```

## Coding conventions

- File-scoped namespaces, nullable reference types on, `TreatWarningsAsErrors=true`.
- Tests use xUnit + FluentAssertions. AAA layout (Arrange / Act / Assert).
- Native AOT is mandatory — **no reflection-heavy patterns** (no `Activator.CreateInstance`,
  no untyped `JsonSerializer.Deserialize`, no runtime expression trees).
  Use source generators when you need codegen.
- Commits follow [Conventional Commits](https://www.conventionalcommits.org/).
  Examples: `feat: add zip packager`, `fix: handle empty manifest`, `chore: bump xunit`.

## PR checklist

- [ ] Tests added / updated
- [ ] `dotnet build` succeeds with no warnings
- [ ] `dotnet test` is green
- [ ] No secrets committed (`gitleaks detect` clean)
- [ ] If you touched `architecture/`, you've updated the relevant ADR

## Code of Conduct

This project follows the [Contributor Covenant 2.1](CODE_OF_CONDUCT.md).
