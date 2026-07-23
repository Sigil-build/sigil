# Updates

Sigil ships a small, signed **update engine** built into every `exe` package:
`Setup.exe /Update` checks a channel manifest you host, and — when a newer
version is available and its signature checks out — downloads and runs it,
performing the same version-aware upgrade described in
[Upgrades & downgrades](upgrades.md). There is no separate updater binary and
no delta-patch SDK yet (see the [ADR on delta-update deferral](../architecture/adr-010-delta-update-deferral.md));
today's engine always fetches the **full package**.

This guide covers the `updates:` manifest block, the channel-manifest
contract your hosting must serve, the security model, `/Update`'s exit codes,
and the web installer (`pack --payload web`) that lets your first-download
`Setup.exe` stay tiny.

## How it works

1. **`Setup.exe /Update` runs** (headless, e.g. from a scheduled task, or
   headed from inside the installed app). It reads the `updates:` block that
   was stamped into the exe at pack time.
2. **Fetch.** It downloads the JSON body at `updates.manifestUrl`, and the
   detached signature at `updates.manifestUrl + ".sig"`.
3. **Parse + verify.** The JSON is validated against the channel-manifest
   contract (below); the signature is verified against `updates.signingKey`.
   A malformed or unsigned/tampered manifest is a **hard reject** — nothing
   is downloaded or run.
4. **Compare versions.** The channel manifest's `version` is compared against
   the installed version using the same dotted-version comparison
   [Upgrades & downgrades](upgrades.md) uses. Same-or-older → up to date,
   clean exit.
5. **Download + run.** If newer, the full package at `packageUrl` is
   downloaded and verified against `sha256`, then the downloaded `Setup.exe`
   is launched to perform the actual upgrade (silently for a headless
   `/Update`, or with its own wizard visible for a headed one). This process
   never re-implements install logic itself — it hands off to the new
   version's own installer and propagates its exit code.

## The `updates:` manifest block

```yaml
updates:
  channel: stable
  manifestUrl: https://updates.example.com/stable/channel.json
  signingKey: "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE...=="
  deltaTargets: 3
```

