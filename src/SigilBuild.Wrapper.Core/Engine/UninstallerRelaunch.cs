// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

namespace SigilBuild.Wrapper.Engine;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using SigilBuild.Wrapper.Cli;

/// <summary>
/// Runs the uninstall from a private temporary copy of <c>uninstall.exe</c>, so the
/// copy in the install directory — and with it the directory — can be removed instead of
/// lingering until the next reboot. The NSIS <c>Au_.exe</c> / Inno <c>_iu*.tmp</c> shape.
/// (R106)
/// </summary>
/// <remarks>
/// <para>
/// <b>The handoff.</b> The uninstaller in the install directory, once it holds the
/// single-instance guard, copies its own image into a fresh, hardened, randomly named
/// directory (<see cref="SecureStaging"/>: <c>%ProgramData%\sigil-uninstall-{guid}</c>
/// administrator-only when elevated, a private <c>%TEMP%</c> directory otherwise),
/// re-verifies the copy's SHA-256 from a handle that denies write and delete, and starts
/// the copy with the original arguments plus <c>/Uninstall</c> and
/// <c>/RelaunchedFrom=&lt;original&gt;</c> while still holding that handle (the R5
/// pattern). It then WAITS for the copy and exits with its exit code: the ARP
/// <c>QuietUninstallString</c> caller, a script and an upgrading Setup all read that code,
/// and Programs and Features refreshes its list when the process it started exits, so the
/// interactive uninstall waits too.
/// </para>
/// <para>
/// <b>How the copy finds the install.</b> Exactly as the original does: the app id comes
/// from the blob the copy carries (it is the same image), the scope from the forwarded
/// <c>/allusers</c> / <c>/currentuser</c>, and the install directory from the state file
/// and the ARP row. Nothing in the uninstall derives the install directory from the
/// running image's location, so a copy elsewhere uninstalls the same install.
/// </para>
/// <para>
/// <b>Privilege.</b> The copy is started with <c>CreateProcess</c> and inherits this
/// process's token: an elevated machine-scope uninstall (which elevated BEFORE this point,
/// from the install directory's image) relaunches elevated, a per-user one relaunches
/// unelevated. Nothing here ever elevates or drops privilege.
/// </para>
/// <para>
/// <b>Removing the original.</b> While the copy replays the journal the original is the
/// mapped image of this waiting process and cannot be deleted, but it can be renamed:
/// <see cref="SelfDelete"/> moves it into the copy's directory, the install directory is
/// left empty, and the journal's <c>remove_directory</c> records remove it. When this
/// process wakes it deletes the copy and schedules what remains of the private directory
/// — its own renamed image — for deletion at the next restart.
/// </para>
/// <para>
/// <b>Fail-safe.</b> Any failure before the copy has started — no hardened directory, a
/// copy that does not verify, a refused spawn — returns <c>null</c> and the caller runs
/// the uninstall in place, as it did before this relaunch existed: the original is then
/// scheduled for deletion at the next restart. A copy that does not verify is never run.
/// </para>
/// </remarks>
public static partial class UninstallerRelaunch
{
    /// <summary>
    /// The reserved switch, including its <c>=</c>, naming the original
    /// <c>uninstall.exe</c> to its temporary copy. Not user-facing input.
    /// </summary>
    public const string Switch = "/RelaunchedFrom=";

    /// <summary>The <see cref="SecureStaging"/> purpose tag of the copy's directory.</summary>
    internal const string StagingPurpose = "uninstall";

    // The prefix SelfDelete gives the original when it renames it into the copy's
    // directory. Cleanup tries such a file once — it is the waiting parent's own image.
    private const string SetAsidePrefix = "original-";

