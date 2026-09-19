# Diagnostics reference

Every problem Sigil reports carries a code, and every code names exactly one
failure. When `sigil validate`, `sigil pack` or `sigil sign` prints something
like

```text
error SIG0122: build.source './payload' resolves to 'C:\work\payload', which does not exist. ...
  see: https://docs.sigil.build/diagnostics/#sig0122
```

the `see:` link lands on this page, at that code's entry.

**One code, one meaning.** Codes are never reused for a second failure, even
across the CLI and the installer runtime, because a code's whole job is to
identify one problem well enough to document it in one place. Four numbers
once meant two unrelated things each; that is why the signing band was
renumbered before any release shipped.

**Severity is not encoded in the number.** Most of these are errors, but some
are warnings that let the pack continue with a feature omitted — each entry
below says which.

## The bands

| Band | Subject | Raised by |
|---|---|---|
| `SIG00xx` | Loading and schema validation | `validate`, `pack` |
| `SIG01xx` | Packaging — the host or the toolchain, not the manifest | `pack` |
| `SIG02xx` | `parameters:` and `install_steps:` | `validate`, `pack` |
| `SIG024x`–`SIG031x` | The `installer:` block | `validate`, `pack` |
| `SIG029x` | Localization | `validate`, `pack` |
| `SIG032x` | Updates and network trust | `pack` and update runtime |
| `SIG04xx` | Signing | `sign` |

---

## SIG00xx — loading and schema

### SIG0001 — the manifest is not valid YAML {#sig0001}

The file could not be parsed at all: it is empty, its root is not a mapping,
or the YAML itself is malformed (most often indentation, or a value containing
`:` that needed quoting).

Nothing else can be checked until this passes — the schema, the step catalog
and every other rule below run against a parsed document.

### SIG0002 — manifest file not found {#sig0002}

The path given to the command does not exist. `sigil` defaults to `sigil.yaml`
in the current directory when no path is passed.

### SIG0003 — spec version mismatch {#sig0003}

Reserved for a `spec:` value this build does not support.