| Field | Required | Notes |
| --- | --- | --- |
| `channel` | no | Free-form label (default `stable`). Purely descriptive — it names which channel manifest you're pointing at (e.g. `stable` vs `beta`); Sigil does not host or resolve channels for you. |
| `manifestUrl` | for `/Update` to work | HTTPS URL of the channel manifest JSON. Its signature is expected at the same URL with `.sig` appended. Omit this field entirely to ship an installer with no update capability — `/Update` then exits cleanly with a distinct "not configured" code (below) rather than failing. |
| `signingKey` | for `/Update` to work | The base64-encoded **X.509 SubjectPublicKeyInfo (SPKI, DER)** encoding of the ECDSA P-256 **public** key that signs your channel manifests — i.e. what `ECDsa.ExportSubjectPublicKeyInfo()` returns, base64-encoded. This is embedded in the packed `Setup.exe` as the trust anchor; see [Security model](#security-model). |
| `deltaTargets` | no | How many previous versions a future delta-patch generator would target (`0`–`20`, default `3`). **Not yet consumed by the update runtime** — full-package updates ship first; see the [delta-update deferral ADR](../architecture/adr-010-delta-update-deferral.md). Safe to set now for forward compatibility; it has no effect today. |

> **Generating the signing key pair.** Any ECDSA P-256 (`nistP256`/`secp256r1`)
> key pair works — for example, in .NET:
> ```csharp
> using var ecdsa = System.Security.Cryptography.ECDsa.Create(
>     System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
> var publicKeyBase64 = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
> // publicKeyBase64 -> updates.signingKey
> // Keep the private key (ecdsa.ExportPkcs8PrivateKey()) offline; it signs
> // every channel manifest you publish and is never embedded anywhere.
> ```
> Keep the private key off the build machine — it is not a Sigil input at
> all, only the *public* key is. See [Security model](#security-model) for
> why this direction of trust matters.

## The channel manifest contract

`manifestUrl` must serve a small JSON document — this is what `/Update`
fetches and checks version freshness against:

```json
{
  "schemaVersion": 1,
  "version": "1.4.0",
  "packageUrl": "https://cdn.example.com/releases/MyApp-1.4.0-x64-Setup.exe",
  "sha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
  "minFromVersion": "1.0.0"
}
```

| Field | Required | Notes |
| --- | --- | --- |
| `schemaVersion` | yes | Must be `1`. Any other value (including an omitted field, which defaults to `0`) is rejected outright — a future breaking schema bump fails loudly on an old installed runtime rather than being silently misread. |
| `version` | yes | The advertised package's dotted version string, compared against the installed version the same way an upgrade decides freshness (see [Upgrades & downgrades](upgrades.md)). |
| `packageUrl` | yes | **Must start with `https://`.** Mirrors the same insecure-URL stance the `http_download` install step enforces at pack time (SIG0235), applied here at update runtime instead. |
| `sha256` | yes | 64-character hex SHA-256 digest of the file at `packageUrl`. Checked before the download is trusted; a malformed (wrong-length/non-hex) value is refused up front rather than always mismatching. |
| `minFromVersion` | no | The lowest installed version this package can update *from*. An installed version below the floor is treated as "an update exists, but not for you" (see [exit codes](#update-exit-codes)) rather than silently skipped or force-installed. Omit it if any older installed version may take this package. |

Each fetch is exact bytes in, exact bytes verified — the channel manifest is
never re-serialized or canonicalized before its signature is checked, so
whatever bytes your server returns for `manifestUrl` are the exact bytes the
`.sig` must cover.

## Security model

The channel manifest is signed with **ECDSA P-256**, verified entirely with
.NET's built-in `System.Security.Cryptography.ECDsa` — no third-party crypto
library, no native dependency. See
[ADR-009](../architecture/adr-009-update-manifest-signature.md) for the full
rationale (in short: the update runtime does not reference Sigil's signing
stack at all, and BCL ECDSA keeps it that way at effectively zero size cost).

- **Detached signature.** The signature lives at `manifestUrl + ".sig"` as a
  sibling HTTP resource — never inline in the JSON. It is the base64 encoding
  of a raw **IEEE P1363** (`r‖s`, 64-byte) ECDSA P-256 signature over SHA-256
  of the manifest's exact fetched bytes — i.e. exactly what
  `ECDsa.SignData(bytes, HashAlgorithmName.SHA256)` produces with .NET's
  default signature format. No ASN.1/DER signature encoding is involved.
- **Trust anchor: the pack-embedded public key.** `updates.signingKey` is
  read from the manifest **at pack time** and stamped into the produced
  `Setup.exe`. The already-installed application — not the update server —
  is the source of truth for which key is trusted. A hostile or compromised
  manifest host cannot supply its own key and self-certify; it can only serve
  content that verifies against the key your users already have installed.
- **Hard rejection.** Every failure mode — a bad signature, a missing or
  malformed key, a wrong-curve key, an unsigned manifest, a network failure
  fetching the `.sig` — is treated as a **hard reject**: nothing is
  downloaded, nothing is run, and the failure is distinguished in the exit
  code and log from an ordinary network/availability failure (below). A
  tampered channel manifest never silently "wins" by falling back to some
  looser mode.
- **Package integrity via `sha256`.** Independent of the manifest's own
  signature, the downloaded package itself is verified against the channel
  manifest's `sha256` before it is ever executed — the same download +
  verify path the `http_download` install step already uses.

## `/Update` behavior and exit codes

`Setup.exe /Update` runs headlessly by default (no wizard), logging each
stage to the console and, if `/LOG` was also passed, to the log file — see
[`/LOG` below](#logging). Exit codes are dedicated and distinct from the
install/uninstall path's `0`/`1`/`2`/`3`/`64`:

| Exit code | Meaning |
| --- | --- |
| `0` | Up to date — nothing to do, or the installed version is already the same or newer than the channel manifest advertises. |
| `6` | Not update-enabled — the manifest declared no `updates.manifestUrl`, so there is nothing to check. |
| `7` | Check/apply failed — a network failure fetching the channel manifest or its signature, a malformed channel manifest, an implausible `sha256`, or a failed package download / child spawn. An operational failure: nothing was changed. |
| `8` | Channel manifest signature rejected — a tampered or unsigned manifest. A **hard security reject**, kept distinct from `7` so a tampering event is unambiguous in logs and automation. |
| `9` | Not eligible — a newer version exists, but the installed version is below the channel manifest's `minFromVersion` floor and cannot take this package via this path. |
| *(the downloaded installer's own code)* | When a newer package is downloaded and run, `/Update` exits with **whatever exit code that child `Setup.exe` returns** (typically `0` on success, `3010` if it reports reboot-required) — `/Update` propagates it rather than inventing its own "upgrade succeeded" code. |

### `/Update /silent` vs. headed

- **Headless (the default for `/Update` run standalone, e.g. from a
  scheduled task):** every stage is reported to the console/log only; the
  downloaded child `Setup.exe` is itself launched **silently** (forwarding
  only the resolved `/allusers` or `/currentuser` scope flag), so the whole
  chain is unattended end to end.
- **Headed** (driven from inside the installed app, e.g. an in-app "Check
  for updates" action): the same check → verify → download decision logic
  runs, but progress is reported through a small branded window instead of
  the console, and — unlike the headless path — the downloaded child
  `Setup.exe` is launched **without** `/silent`, so the user sees the new
  version's own install wizard take over for the actual upgrade. Both paths
  share one decision engine; only the reporting sink and the child's
  silence differ.

### `/LOG`

`/Update` honors the same `/LOG[=path]` convention as install and uninstall
(bare `/LOG` resolves to `%TEMP%\sigil-<appid>.log`; `/LOG=path` picks an
explicit file) — every stage of the update flow (channel checked, signature
verified, version compared, download started, child launched, final exit
code) is written to it alongside the console.

## The web installer

By default, `sigil pack` stamps your full app payload directly into
`Setup.exe` ("embedded" payload — the original, unchanged behavior). For an
app distributed primarily over the web, a multi-hundred-MB embedded
`Setup.exe` is a poor first download. `--payload web` instead produces:

1. The normal, full package artifact (`<App>-<version>-<arch>-Setup.exe`),
   exactly as before — this is what gets uploaded to your CDN/host.
2. A second, tiny **stub** `Setup.exe` (suffixed `WebSetup`) whose *only*
   install action is to download the full package from `--package-url`
   (verified against its own just-computed `sha256`) and run it — the
   Burn/NSIS-style "web installer" pattern. This is what you hand out as the
   small, fast first download; the stub's own progress screen is the only UI
   shown while the real package downloads and takes over.

```bash
sigil pack sigil.yaml --payload web \
  --package-url https://cdn.example.com/releases/MyApp-1.4.0-x64-Setup.exe
```

`--package-url` is **required** when `--payload web` is used, and must be a
resolvable HTTPS URL — `sigil pack` refuses up front (diagnostic **SIG0322**)
if it is missing, empty, or not `https://`, since a stub whose download step
can never succeed is worse than not producing one. `--payload embedded`
(the default) ignores `--package-url` entirely.

## See also

- [Upgrades & downgrades](upgrades.md) — the version-comparison and
  install-directory-preservation logic the downloaded update package goes
  through once launched.
- [ADR-009: ECDSA P-256 channel-manifest signatures](../architecture/adr-009-update-manifest-signature.md)
- [ADR-010: delta-update deferral](../architecture/adr-010-delta-update-deferral.md)
