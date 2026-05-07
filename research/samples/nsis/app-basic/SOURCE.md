# Source: app-basic slot

- **Upstream repo:** https://github.com/kichik/nsis
- **Commit SHA:** `1929435c23fc08342cb2fdcea2950021ce31c2ec`
- **File path within repo:** `Examples/example2.nsi`
- **Upstream URL:** https://github.com/kichik/nsis/blob/1929435c23fc08342cb2fdcea2950021ce31c2ec/Examples/example2.nsi
- **License:** zlib/libpng (NSIS license — see `nsis/COPYING`)
- **Why selected:** Representative of a bare-bones NSIS installer. Adds the minimum viable feature set on top of `example1.nsi`: persistent install directory (`InstallDirRegKey`), uninstall section with explicit per-file `Delete` and `RmDir` calls, optional Start-Menu shortcut section, and a Windows-Vista+ `RequestExecutionLevel admin` directive. Roughly 80 lines, no LogicLib, no Modern UI — the purest "happy-path" template.
- **Reproduce counts:**
  ```bash
  NSIS=/c/Projects/hw/repos/Ascendly-Tools/ascendly-installer/nsis
  bash research/nsis-survey.sh \
    "app-basic=$NSIS/Examples/example2.nsi"
  ```
