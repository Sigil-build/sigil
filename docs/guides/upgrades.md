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
passed. See [the setup.exe reference](../setup-exe-reference.md). The
`installer.hooks.pre_install` steps run before that check, so an app that ships its
own `--quit` can be stopped by a hook instead — see below.

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
  [Uninstaller](uninstaller.md#cannot-upgrade-is-not-verified).

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

### Option choices are preserved

Every install records the resolved value of each `installer.options` component —
the built-ins and your `components[]` — with its uninstall state
(`uninstall.json` in `%LOCALAPPDATA%\Sigil\<app id>\`, or `%ProgramData%` for a
per-machine install). An upgrade, a forced downgrade or a same-version reinstall
of the same app reads it back, so a user who ticked **Add to PATH** at 1.0 sees
it ticked again on 1.1's Options screen, and a silent upgrade applies it again.
Precedence for each component is:

1. a `locked` component's manifest `default` — always, then
2. the wizard checkbox (interactive runs), then
3. `/P<name>=` / `/Poption.<name>=` on the command line, then
4. **the value the previous install recorded**, then
5. the component's manifest `default`.

A component the new version no longer declares is ignored; one it adds starts at
its default. A custom component whose `when` made it inapplicable is not recorded,
so a forced-off value nobody chose never overrides the default later. The prior
version's teardown deletes its state, so the new version reads the recorded values
when the run starts — before that teardown — and writes its own once the install
completes. State written by a Sigil build that predates this has no recorded
options; that upgrade uses the manifest defaults, and records its own for next time.
Nothing is carried across a scope change, because each scope keeps its own state.

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

Lifecycle `pre_install` hooks are the **first** thing an install runs, before
anything else touches the machine. The order is:

1. `installer.hooks.pre_install` steps;
2. the files-in-use check (in the wizard, the Close-applications screen);
3. `installer.prerequisites`;
4. removal of the previous version (its own `uninstall.exe /S /Uninstall`);
5. the journalled install body, then `post_install` hooks.

So on an upgrade the hooks run while the **previous version is still installed**,
and `{install_dir}` — which an upgrade resolves to the previous install directory —
still holds its files. That is what makes the common teardown hook work:

```yaml
installer:
  hooks:
    pre_install:
      - id: quit-running
        type: run_program
        program: "{install_dir}\\myapp.exe"
        args: ["--quit"]
        timeout_seconds: 30
        on_failure: continue
```

On a **fresh** install the program does not exist yet and the step fails; with
`on_failure: continue` that is logged and the install goes on. Leave it at the
default `fail` and a fresh install aborts.

A hook can also read the old files (configuration capture, for instance) — but it
runs outside the rollback journal, so nothing it does is undone if the install
fails later. The upgrade path itself performs no migration beyond removing the old
version and preserving the install directory; the data-retrieval expression
functions (`registry_read`, `file_version`, …) remain the way to read prior state
into a `when` or a `var`.

## Not covered

- **Side-by-side installs** of multiple versions — Sigil keeps a single ARP row
  per `app.id`.
- **Delta / patch updates** — upgrades install the full new package.
