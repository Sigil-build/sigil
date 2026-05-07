# Source: app-multiuser slot

- **Upstream repo:** https://github.com/kichik/nsis
- **Commit SHA:** `1929435c23fc08342cb2fdcea2950021ce31c2ec`
- **File path within repo:** `Examples/MultiUser.nsi`
- **Upstream URL:** https://github.com/kichik/nsis/blob/1929435c23fc08342cb2fdcea2950021ce31c2ec/Examples/MultiUser.nsi
- **License:** zlib/libpng (NSIS license — see `nsis/COPYING`)
- **Why selected:** Representative of a dual-mode HKCU + HKLM installer that uses the `MultiUser.nsh` helper. Sets `MULTIUSER_EXECUTIONLEVEL Highest`, `MULTIUSER_MUI`, `MULTIUSER_INSTALLMODE_COMMANDLINE`, and demonstrates the install-mode page that lets the user pick "all users" vs "current user" at runtime. Counts here serve as a counterpoint to `app-shared`: same uninstall-key surface, but registry writes are routed through `SHCTX` rather than fixed to `HKLM`.
- **Reproduce counts:**
  ```bash
  NSIS=/c/Projects/hw/repos/Ascendly-Tools/ascendly-installer/nsis
  bash research/nsis-survey.sh \
    "app-multiuser=$NSIS/Examples/MultiUser.nsi"
  ```
