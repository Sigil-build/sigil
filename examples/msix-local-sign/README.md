# msix-local-sign example

Demonstrates MSIX packaging with a local PFX signing certificate.

## Structure

```
out/               ← your compiled app binary goes here
  LocalSignedApp.exe   (placeholder — replace with your real build output)
assets/
  logo.png         ← 512×512 source logo; Sigil resizes automatically
certs/
  codesign.pfx     ← your local signing certificate (not committed)
sigil.yaml
```

## Quick start

1. Build your app and copy the output to `out/`
2. Export a self-signed PFX cert to `certs/codesign.pfx`
3. Set the password env var: `$env:SIGIL_PFX_PASSWORD = "yourpassword"`
4. Run: `sigil pack sigil.yaml --out dist/`

The executable name inside the MSIX is derived from the last segment of `app.id`
in `sigil.yaml`. For `id: com.example.LocalSignedApp` this becomes `LocalSignedApp.exe`,
so your compiled binary must have that filename.
