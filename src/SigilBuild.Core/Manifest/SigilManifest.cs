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
    IReadOnlyList<InstallStep>? PostInstall = null);
