# Prerequisites

Many apps need a shared runtime installed first — the Visual C++ redistributable,
the .NET runtime, and so on. Sigil models these as **first-class prerequisite
units** under `installer.prerequisites`, the declarative equivalent of WiX Burn's
`ExePackage` + `DetectCondition` or the InnoDependencyInstaller idiom. There is no
scripting: you declare *how to detect* the dependency and *what to run* when it is
missing, and Sigil does the rest.

## How it works

Each prerequisite runs **before** the transactional install body and the
`pre_install` hooks — and **before the rollback journal opens** — sequentially:

1. **Detect.** The `detect` expression is evaluated. If it is already true, the
   prerequisite is skipped entirely (no download, no run).
2. **Acquire.** Otherwise the `source` is obtained — a bundled `payload://` file, or
   an `https://` download verified against its `sha256`.
3. **Run.** The installer is launched with `args`; its exit code must be in
   `exit_codes_ok` (default `[0]`).
4. **Re-detect.** `detect` is evaluated again. If it is *still* false the run aborts
   with a clear message — the prerequisite installer ran but did not take effect.

Because prerequisites run before the journal opens, **any failure aborts with no
partial install**. An exit code of **3010** (the Windows "success, reboot required"
convention) is accepted and flags the session as reboot-required: the wizard's Done
screen shows a restart notice, and a silent install exits with code **3010**.

> **Prerequisites are never rolled back.** A VC++ redistributable or the .NET runtime
> is a shared, machine-level dependency that other applications rely on — undoing it
> on uninstall would be wrong. Prerequisite side effects are not journaled.

## Fields

| Field | Required | Notes |
| --- | --- | --- |
| `name` | yes | Shown on the wizard progress row and in the log ("Installing &lt;name&gt;…"). |
| `detect` | yes | A `when`-grammar expression, true when the dependency is already present. |
| `source` | yes | `payload://…` (bundled) or `https://…` (downloaded). |
| `sha256` | for `https` | Integrity checksum. A download without one is refused at pack time (SIG0280). |
| `args` | no | Arguments to the installer (typically `/quiet /norestart`). Tokens allowed. |
| `exit_codes_ok` | no | Exit codes treated as success; default `[0]`. `3010` also flags reboot. |
| `scope_required` | no | `allusers` or `currentuser`; a mismatch is a diagnostic at session start. |
| `timeout_seconds` | no | Per-prerequisite run timeout. |

## Recipe: Visual C++ 2015–2022 redistributable (registry detect)

The x64 redistributable records `Installed=1` under its runtime key, so `detect`
is a plain registry check. Ship the redistributable in your payload (or download it
with a `sha256`). `3010` is expected — the redist often asks for a reboot.

```yaml
spec: v1.0

app:
  id: com.example.App
  name: Example App
  version: 1.0.0
  publisher: Example, Inc.

build:
  source: ./payload

package:
  formats: [exe]
  architectures: [x64]

installer:
  prerequisites:
    - name: "Visual C++ 2015-2022 Redistributable (x64)"
      detect: "registry_exists('HKLM', 'SOFTWARE\\Microsoft\\VisualStudio\\14.0\\VC\\Runtimes\\x64', 'Installed')"
      source: "payload://prereq/vc_redist.x64.exe"
      args: ["/install", "/quiet", "/norestart"]
      exit_codes_ok: [0, 3010]
      scope_required: allusers
```

## Recipe: .NET Desktop Runtime (file / registry detect)

Detect the runtime with a file check (or a registry version read), and download the
official installer over HTTPS with its published `sha256`.

```yaml
spec: v1.0

app:
  id: com.example.App
  name: Example App
  version: 1.0.0
  publisher: Example, Inc.

build:
  source: ./payload

package:
  formats: [exe]
  architectures: [x64]

installer:
  prerequisites:
    - name: ".NET Desktop Runtime 8"
      detect: "file_exists('C:\\Program Files\\dotnet\\dotnet.exe') && version_gte(registry_read('HKLM', 'SOFTWARE\\dotnet\\Setup\\InstalledVersions\\x64\\sharedfx\\Microsoft.WindowsDesktop.App', 'Version'), '8.0.0')"
      source: "https://example.com/windowsdesktop-runtime-8.0-win-x64.exe"
      sha256: "0000000000000000000000000000000000000000000000000000000000000000"
      args: ["/install", "/quiet", "/norestart"]
      exit_codes_ok: [0, 3010]
      scope_required: allusers
```

Replace the `sha256` with the real checksum of the installer you pin (Sigil refuses
to pack an `https://` prerequisite without one).

## Notes & limits

- **No redist catalog / feed.** You pin the exact installer (bundled or a checksummed
  URL); Sigil does not resolve dependencies from an online catalog.
- **Sequential.** Prerequisites run one at a time, in declaration order — no parallel
  installs.
- **Detect must be reliable.** The re-detect guard turns a silently-failed dependency
  install into a clear error, so invest in a `detect` that truly reflects "installed".
- **Use live functions in `detect`, not `var.*`.** `detect` is evaluated twice — before
  and after the installer runs — so it must read *live* state each time
  (`registry_read`, `registry_exists`, `file_exists`, `file_version`). An
  `installer.vars` value is computed once at session start and never refreshed, so a
  `detect` written in terms of `var.*` would re-read a stale snapshot and wrongly report
  the freshly-installed dependency as still missing.
- **Reboot-required (3010).** An installer that returns 3010 is accepted as success and
  flags a reboot even if you did not list 3010 in `exit_codes_ok`. Because the component
  only becomes active on the next boot, its `detect` is *not* re-checked in-process.
