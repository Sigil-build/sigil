// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.Collections.Generic;

namespace SigilBuild.Core.Manifest;

public sealed record SigilManifest(
    string Spec,
    AppSection App,
    BuildSection Build,
    PackageSection? Package,
    SignSection? Sign,
    PublishSection? Publish,
    UpdatesSection? Updates,
    InstallerSection? Installer,
    SourceLocation Location,
    IReadOnlyDictionary<string, ParameterDefinition>? Parameters = null,
    IReadOnlyList<InstallStep>? InstallSteps = null,
    IReadOnlyList<InstallStep>? PreInstall = null,
    IReadOnlyList<InstallStep>? PostInstall = null,
    // uninstall: steps that run when the wrapper is invoked with /Uninstall,
    // BEFORE the rollback journal replays. Lets the manifest tear down anything
    // that wasn't journalled (services + scheduled tasks + custom PS scripts)
    // via the same run_program / file_copy / registry_* surface install_steps use.
    IReadOnlyList<InstallStep>? Uninstall = null);