    // How long cleanup keeps retrying the copy, whose image section can outlive the
    // process-exit signal by a moment.
    private const int CopyDeleteAttempts = 40;
    private static readonly TimeSpan CopyDeleteDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Prepare an <c>uninstall.exe</c> process before anything else runs: resolve a
    /// relative <c>/LOG=</c> against the directory it was started in, and move the
    /// current directory out of the install directory. A process whose current directory
    /// is the install directory keeps it from being removed — and double-clicking
    /// <c>uninstall.exe</c> in Explorer starts it in exactly that directory. Returns
    /// <paramref name="args"/> unchanged for any other image. (R106)
    /// </summary>
    /// <remarks>
    /// The new current directory is the system directory, never <c>%TEMP%</c>: .NET
    /// resolves a bare program name against the current directory first, and a hook run by
    /// an elevated uninstall must not find a <c>powershell.exe</c> a user planted there.
    /// </remarks>
    public static string[] PrepareProcess(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var image = Environment.ProcessPath;
        if (!IsUninstallerImage(image))
        {
            return args;
        }

        var cwd = Environment.CurrentDirectory;
        var rewritten = AbsolutizeLogPath(args, cwd);

        var imageDir = Path.GetDirectoryName(Path.GetFullPath(image!));
        if (!string.IsNullOrEmpty(imageDir) && IsWithin(cwd, imageDir))
        {
#pragma warning disable CA1031 // Best-effort: a current directory we cannot leave costs only the empty folder.
            try
            {
                Environment.CurrentDirectory = Environment.SystemDirectory;
            }
            catch (Exception)
            {
                // The directory stays behind, empty; the uninstall itself is unaffected.
            }
#pragma warning restore CA1031
        }
        return rewritten;
    }

    /// <summary>
    /// Should this session hand its run to a temporary copy? Only a stamped
    /// <c>uninstall.exe</c> in uninstall mode that is not already the copy, and only on
    /// Windows. <c>setup.exe /Uninstall</c> does not: it is not in the install directory.
    /// (R106)
    /// </summary>
    public static bool ShouldRelaunch(InstallSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return OperatingSystem.IsWindows()
            && ShouldRelaunch(
                session.Mode, session.IsStamped, session.CommandLine.RelaunchedFrom, Environment.ProcessPath);
    }

    /// <summary>The decision of <see cref="ShouldRelaunch(InstallSession)"/>, with its inputs explicit.</summary>
    internal static bool ShouldRelaunch(
        WrapperMode mode, bool stamped, string? relaunchedFrom, string? imagePath)
        => mode == WrapperMode.Uninstall
            && stamped
            && relaunchedFrom is null
            && IsUninstallerImage(imagePath);

    /// <summary>
    /// Copy this image to a private temporary directory, run the copy and wait for it.
    /// Returns the copy's exit code, or <c>null</c> when no copy was started and the
    /// caller must run the uninstall in place. Every decision is reported on
    /// <paramref name="log"/>. (R106)
    /// </summary>
    public static int? TryRunFromTemporaryCopy(
        InstallSession session, IReadOnlyList<string> args, Action<string, bool> log)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(log);

