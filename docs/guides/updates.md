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
3. **Verify, then parse.** The detached signature is verified against
   `updates.signingKey` **first**, over the exact fetched bytes; only then is
   the JSON parsed and validated against the channel-manifest contract
   (below). An unsigned or tampered manifest is a **hard reject** — the JSON
   parser never even sees it — and a malformed one is rejected too. Nothing
   is downloaded or run in either case.
4. **Check freshness and replay.** Signature proves *who* minted the document,
   never *when*. So the authenticated manifest is then checked against its own
   `issuedAt`/`expiresAt` validity window (±5 minutes of clock-skew tolerance),
   against a client-enforced **30-day maximum age** measured from `issuedAt`
   regardless of what `expiresAt` says, and against the highest `sequence`
   this machine has previously accepted for this app. Any refusal stops the
   run — see [Freshness and replay protection](#freshness-and-replay-protection).
   The high-water mark is persisted to `update-sequence.txt` in the per-app
   state directory (`%ProgramData%\Sigil\<AppId>` for a machine install,
   `%LocalAppData%\Sigil\<AppId>` for a per-user one) the moment the manifest
   is authenticated and judged fresh — **not** after the install succeeds, so a
   failed install cannot reopen the replay window.
5. **Compare versions.** The channel manifest's `version` is compared against
   the installed version using the same dotted-version comparison
   [Upgrades & downgrades](upgrades.md) uses. Same-or-older → up to date,
   clean exit.
6. **Download + run.** If newer, the full package at `packageUrl` is
   downloaded and verified against `sha256` — and, subject to
   [`installer.require_signed_downloads`](#downloaded-package-signature-policy),
   Authenticode-checked from inside the window where the verified file handle
   is still held — then the downloaded `Setup.exe`
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
  "minFromVersion": "1.0.0",
  "issuedAt": "2026-09-08T12:00:00Z",
  "expiresAt": "2026-09-15T12:00:00Z",
  "sequence": 42
}
```

| Field | Required | Notes |
| --- | --- | --- |
| `schemaVersion` | yes | Must be `1`. Any other value (including an omitted field, which defaults to `0`) is rejected outright — a future breaking schema bump fails loudly on an old installed runtime rather than being silently misread. |
| `version` | yes | The advertised package's dotted version string, compared against the installed version the same way an upgrade decides freshness (see [Upgrades & downgrades](upgrades.md)). |
| `packageUrl` | yes | **Must start with `https://`.** Mirrors the same insecure-URL stance the `http_download` install step enforces at pack time (SIG0235), applied here at update runtime instead. |
| `sha256` | yes | 64-character hex SHA-256 digest of the file at `packageUrl`. Checked before the download is trusted; a malformed (wrong-length/non-hex) value is refused up front rather than always mismatching. |
| `issuedAt` | **yes** | ISO-8601 timestamp of when this manifest was minted. Must carry an explicit zone — a trailing `Z` or a numeric offset such as `+02:00`. A zone-less local timestamp is **rejected**. |
| `expiresAt` | **yes** | ISO-8601 timestamp (same format rules) after which the manifest must not be acted on. Must not be earlier than `issuedAt`: an empty validity window is refused, not treated as permissive. That particular refusal happens in the freshness check rather than the parse, so it exits `8`, not `7`. |
| `sequence` | **yes** | Non-negative monotonic integer. Increment it on every manifest you publish for this channel; a client refuses any value **strictly lower** than the highest it has already accepted for this app (an equal value is accepted). |
| `minFromVersion` | no | The lowest installed version this package can update *from*. An installed version below the floor is treated as "an update exists, but not for you" (see [exit codes](#update-behavior-and-exit-codes)) rather than silently skipped or force-installed. Omit it if any older installed version may take this package. |

> **Breaking change — `issuedAt`, `expiresAt` and `sequence` are required.**
> They were added by [ADR-011](../architecture/adr-011-update-manifest-freshness.md)
> and every shipped installer enforces them. A channel manifest that omits any
> of the three is **malformed** (`SIG0320`), and `/Update` exits `7` without
> downloading anything — so a publisher who mints the old five-field document
> ships an update channel that no installed client will take. They are
> required rather than optional on purpose: an *optional* freshness field is
> defeated by replaying a correctly signed manifest that predates it, which is
> the exact attack.

Each fetch is exact bytes in, exact bytes verified — the channel manifest is
never re-serialized or canonicalized before its signature is checked, so
whatever bytes your server returns for `manifestUrl` are the exact bytes the
`.sig` must cover. All three freshness fields therefore live **inside** the
signed byte range by construction; there is no sidecar or header a freshness
value could arrive on unsigned.

### Freshness and replay protection

The signature says who minted the manifest, not when. Without a second gate,
an on-path attacker or a compromised CDN can replay yesterday's correctly
signed manifest indefinitely — freezing your users on a version with a known
vulnerability, or steering them onto an intermediate version that is newer
than what they have and still exploitable. So the client applies four rules to
every authenticated manifest, in this order:

| Rule | Refusal |
| --- | --- |
| Not expired | `now > expiresAt + 5 min` → refused as stale. |
| Not too old | `now > issuedAt + 30 days + 5 min` → refused, **regardless of `expiresAt`**. `expiresAt` is publisher-chosen; a manifest set to expire in the year 3000 would otherwise opt out of the whole defence. This ceiling is enforced by the client and is not configurable from the manifest. |
| Not future-dated | `issuedAt > now + 5 min` → refused rather than guessing which clock is wrong. |
| Not replayed | `sequence` below the machine's persisted high-water mark → refused as a rollback to a superseded manifest. |

The ±5-minute allowance on both ends of the window is clock-skew tolerance: a
machine with a wrong clock is far more common than one under an active replay,
and five minutes is negligible against an attack that is only interesting over
days.

Each of these four refusals exits **`8`**, the hard-security-reject code —
distinct from `7`, which is what a *malformed* manifest (a missing or
unparseable `issuedAt`, say) returns.

**Practical consequences for your publishing process.** Pick an `expiresAt`
that comfortably exceeds your release cadence but stays well inside 30 days;
re-mint and re-sign the manifest before it lapses, even when the advertised
version has not changed, or clients stop seeing updates. Bump `sequence` on
every publish and never lower it — a machine that has accepted sequence 42 will
refuse 41 forever. (Re-publishing at the *same* sequence is accepted, so
re-minting an unchanged manifest to extend its window does not force a bump —
but bumping anyway keeps the counter meaningful.)

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
- **The curve is pinned.** A key that imports cleanly but is not P-256 — a
  P-384 SPKI, say — is rejected on its key size, not accepted merely because
  the import did not throw. And an `updates:` block with a missing or empty
  `signingKey` is a **hard reject**, not "no key, so no verification needed":
  an app that ships update capability without a trust anchor cannot be trusted
  to auto-update.
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

### Downloaded-package signature policy

`sha256` proves the bytes match what the manifest advertised. It does not
prove the publisher signed them — the manifest could have advertised anything.
So, separately, the downloaded `Setup.exe` can be **Authenticode-checked**
immediately before it is launched, from inside the window where the verified
file handle is still held (so nothing can swap the file between check and
run). Whether that check is armed is declared by `installer.require_signed_downloads`:

| Value | Behaviour |
| --- | --- |
| `sign_declared` *(default)* | The check is armed only when this manifest declares a `sign:` block. This is the historical behaviour, kept as the default so no existing manifest changes meaning — but note it infers "should downloads be verified?" from "did the publisher configure signing for their own output?", which are different questions. When the check is *not* armed, that is said out loud on the log rather than passing silently. |
| `always` | Armed regardless of whether a `sign:` block exists. |
| `always_verified_revocation` | As `always`, and additionally **refuses** a binary whose revocation status could not be established. By default an unreachable CRL/OCSP responder is a warning, because refusing would break installs behind a captive portal, on an air-gapped network, or inside a locked-down enterprise egress. Turn this on when you know your audience is reliably online. |

An unrecognized value is a pack-time diagnostic, `SIG0326`.

This setting governs update packages and the web-stub payload. It does **not**
govern prerequisites: a downloaded prerequisite installer is always
signature-checked, and carries its own per-prerequisite `allow_unsigned`
opt-out — see [Prerequisites](prerequisites.md).

## `/Update` behavior and exit codes

`Setup.exe /Update` runs headlessly by default (no wizard), logging each
stage to the console and, if `/LOG` was also passed, to the log file — see
[`/LOG` below](#logging). Codes `6`-`9` are dedicated to `/Update` and are not
used by the install/uninstall path, which has its own set — `0`, `1`, `2`, `3`,
`4` (files in use), `5` (another setup instance is already running), `64`
(usage), `78` (native-runtime bootstrap refused) and `3010` (success, reboot
required). See the [setup.exe reference](../setup-exe-reference.md) for the
full table.

| Exit code | Meaning |
| --- | --- |
| `0` | Up to date — nothing to do, or the installed version is already the same or newer than the channel manifest advertises. |
| `6` | Not update-enabled — the manifest declared no `updates.manifestUrl`, so there is nothing to check. |
| `7` | Check/apply failed — a network failure fetching the channel manifest or its signature, a **malformed** channel manifest (`SIG0320` — including one missing `issuedAt`, `expiresAt` or `sequence`), an implausible `sha256`, or a failed package download / child spawn. An operational failure: nothing was changed. |
| `8` | **Hard security reject**, kept distinct from `7` so a security event is unambiguous in logs and automation. Covers three distinct causes, so do not read `8` as "tampering" alone: (a) the channel manifest's **signature** did not verify (`SIG0321`) — tampered, unsigned, wrong key, or wrong curve; (b) the manifest was authentic but **failed the freshness/replay check** — expired, older than 30 days, future-dated, an empty validity window, or a `sequence` **lower than** one already accepted; (c) the **downloaded package was refused by Authenticode** under the `require_signed_downloads` policy. The log line distinguishes them. |
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
