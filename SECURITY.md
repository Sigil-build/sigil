# Security Policy

Sigil packages, signs, and installs real software. Installers it produces can
self-elevate to admin, write to `HKLM`, register COM servers, and open firewall
rules. A vulnerability here can mean privilege escalation on an end user's
machine, not just a bug in a build tool — treat reports accordingly.

## Supported versions

Sigil is pre-1.0. Until a stable `1.0` release, only the **most recently
published tag** receives security fixes.

| Version | Supported |
|---|---|
| Latest `0.x` release | ✅ |
| Older `0.x` releases | ❌ |

## Reporting a vulnerability

**Please do not open a public GitHub issue for a security report** —
especially for privilege-escalation, elevation-of-privilege, DLL-hijacking,
signature-verification-bypass, or arbitrary-code-execution findings in the
installer host, the wrapper engine, or the signing/update pipeline. A public
issue discloses the finding to every Sigil user before a fix exists.

Instead, use one of these channels, in order of preference:

1. **GitHub private vulnerability reporting** — open a report via the
   "Report a vulnerability" button under this repository's Security tab. This
   routes directly to maintainers and keeps the discussion private until a fix
   ships.
2. **Email** — send details to <security@sigil.build>.

In scope, explicitly:

- Privilege escalation via any install step (`registry_write`, `com_register`,
  `firewall_rule`, service installation, elevated file/directory operations).
- Rollback-journal tampering or replay that lets an unprivileged actor cause a
  privileged action.
- Signature-verification bypass for downloaded payloads, prerequisites, or
  update manifests.
- Path traversal or containment escapes in install-step targets
  (`install_dir`, `/D=`, registry coordinates, directory creation).
- Insecure handling of code-signing material (local PFX, Azure Trusted
  Signing) in `SigilBuild.Signing`.

Please include:

- A description of the vulnerability and its impact.
- Steps to reproduce, or a minimal `sigil.yaml` / command line that
  demonstrates it.
- The Sigil version (`sigil --version`) and Windows version you tested on.

### What to expect

- **Acknowledgement within 5 business days** of a report.
- **An initial assessment (in scope / out of scope, severity) within 10
  business days.**
- We will keep you informed as a fix is developed and credit you in the
  release notes (or CHANGELOG entry) unless you prefer to remain anonymous.
- We ask for a **90-day disclosure window** from acknowledgement before any
  public write-up, to give a fix time to ship. We will work with you to
  extend or shorten that window if circumstances warrant it.

## Scope notes specific to Sigil's design

- Sigil is **Windows-only** and pre-MVP; see `README.md` and
  `docs/plan/release/00-GAP_REGISTER.md` for known gaps that are already
  tracked and do not need a separate report unless you have found a way to
  exploit one.
- Reports about third-party dependencies (see `THIRD-PARTY-NOTICES.md` and
  `Directory.Packages.props`) are welcome, but please also report them
  upstream — we cannot patch a dependency's own CVE, only update the pin.
