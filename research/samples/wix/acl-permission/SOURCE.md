# Source: acl-permission slot

- **Upstream repo:** https://github.com/wixtoolset/wix
- **Commit SHA:** `b8977d6f88e7b68e000bac226a2814f236770570`
- **File paths within repo:**
  - `src/ext/Util/test/WixToolsetTest.Util/TestData/PermissionEx/Package.wxs`
  - `src/ext/Util/test/WixToolsetTest.Util/TestData/PermissionEx/PackageComponents.wxs`
- **Upstream URLs:**
  - https://github.com/wixtoolset/wix/tree/main/src/ext/Util/test/WixToolsetTest.Util/TestData/PermissionEx/Package.wxs
  - https://github.com/wixtoolset/wix/tree/main/src/ext/Util/test/WixToolsetTest.Util/TestData/PermissionEx/PackageComponents.wxs
- **License:** MS-RL (WiX Toolset license — see `wix/LICENSE.TXT`)
- **Why selected:** Representative of a privileged install that grants ACLs on filesystem, registry, and service objects. Exercises `<util:PermissionEx>` against `<File>`, `<RegistryKey>`, and `<ServiceInstall>` parents — five distinct `util:PermissionEx` instances, the canonical pattern for "this app needs ACL grants". Counts across both files are summed.
- **Reproduce counts:**
  ```bash
  WIX_REPO=/c/Projects/hw/repos/Ascendly-Tools/ascendly-installer/wix
  bash research/survey.sh \
    "acl-permission=$WIX_REPO/src/ext/Util/test/WixToolsetTest.Util/TestData/PermissionEx/Package.wxs:$WIX_REPO/src/ext/Util/test/WixToolsetTest.Util/TestData/PermissionEx/PackageComponents.wxs"
  ```
