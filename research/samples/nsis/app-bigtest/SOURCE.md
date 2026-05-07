# Source: app-bigtest slot

- **Upstream repo:** https://github.com/kichik/nsis
- **Commit SHA:** `1929435c23fc08342cb2fdcea2950021ce31c2ec`
- **File path within repo:** `Examples/bigtest.nsi`
- **Upstream URL:** https://github.com/kichik/nsis/blob/1929435c23fc08342cb2fdcea2950021ce31c2ec/Examples/bigtest.nsi
- **License:** zlib/libpng (NSIS license — see `nsis/COPYING`)
- **Why selected:** Comprehensive feature exerciser used by the NSIS team to smoke-test the compiler. ~280 lines covering license / components / directory / instfiles pages, multiple `Section` blocks (including hidden/required sections), `IfFileExists` re-install detection, `MessageBox` flow control with `MB_YESNO`/`MB_RETRYCANCEL`/`MB_OK`, `WriteINIStr` for an Internet shortcut, `ExecWait` for invoking external tools, and a full uninstall section. Anchors the high-water mark for `MessageBox` density (19 in this slot vs 2 across all others combined).
- **Reproduce counts:**
  ```bash
  NSIS=/c/Projects/hw/repos/Ascendly-Tools/ascendly-installer/nsis
  bash research/nsis-survey.sh \
    "app-bigtest=$NSIS/Examples/bigtest.nsi"
  ```
