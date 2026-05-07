# Source: app-modernui slot

- **Upstream repo:** https://github.com/kichik/nsis
- **Commit SHA:** `1929435c23fc08342cb2fdcea2950021ce31c2ec`
- **File path within repo:** `Examples/Modern UI/StartMenu.nsi`
- **Upstream URL:** https://github.com/kichik/nsis/blob/1929435c23fc08342cb2fdcea2950021ce31c2ec/Examples/Modern%20UI/StartMenu.nsi
- **License:** zlib/libpng (NSIS license — see `nsis/COPYING`)
- **Why selected:** Representative of a Modern UI (MUI2) installer that uses the Start-Menu page. Demonstrates the canonical MUI2 page sequence (`MUI_PAGE_WELCOME` → `MUI_PAGE_LICENSE` → `MUI_PAGE_COMPONENTS` → `MUI_PAGE_DIRECTORY` → `MUI_PAGE_STARTMENU` → `MUI_PAGE_INSTFILES`), `MUI_STARTMENUPAGE_*` configuration variables, the `MUI_STARTMENU_WRITE_BEGIN`/`MUI_STARTMENU_WRITE_END` block that wraps `CreateShortcut` against the user-selected folder, `LangString` + `MUI_DESCRIPTION_TEXT` for component-tooltip i18n, and `!insertmacro MUI_LANGUAGE "English"`. Useful contrast to `app-bigtest`'s classic-UI flows. Note: this example deliberately uses `!insertmacro MUI_*` rather than raw `File` commands for shipping content, which is why the `File` row is 0 for this slot — Modern UI compositions hide payload writes behind macro layers.
- **Reproduce counts:**
  ```bash
  NSIS=/c/Projects/hw/repos/Ascendly-Tools/ascendly-installer/nsis
  bash research/nsis-survey.sh \
    "app-modernui=$NSIS/Examples/Modern UI/StartMenu.nsi"
  ```
