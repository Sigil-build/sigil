# Upgrades & downgrades

When you ship a new `exe` package for an app that is already installed, Sigil
compares the version being installed against the version already recorded in
Add/Remove Programs and picks one of four paths automatically — the equivalent
of WiX's `MajorUpgrade` plus a downgrade block, or the Inno/NSIS
"detect the old version and uninstall it first" idiom. There is nothing to
configure: the version comes from `app.version`, and the app identity from
`app.id`.

## The four paths

At startup the installer reads the scope-correct ARP entry for `app.id`
(`DisplayVersion`, `InstallLocation`, `UninstallString`) and classifies the run:

| Installed vs. this build | What happens |
| --- | --- |
| **Nothing installed** | Fresh install. |
| **Same version** | Repair / reinstall — the recorded install is replayed in reverse, then re-applied (idempotent: no duplicate PATH entries, shortcuts, or ARP rows). |
| **Older installed** | **Upgrade.** The previous version's `uninstall.exe /S /Uninstall` is run first (and must exit `0`), then the new version installs into the **previous install directory** so user data outside the install journal is preserved. |
| **Newer installed** | **Downgrade — blocked.** The wizard shows a notice screen; a silent install exits with code **3**. Pass `/force-downgrade` to override. |

An upgrade over a **running** app is the other common non-zero outcome: files held
open by a running process abort the run with exit **4** unless `/closeapps` was
passed. See [the setup.exe reference](../setup-exe-reference.md).

The upgrade removes the old version by running **its own** `uninstall.exe`
(not the new build's uninstall logic), because that binary owns the previous
version's rollback journal and knows exactly how to reverse it.

Two things about that spawn are worth knowing before you debug one:

- **The prior uninstaller is admitted through the single-instance lock explicitly.**
  It derives the *same* app+scope mutex name as the run that spawned it, so it would
  otherwise refuse to start as "already running" and the whole upgrade would exit **5**.
  It is let through by a `SIGIL_SETUP_LOCK_HANDOFF` environment token, validated against
  the real parent process id and that process's creation time — so only a genuine child
  of the running setup is admitted, not anything else that happens to know the token.
- **An unverifiable prior uninstaller is refused.** When privilege is at stake — a
  machine-scope install, or a process that is already elevated — the previous version's
  `uninstall.exe` is only launched if it is Authenticode-valid or sits in a directory
  only administrators can write. Otherwise the run aborts before anything is installed
  with `cannot upgrade: '…' is not verified`. This is reachable in practice for an
  **unsigned** machine-scope install on a secondary volume such as `D:\Apps\MyApp`,
  whose default permissions grant `Users` write. See
  [Uninstaller](uninstaller.md#cannot-upgrade--is-not-verified).

### Scope

If the app was previously installed per-user, the upgrade stays per-user; if it
was per-machine, it stays per-machine. The existing install's scope wins over an
auto-resolved scope, so an upgrade always re-targets exactly what was installed.
An explicit `/allusers` / `/currentuser`, or a manifest that fixes `scope:`, is
still authoritative. If you force a *different* scope than the existing install
(e.g. `/allusers` over a per-user install), Sigil removes the old version in its
own scope and installs fresh into the new scope's default directory — the prior
directory is **not** carried across a scope change.

### Install directory is preserved

An upgrade installs into the prior location even when the new build's default
destination differs (a changed `installer.install_dir`, a renamed app, a
different scope root). Precedence for the destination is:

1. the wizard-collected path, then
2. `/D=<path>`, then
3. **the prior install directory (upgrade)**, then
4. `installer.install_dir`, then
5. the default `<scope root>\<App.Name>`.

## `/force-downgrade`

```text
Setup.exe /S /force-downgrade
```

Installs an older version over an installed newer one. Without it, a silent
downgrade refuses to run and returns exit code **3** (distinct from `1` step
failure, `2` cancelled, and `64` usage error) so automation can detect the
block. With it, the newer version is removed first (exactly like an upgrade)
and the older version is installed.

## Version comparison & pre-release tags

Version ordering uses .NET's numeric dotted-version comparison — the same
semantics as the `version_gte(a, b)` expression function. Only numeric dotted
forms with at least two components parse (`1.2`, `1.2.3`, `1.2.3.4`).

**SemVer pre-release / build tags are not understood.** A value like
`1.2.0-rc1` or `1.2.0+build.7` is *not* parsed as "before 1.2.0"; it falls back
to a plain lexicographic comparison. Sigil does not implement SemVer
pre-release precedence.

For the upgrade decision this is deliberately conservative: whenever a numeric
ordering can't be proven, the run is treated as an **upgrade**, never a block.
An installed version that does not parse as a numeric dotted version is treated
as older than the incoming build (an upgrade, with a warning); an incoming
`app.version` that is a SemVer tag (or absent) likewise never blocks. Keep
`app.version` a plain numeric dotted version to get precise upgrade detection.

## Interaction with `pre_install` hooks

Lifecycle `pre_install` hooks run as part of the **new** install, which happens
*after* the previous version has already been removed. If you need logic that
observes the old files (data migration, config capture), it cannot run in
`pre_install` — by then the old version is gone. The upgrade path intentionally
performs no migration beyond removing the old version and preserving the install
directory; capture anything you need from the prior install before packaging the
upgrade, or read it at install time with the data-retrieval expression functions
(`registry_read`, `file_version`, …).

## Not covered

- **Side-by-side installs** of multiple versions — Sigil keeps a single ARP row
  per `app.id`.
- **Delta / patch updates** — upgrades install the full new package.
