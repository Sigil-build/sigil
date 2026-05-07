# Source: db-sql slot

- **Upstream repo:** https://github.com/wixtoolset/wix
- **Commit SHA:** `b8977d6f88e7b68e000bac226a2814f236770570`
- **File paths within repo:**
  - `src/ext/Sql/test/WixToolsetTest.Sql/TestData/UsingSql/Package.wxs`
  - `src/ext/Sql/test/WixToolsetTest.Sql/TestData/UsingSql/PackageComponents.wxs`
- **Upstream URLs:**
  - https://github.com/wixtoolset/wix/tree/main/src/ext/Sql/test/WixToolsetTest.Sql/TestData/UsingSql/Package.wxs
  - https://github.com/wixtoolset/wix/tree/main/src/ext/Sql/test/WixToolsetTest.Sql/TestData/UsingSql/PackageComponents.wxs
- **License:** MS-RL (WiX Toolset license — see `wix/LICENSE.TXT`)
- **Why selected:** Representative of a SQL-database-and-service installer. Exercises the WiX 5 `Sql` extension (`sql:SqlDatabase`, `sql:SqlScript`, `sql:SqlString`) plus a typical Windows service install (`<ServiceInstall>` + `<File>` payload). Counts across both files are summed.
- **Reproduce counts:**
  ```bash
  WIX_REPO=/c/Projects/hw/repos/Ascendly-Tools/ascendly-installer/wix
  bash research/survey.sh \
    "db-sql=$WIX_REPO/src/ext/Sql/test/WixToolsetTest.Sql/TestData/UsingSql/Package.wxs:$WIX_REPO/src/ext/Sql/test/WixToolsetTest.Sql/TestData/UsingSql/PackageComponents.wxs"
  ```