**Not currently emitted.** The code is declared and nothing raises it today;
the supported `spec` value is enforced by the schema instead, which reports
[SIG0010](#sig0010).

### SIG0010 — the manifest violates the schema {#sig0010}

The document parsed as YAML but broke a rule in `sigil-schema.json`: an unknown
property, a missing required one, a value of the wrong type, or a string
outside its allowed set.

The message names the offending path. The [manifest reference](manifest-reference.md)
is generated from that same schema, so it is the authority on what a block
accepts.

### SIG0020 — environment variable is not set {#sig0020}

The manifest interpolates `${ENV_VAR}` and the variable is not present in the
environment running the command. Typical in CI, where the variable exists
locally but was never added to the job.

### SIG0050 — missing optional field {#sig0050}

Reserved for informational notices about optional fields left unset.

**Not currently emitted.**

---

## SIG01xx — packaging

Nothing in this band is the manifest's fault. The document is valid; the
machine cannot honour it.

### SIG0100 — MSIX packaging requires Windows {#sig0100}

`formats: [msix]` on a non-Windows pack host. MSIX is produced by Windows SDK
tooling with no cross-platform equivalent.

### SIG0101 — Windows SDK not found {#sig0101}

MSIX packaging needs `MakeAppx.exe` from the Windows 10/11 SDK, and it is not
installed. Install it from <https://aka.ms/winsdk>.

### SIG0110 — MakeAppx failed {#sig0110}

`MakeAppx.exe` exited non-zero. The message carries its exit code and output —
that output, not this page, is what explains the failure.

### SIG0111 — Windows App Certification Kit is not installed {#sig0111}

**Warning, not an error.** The manifest asked for WACK validation
(`runWack: true`) and `appcert.exe` is absent, so validation was skipped. The
package was still produced.

### SIG0112 — WACK reported failures {#sig0112}

The App Certification Kit ran and found problems. The message names the report
file; open it for the specifics.

### SIG0120 — the installer host runtime is missing {#sig0120}

`formats: [exe]` needs an AOT-published installer host staged at
`runtimes/<rid>/SigilBuild.Installer.Host.exe` next to the CLI, and it is not
there. A release archive ships it; a development tree needs
`scripts/publish-installer-runtime.ps1` to have been run.

### SIG0121 — the `exe` format requires a Windows pack host {#sig0121}

Producing a `Setup.exe` stamps the payload into the host executable through the
Win32 resource-update APIs (`BeginUpdateResourceW`), which exist only on
Windows. Other declared formats still pack; the exit code reports the unmet
request.

### SIG0122 — `build.source` names a directory that does not exist {#sig0122}

`build.source` is the one manifest field the schema cannot check: it is an
assertion about the filesystem.

**Relative paths resolve against the manifest's own directory, not the working
directory.** That is the usual cause — running `sigil pack` from elsewhere, or
pointing at a directory one level off.

Packing is refused rather than continued. An absent source yields a package
with no payload, and until this check existed `pack` printed an artifact path
and exited `0`; the failure surfaced later, on the end user's machine, as a
failed install step and a rollback.

### SIG0123 — the manifest installs from `payload://` but there is no payload {#sig0123}

`build.source` exists, but it holds no files — so the package would carry an
empty payload while the install steps expect to copy out of one.

The sibling of [SIG0122](#sig0122), and the more dangerous half. A *missing*
`build.source` fails the install loudly and rolls back. An *empty* one used to
pack, install, report success and register the application in Add/Remove
Programs having laid down nothing but its own uninstaller — an installed
application that contains nothing, which is much harder to notice than a
failure.

An installer that legitimately carries no payload is unaffected: one that only
writes registry values, or `sigil pack --payload web`, whose stub downloads the
real package at install time. This fires only when the manifest actually
resolves `payload://`.

---

## SIG02xx — parameters and install steps

### SIG0210 — unknown parameter type {#sig0210}

A `parameters.<name>.type` value outside the supported set.

### SIG0220 — parameter validation failed {#sig0220}

A parameter's default or supplied value failed its own declared constraints —
pattern, range, or allowed values.

### SIG0230 — unknown install step type {#sig0230}

`install_steps[].type` is not a step Sigil knows. See the
[install steps guide](guides/install-steps.md) for the catalog.

### SIG0231 — unknown field on an install step {#sig0231}

**Warning, not an error.** The step carries a field its type does not define,
and that field is ignored. Almost always a typo or a field borrowed from a
different step type — worth fixing, because the behaviour you wrote is not the
behaviour you will get.

### SIG0232 — a required step field is missing {#sig0232}

The step omits something its type requires, most commonly `id`.

### SIG0233 — a step field holds a value outside its allowed set {#sig0233}

A bad enum — for example an unrecognized `trigger` or `run_level` on
`scheduled_task_create`.

Fatal by design: there is no safe fallback for an enum value nobody defined,
and guessing one would install something other than what the manifest says.

### SIG0234 — a parameter's `source:` block is invalid {#sig0234}

A dynamic-options `source:` block is missing required fields. All of `url`,
`items_path`, `value_property` and `label_property` are needed for the wizard
to populate the control at install time. See
[parameters](guides/parameters.md).

### SIG0235 — `http_download` URL is not HTTPS {#sig0235}

Refused at pack time. A downloaded payload is executed on the user's machine,
frequently elevated; a cleartext origin makes that an injection point.

### SIG0236 — `http_download` has no integrity checksum {#sig0236}

Refused at pack time. HTTPS authenticates the server, not the bytes you
expected from it. Declare the `sha256`.

---

## SIG024x — `installer.screens`

### SIG0240 — a screen field references an unknown parameter {#sig0240}

A custom screen binds a parameter that is not declared. Add it to the top-level
`parameters:` block.

### SIG0241 — a screen's `when` expression is invalid {#sig0241}

The expression failed to parse. The message carries the reason; the grammar is
documented in [conditional installs](guides/conditional-installs.md).

### SIG0242 — a screen title or subtitle has an unterminated token {#sig0242}

A `{` was opened and never closed. Left alone, the brace would be rendered
literally to the user.

---

## SIG025x–SIG031x — the `installer:` block

### SIG0250 — the licence file could not be read {#sig0250}

**Warning, not an error.** The file named by `installer.license` is missing,
unreadable or empty at pack time. The pack succeeds and the License screen is
omitted — so check for this if a wizard you expected to show a licence does
not.

### SIG0260 — invalid `installer.scope` {#sig0260}

**Warning, not an error.** The value is outside `{user, machine, auto}` and the
parser falls back to `auto`. The schema enum is the hard gate; this covers the
paths that reach the parser directly.

### SIG0270 — invalid `installer.vars` entry {#sig0270}

A variable expression is malformed, or the variables form a reference cycle.

A cycle is fatal: there is no evaluation order that resolves it, so there is
nothing sensible to install.

### SIG0280 — invalid prerequisite {#sig0280}

A `installer.prerequisites` entry is missing `name`, `detect` or `source`, or
an `https://` source omits the required `sha256`. A bundled `payload://` source
needs no checksum — the package's own signature covers it. See
[prerequisites](guides/prerequisites.md).

### SIG0300 — invalid custom component {#sig0300}

A component under `installer.options.components` has a name that is not a bare
identifier, collides with a built-in component or a declared parameter,
duplicates another custom component, or omits its required label.

### SIG0310 — this step requires machine scope {#sig0310}

Three steps touch machine-global state: `scheduled_task_create`,
`com_register` and `firewall_rule`. If any of them appears anywhere in the
manifest — `install_steps`, `pre_install`, `post_install`, `uninstall`, or any
`installer.hooks` phase — then `installer.scope` must be `machine`.

Note that **`auto` also fails this check**: it resolves to per-user scope by
default, and a per-user install cannot write machine-global state. Set
`scope: machine` explicitly if these steps are intended.

---

## SIG029x — localization

### SIG0290 — a localized string has no English value {#sig0290}

Fatal. Every runtime language fallback bottoms out at `en`, so a map without an
`en` key has no defined rendering — the string would ship blank. See
[localization](guides/localization.md).

### SIG0291 — invalid language tag {#sig0291}

A key in a localized map is not a language tag Sigil recognizes.

### SIG0292 — a localized value is not a plain string {#sig0292}

A language key holds a nested sequence or mapping instead of a scalar. Fatal
for the same reason as [SIG0290](#sig0290): the value would silently collapse
to an empty string, one language at a time.

---

## SIG032x — updates and network trust

[SIG0320](#sig0320) and [SIG0321](#sig0321) are raised at **update runtime**,
inside the installed application's update check. The rest are raised at pack
time, against the manifest.

### SIG0320 — malformed channel manifest {#sig0320}

The fetched update manifest failed to parse, is missing `version`,
`packageUrl` or `sha256`, declares a non-HTTPS `packageUrl`, or declares a
`schemaVersion` this build does not support.

### SIG0321 — channel manifest signature is invalid {#sig0321}

The detached ECDSA P-256 signature, fetched from `manifestUrl + ".sig"`, does
not verify against `updates.signingKey`. The update is refused. See
[updates](guides/updates.md).

### SIG0322 — the web installer's package URL could not be resolved {#sig0322}

`sigil pack --payload web` was given no `--package-url`, an empty one, or one
that is not `https://`. Pack refuses rather than stamping a stub whose
download step could never succeed.

### SIG0323 — a parameter `source.url` is not HTTPS {#sig0323}

Values fetched from that URL become parameter values, and parameters are
substituted into step fields — paths, registry coordinates, command arguments
— that execute elevated. A cleartext origin is therefore an injection point
into a privileged run.

Re-checked at install time as well, because a URL built from tokens is not
knowable at pack time.

### SIG0324 — `updates.manifestUrl` is not HTTPS {#sig0324}

The schema constrains only the URI shape, so the scheme is gated here. Code
execution is still gated by the channel-manifest signature, so the exposure is
cleartext leakage of app id, version and channel, plus a reliable
update-suppression denial of service.

### SIG0325 — `updates.signingKey` is not a valid public key {#sig0325}

The value must be a base64-encoded X.509 SPKI DER of an **ECDSA P-256 public
key**.

Unvalidated, a private-key *file path* packs cleanly and produces an installer
whose every future update dies at [SIG0321](#sig0321) — failing closed, but
only after it has shipped.

### SIG0326 — invalid `installer.require_signed_downloads` {#sig0326}

The value is outside the declared policy set. That policy governs whether a
binary pulled off the network this run must be Authenticode-valid before it is
launched elevated, so an unrecognized value is refused rather than quietly
treated as the default.

---

## SIG04xx — signing

Signing has its own band because it fails for reasons that have nothing to do
with the manifest.

### SIG0400 — local signing requires Windows {#sig0400}

`signtool.exe` is a Windows tool. Use Azure Trusted Signing, or sign on a
Windows host. See [signing](guides/signing.md).

### SIG0401 — the signing certificate is invalid {#sig0401}

The certificate could not be loaded or does not validate — wrong password,
wrong format, expired, or no private key attached.

### SIG0402 — signtool failed {#sig0402}

`signtool.exe` ran and exited non-zero. Its output carries the reason.

### SIG0410 — the Azure signing job failed {#sig0410}

The remote signing request was submitted and came back rejected.

### SIG0411 — Azure signing failed {#sig0411}

Signing through Azure Trusted Signing could not be completed — most often
credentials, endpoint or certificate-profile configuration. All six settings
must be present and refer to the same account.
