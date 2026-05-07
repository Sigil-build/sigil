# Source: app-shared slot

- **Upstream repo:** https://github.com/kichik/nsis
- **Commit SHA:** `1929435c23fc08342cb2fdcea2950021ce31c2ec`
- **File path within repo:** `Examples/install-shared.nsi`
- **Upstream URL:** https://github.com/kichik/nsis/blob/1929435c23fc08342cb2fdcea2950021ce31c2ec/Examples/install-shared.nsi
- **License:** zlib/libpng (NSIS license — see `nsis/COPYING`)
- **Why selected:** Representative of an all-users (HKLM) installer. Demonstrates the canonical "install-for-everyone" pattern: `RequestExecutionLevel Admin`, `SetShellVarContext All`, `$ProgramFiles` install directory, full Add/Remove Programs uninstall key (`DisplayName`, `DisplayIcon`, `UninstallString`, `QuietUninstallString`), and a LogicLib `${If}` admin-rights guard via `UserInfo::GetAccountType` + `Push/Pop`. Pairs naturally with `app-multiuser` for HKCU/HKLM contrast.
- **Reproduce counts:**
  ```bash
  NSIS=/c/Projects/hw/repos/Ascendly-Tools/ascendly-installer/nsis
  bash research/nsis-survey.sh \
    "app-shared=$NSIS/Examples/install-shared.nsi"
  ```
