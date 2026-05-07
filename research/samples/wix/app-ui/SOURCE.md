# Source: app-ui slot

- **Upstream repo:** https://github.com/wixtoolset/wix
- **Commit SHA:** `b8977d6f88e7b68e000bac226a2814f236770570`
- **File path within repo:** `src/wix/test/WixToolsetTest.CoreIntegration/TestData/Decompile/ExpectedUI.wxs`
- **Upstream URL:** https://github.com/wixtoolset/wix/tree/main/src/wix/test/WixToolsetTest.CoreIntegration/TestData/Decompile/ExpectedUI.wxs
- **License:** MS-RL (WiX Toolset license — see `wix/LICENSE.TXT`)
- **Why selected:** Representative of a UI-heavy desktop application installer. ~580 lines covering every WixUI dialog template (`FatalError`, `UserExit`, `ExitDialog`, `WelcomeDlg`, `LicenseAgreementDlg`, `InstallDirDlg`, `VerifyReadyDlg`, etc.), exercising the `<UI>`, `<Dialog>`, `<Control>`, `<Publish>`, `<TextStyle>`, and `<UIRef>` element families on top of a minimal file/component layout. Note that this is the `Decompile` test fixture, so it has many UI conditionals (`Condition=` on `<Publish>` events) but only one product `<File>`/`<Component>` — UI density is the point of this slot.
- **Reproduce counts:**
  ```bash
  WIX_REPO=/c/Projects/hw/repos/Ascendly-Tools/ascendly-installer/wix
  bash research/survey.sh \
    "app-ui=$WIX_REPO/src/wix/test/WixToolsetTest.CoreIntegration/TestData/Decompile/ExpectedUI.wxs"
  ```
