# Source: web-iis slot

- **Upstream repo:** https://github.com/wixtoolset/wix
- **Commit SHA:** `b8977d6f88e7b68e000bac226a2814f236770570`
- **File paths within repo:**
  - `src/ext/Iis/test/WixToolsetTest.Iis/TestData/UsingIis/Package.wxs`
  - `src/ext/Iis/test/WixToolsetTest.Iis/TestData/UsingIis/PackageComponents.wxs`
- **Upstream URLs:**
  - https://github.com/wixtoolset/wix/tree/main/src/ext/Iis/test/WixToolsetTest.Iis/TestData/UsingIis/Package.wxs
  - https://github.com/wixtoolset/wix/tree/main/src/ext/Iis/test/WixToolsetTest.Iis/TestData/UsingIis/PackageComponents.wxs
- **License:** MS-RL (WiX Toolset license — see `wix/LICENSE.TXT`)
- **Why selected:** Representative of an IIS-hosted web application installer. The `Package.wxs` declares the package + feature; `PackageComponents.wxs` instantiates `iis:WebSite`, `iis:WebAddress`, `iis:WebVirtualDir`, `iis:WebApplication`, `iis:WebAppPool`, and `iis:WebDirProperties` — exercising the IIS extension that ships in the WiX 5 `Iis` extension. Counts across both files are summed under one column.
- **Reproduce counts:**
  ```bash
  WIX_REPO=/c/Projects/hw/repos/Ascendly-Tools/ascendly-installer/wix
  bash research/survey.sh \
    "web-iis=$WIX_REPO/src/ext/Iis/test/WixToolsetTest.Iis/TestData/UsingIis/Package.wxs:$WIX_REPO/src/ext/Iis/test/WixToolsetTest.Iis/TestData/UsingIis/PackageComponents.wxs"
  ```
