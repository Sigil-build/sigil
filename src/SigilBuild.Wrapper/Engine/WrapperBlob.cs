using System;
using System.Collections.Generic;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Wrapper.Engine;

/// <summary>
/// Embedded blob describing the parameters and step list for the wrapper.
/// At pack time, <c>WrapperResourceWriter</c> embeds this as a Win32 resource
/// in the wrapper exe; at install time, <see cref="LoadFromSelf"/> reads it
/// back.
/// </summary>
/// <remarks>
/// Task 12 stub: returns an empty blob so <see cref="Program.Main"/> can wire
/// the parser through the engine end-to-end. The Win32 resource read lands in
/// Task 14.
/// </remarks>
internal sealed record WrapperBlob(
    string AppId,
    IReadOnlyList<ParameterDefinition> Parameters,
    IReadOnlyList<InstallStep> InstallSteps,
    IReadOnlyList<InstallStep> PreInstall,
    IReadOnlyList<InstallStep> PostInstall,
    IReadOnlyList<InstallStep> UpdateSteps)
{
    /// <summary>
    /// Empty sentinel blob: well-known <c>AppId</c> placeholder and zero-length
    /// step / parameter lists. Used by the Task 12 stub of
    /// <see cref="LoadFromSelf"/> until the real Win32 resource read lands.
    /// </summary>
    public static WrapperBlob Empty { get; } = new(
        AppId: "<unset>",
        Parameters: Array.Empty<ParameterDefinition>(),
        InstallSteps: Array.Empty<InstallStep>(),
        PreInstall: Array.Empty<InstallStep>(),
        PostInstall: Array.Empty<InstallStep>(),
        UpdateSteps: Array.Empty<InstallStep>());

    /// <summary>
    /// Read the blob from the running executable's embedded Win32 resource.
    /// </summary>
    /// <remarks>
    /// TODO(Task 14): replace this stub with the real Win32 <c>FindResource</c>
    /// / <c>LoadResource</c> / <c>LockResource</c> sequence. For now it returns
    /// <see cref="Empty"/> so <see cref="Program.Main"/> wiring compiles.
    /// </remarks>
    public static WrapperBlob LoadFromSelf() => Empty;
}
