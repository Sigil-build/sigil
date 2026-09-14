// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

namespace SigilBuild.Core.Manifest;

public enum SignProvider { None, Local, AzureTrustedSigning }

public sealed record LocalSignConfig(
    string Pfx,
    string? PasswordEnv,
    string? TimestampUrl);

public sealed record AzureTrustedSigningConfig(
    string Endpoint,
    string AccountName,
    string CertificateProfile,
    string TenantIdEnv,
    string ClientIdEnv,
    string ClientSecretEnv);

public sealed record SignSection(
    SignProvider Provider,
    LocalSignConfig? Local,
    AzureTrustedSigningConfig? AzureTrustedSigning);