        var image = Environment.ProcessPath;
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(image))
        {
            return null;
        }

        SecureStaging staging;
        try
        {
            staging = SecureStaging.Create(StagingPurpose, log);
        }
        catch (Exception ex) when (
            ex is StagingSecurityException or IOException or UnauthorizedAccessException)
        {
            log($"uninstall: not relaunching from a temporary copy ({ex.Message}); " +
                "uninstalling in place, and this uninstaller is removed at the next restart", true);
            return null;
        }

        FileStream? held = null;
        IReadOnlyList<string>? copyArgs = null;
        Process? copy;
        try
        {
            var copyPath = StageVerifiedCopy(image, staging, out held);
            copyArgs = session.BuildElevationRelaunchArgs(BuildCopyArgs(args, Path.GetFullPath(image)));
            copy = Process.Start(BuildStartInfo(copyPath, copyArgs, session.InstanceLock?.MintRelaunchHandoff()));
            if (copy is null)
            {
                throw new InvalidOperationException($"'{copyPath}' did not start");
            }
            log($"uninstall: handed off to the temporary copy '{copyPath}' (pid {copy.Id}) so the " +
                "install directory can be removed; waiting for it and exiting with its exit code", false);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or StagedFileVerificationException
               or Win32Exception or InvalidOperationException or UsageException)
        {
            // Nothing was started: undo what was staged and let the caller run in place.
            log($"uninstall: not relaunching from a temporary copy ({ex.Message}); " +
                "uninstalling in place, and this uninstaller is removed at the next restart", true);
            held?.Dispose();
            ElevationSecretHandoff.CleanUp(copyArgs, childMayStillBeRunning: false);
            staging.Dispose();
            return null;
        }

        int code;
        using (copy)
        {
            // The deny-write handle is held across Process.Start (R5); once the copy runs,
            // its mapped image cannot be written either, and the handle would only stop
            // cleanup from deleting the file.
            held?.Dispose();
            copy.WaitForExit();
            code = copy.ExitCode;
        }

        log($"uninstall: the temporary copy exited with code {code}", code != 0);
        ElevationSecretHandoff.CleanUp(copyArgs, childMayStillBeRunning: false);
        CleanUpCopyDirectory(staging.Directory, log);
        return code;
    }

    /// <summary>
    /// The copy's argument vector: <paramref name="args"/> with any inbound
    /// <see cref="Switch"/> token dropped, <c>/Uninstall</c> made explicit, and
    /// <see cref="Switch"/> naming <paramref name="originalPath"/> appended. (R106)
    /// </summary>
    internal static List<string> BuildCopyArgs(IReadOnlyList<string> args, string originalPath)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrEmpty(originalPath);

        var result = new List<string>(args.Count + 2);
        var hasUninstall = false;
        foreach (var arg in args)
        {
            if (arg is not null && arg.StartsWith(Switch, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (string.Equals(arg, "/Uninstall", StringComparison.OrdinalIgnoreCase))
            {
                hasUninstall = true;
            }
            result.Add(arg!);
        }

        // The copy is named uninstall.exe and would imply uninstall mode anyway; saying so
        // keeps the mode independent of the file name.
        if (!hasUninstall)
        {
            result.Add("/Uninstall");
        }
        result.Add(Switch + originalPath);
        return result;
    }

    /// <summary>
    /// Copy <paramref name="source"/> into <paramref name="staging"/> as
    /// <c>uninstall.exe</c>, hashing the bytes as they are copied from ONE source handle,
    /// then reopen the copy with <see cref="SecureStaging.OpenVerified"/> — deny-write,
    /// deny-delete, re-hashed from that handle — and hand that handle back. The caller
    /// keeps it open across <c>Process.Start</c>, so the bytes that were verified are the
    /// bytes the loader maps. (R5, R106)
    /// </summary>
    /// <exception cref="StagedFileVerificationException">The copy does not hash to its source.</exception>
    internal static string StageVerifiedCopy(string source, SecureStaging staging, out FileStream held)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentNullException.ThrowIfNull(staging);

        var name = InstallSurvivability.UninstallerFileName;
        var destination = staging.PathFor(name);
        string sha256;
        using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        using (var to = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                to.Write(buffer, 0, read);
            }
            to.Flush(flushToDisk: true);
            sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        held = staging.OpenVerified(name, sha256);
        return destination;
    }

    /// <summary>
    /// Is <paramref name="claimed"/> — the <see cref="Switch"/> value this process was
    /// started with — really the uninstaller this process is a copy of? Returns its full
    /// path when it is a fully-qualified <c>uninstall.exe</c> in a directory other than
    /// this image's, byte-identical to <paramref name="ownImage"/>; <c>null</c> otherwise.
    /// (R106)
    /// </summary>
    /// <remarks>
    /// <para>
    /// The switch is caller-reachable, so what it unlocks is gated here. A verified
    /// original is (1) exempted from the files-in-use gate by FILE IDENTITY — it is the
    /// image of the relaunch parent waiting on this process, which would otherwise block
    /// its own uninstall — and (2) its directory is never touched: only the copy's own
    /// directory receives a file <see cref="SelfDelete"/> cannot delete. Requiring the
    /// claimed file to carry this very image's bytes means the only process a forged
    /// switch can exempt is one running another copy of this same uninstaller.
    /// </para>
    /// </remarks>
    public static string? VerifyOriginal(string? claimed, string? ownImage)
    {
        if (string.IsNullOrEmpty(claimed) || string.IsNullOrEmpty(ownImage))
        {
            return null;
        }

#pragma warning disable CA1031 // Fail closed: anything unreadable is not a verified original.
        try
        {
            if (!Path.IsPathFullyQualified(claimed)
                || !IsUninstallerImage(claimed)
                || !File.Exists(claimed))
            {
                return null;
            }

            var original = Path.GetFullPath(claimed);
            var own = Path.GetFullPath(ownImage);
            if (string.Equals(
                    Path.GetDirectoryName(original),
                    Path.GetDirectoryName(own),
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return HashFile(original).AsSpan().SequenceEqual(HashFile(own)) ? original : null;
        }
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Wire a temporary copy up to its original: verify the <see cref="Switch"/> value
    /// (<see cref="VerifyOriginal"/>), and when it holds, exempt the original's image from
    /// the files-in-use gate and let <see cref="SelfDelete"/> move the original into this
    /// copy's directory. A no-op for every run that is not the copy. (R106)
    /// </summary>
    public static void ConfigureRelaunchedCopy(InstallSession session, Action<string, bool> log)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(log);

        var claimed = session.CommandLine.RelaunchedFrom;
        if (claimed is null || session.Mode != WrapperMode.Uninstall)
        {
            return;
        }

        var own = Environment.ProcessPath;
        var original = VerifyOriginal(claimed, own);
        if (original is null)
        {
            log($"uninstall: '{claimed}' is not the uninstaller this copy was made from; " +
                "ignoring it — the files-in-use check treats it as any other process, and " +
                "nothing is moved out of its directory", true);
            return;
        }

        session.OriginalUninstallerImage = original;
        SelfDelete.SetAsideDirectory = Path.GetDirectoryName(Path.GetFullPath(own!));
        log($"uninstall: running from a temporary copy of '{original}'", false);
    }

    /// <summary>
    /// After the copy has exited: delete it, and schedule whatever cannot be deleted —
    /// this process's own image, renamed in by <see cref="SelfDelete"/> — and then the
    /// directory for deletion at the next restart. Best-effort; never throws. (R106)
    /// </summary>
    internal static void CleanUpCopyDirectory(string directory, Action<string, bool> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

#pragma warning disable CA1031 // Best-effort cleanup of a private temporary directory; never fail the uninstall.
        try
        {
            var remaining = new List<string>();
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var attempts = Path.GetFileName(file).StartsWith(SetAsidePrefix, StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : CopyDeleteAttempts;
                if (!TryDelete(file, attempts))
                {
                    remaining.Add(file);
                }
            }

            if (remaining.Count == 0)
            {
                Directory.Delete(directory);
                return;
            }

            var scheduled = true;
            foreach (var file in remaining)
            {
                scheduled &= SelfDelete.ScheduleDeleteOnReboot(file);
            }
            scheduled &= SelfDelete.ScheduleDeleteOnReboot(directory);
            log(scheduled
                    ? $"uninstall: '{directory}' is removed at the next restart"
                    : $"uninstall: '{directory}' could not be scheduled for removal (that needs " +
                      "administrator rights); it stays in the temporary folder until it is cleaned up",
                false);
        }
        catch (Exception ex)
        {
            log($"uninstall: could not clean up '{directory}' ({ex.Message})", false);
        }
#pragma warning restore CA1031
    }

    /// <summary>True when <paramref name="path"/> names a file called <c>uninstall.exe</c>.</summary>
    internal static bool IsUninstallerImage(string? path) =>
        !string.IsNullOrEmpty(path)
        && string.Equals(
            Path.GetFileName(path),
            InstallSurvivability.UninstallerFileName,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="args"/> with a relative <c>/LOG=path</c> resolved against
    /// <paramref name="baseDirectory"/>, so it means the same file after the current
    /// directory moves and in a copy started elsewhere. Every other token is unchanged.
    /// </summary>
    internal static string[] AbsolutizeLogPath(IReadOnlyList<string> args, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);
        var result = new string[args.Count];
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is not null
                && arg.StartsWith("/LOG=", StringComparison.OrdinalIgnoreCase)
                && arg.Length > 5
                && !Path.IsPathFullyQualified(arg.AsSpan(5)))
            {
                result[i] = "/LOG=" + Path.GetFullPath(arg.Substring(5), baseDirectory);
                continue;
            }
            result[i] = arg!;
        }
        return result;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="directory"/> or lies beneath it.</summary>
    internal static bool IsWithin(string path, string directory)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var d = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return p.Equals(d, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static ProcessStartInfo BuildStartInfo(
        string copyPath, IReadOnlyList<string> copyArgs, string? lockHandoff)
    {
        var psi = new ProcessStartInfo
        {
            FileName = copyPath,
            UseShellExecute = false,
            // The copy shares this process's console when there is one, so a silent run's
            // output still reaches the caller. Without one — an upgrade starts the prior
            // uninstaller with no window — a console-subsystem copy would otherwise open a
            // console window of its own.
            CreateNoWindow = !HasConsole(),
            WorkingDirectory = Environment.SystemDirectory,
        };
        foreach (var arg in copyArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        // Only on the copy's own environment block, never on this process's. (R76)
        if (lockHandoff is not null)
        {
            psi.Environment[SetupInstanceLock.HandoffVariable] = lockHandoff;
        }
        return psi;
    }

    private static bool HasConsole() =>
        OperatingSystem.IsWindows() && GetConsoleWindow() != IntPtr.Zero;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    private static partial IntPtr GetConsoleWindow();

    private static byte[] HashFile(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return SHA256.HashData(stream);
    }

    private static bool TryDelete(string file, int attempts)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                File.Delete(file);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (i + 1 < attempts)
                {
                    Thread.Sleep(CopyDeleteDelay);
                }
            }
        }
        return false;
    }
}
