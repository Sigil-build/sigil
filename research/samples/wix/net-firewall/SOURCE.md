# Source: net-firewall slot

- **Upstream repo:** https://github.com/wixtoolset/wix
- **Commit SHA:** `b8977d6f88e7b68e000bac226a2814f236770570`
- **File paths within repo:**
  - `src/ext/Firewall/test/WixToolsetTest.Firewall/TestData/UsingFirewall/Package.wxs`
  - `src/ext/Firewall/test/WixToolsetTest.Firewall/TestData/UsingFirewall/PackageComponents.wxs`
- **Upstream URLs:**
  - https://github.com/wixtoolset/wix/tree/main/src/ext/Firewall/test/WixToolsetTest.Firewall/TestData/UsingFirewall/Package.wxs
  - https://github.com/wixtoolset/wix/tree/main/src/ext/Firewall/test/WixToolsetTest.Firewall/TestData/UsingFirewall/PackageComponents.wxs
- **License:** MS-RL (WiX Toolset license — see `wix/LICENSE.TXT`)
- **Why selected:** Representative of a network-listening application that needs Windows Firewall rules. Exercises the WiX 5 `Firewall` extension via `<fw:RemoteAddress>`, ipv4/ipv6 scope variants, and 21 distinct `<fw:FirewallException>` declarations spread across program-, port-, and service-scoped variants — which is the highest density of firewall rules anywhere in the WiX corpus. Also includes a `<ServiceInstall>` + `<Shortcut>` for completeness. Counts across both files are summed.
- **Reproduce counts:**
  ```bash
  WIX_REPO=/c/Projects/hw/repos/Ascendly-Tools/ascendly-installer/wix
  bash research/survey.sh \
    "net-firewall=$WIX_REPO/src/ext/Firewall/test/WixToolsetTest.Firewall/TestData/UsingFirewall/Package.wxs:$WIX_REPO/src/ext/Firewall/test/WixToolsetTest.Firewall/TestData/UsingFirewall/PackageComponents.wxs"
  ```
