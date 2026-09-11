namespace SigilBuild.Wrapper.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using SigilBuild.Wrapper.Cli;

/// <summary>
/// Keeps <see cref="SigilBuild.Core.Manifest.ParameterType.Secret"/>
/// parameter values off the elevated (UAC) relaunch command line. (R18)
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Elevation.RelaunchElevatedAndWait"/> re-emits the original argv to the
/// elevated child, so an unfiltered <c>/P&lt;secret&gt;=&lt;value&gt;</c> token appears
/// verbatim in a second process's command line — which Sysmon, EDR agents, WMI
/// <c>Win32_Process</c> pollers and the plain Task Manager column all record. The
/// value is redacted from Sigil's own logs and journal, so the relaunch is the one
/// channel that would publish it anyway.
/// </para>
/// <para>
/// <b>Mechanism.</b> The un-elevated parent writes the secret name/value pairs to a
/// DPAPI-protected envelope in <c>%TEMP%</c>, strips the secret tokens from the
/// relaunch vector, and appends a single <c>/SecretHandoff=&lt;path&gt;</c> switch.
/// The elevated child's <see cref="CommandLineParser"/> consumes that switch before
/// parameter binding, decrypts the envelope, deletes it, and binds the pairs exactly
/// as if they had arrived as <c>/P</c> tokens — so every existing redaction
/// (<see cref="ParsedCommandLine.AuditSafeRendering"/>, <c>StepContext.Redact</c>,
/// <c>UninstallStateStore</c>) keeps working unchanged.
/// </para>
/// <para>
/// <b>Why machine scope.</b> <c>CRYPTPROTECT_LOCAL_MACHINE</c> rather than the
/// default user scope: UAC may elevate as a <em>different</em> administrator account
/// (the "Enter an administrator password" prompt on a standard-user desktop), and a
/// user-scope blob is then undecryptable by the child. Machine scope means any local
/// process could unprotect the blob, so the compensating controls are: a restrictive
/// DACL applied <em>at create time</em> (current user + <c>BUILTIN\Administrators</c>
/// only, inheritance discarded), <c>FileShare.None</c> while writing,
/// delete-after-single-read, and a best-effort parent-side delete if the child never
/// got that far. That is strictly better than the previous state, where the value was
/// published to every process-creation auditor on the box.
/// Handle inheritance is not an option: <c>ShellExecuteExW</c> with the
/// <c>runas</c> verb does not inherit handles across the elevation boundary.
/// </para>
/// <para>
/// <b>Envelope format</b> (versioned, binary, little-endian — deliberately not JSON:
/// Wrapper.Core's source-generated contexts are a lockstep surface and this blob has
/// exactly one producer and one consumer):
/// <c>int32 version</c>, <c>int32 count</c>, then per pair
/// <c>int32 nameByteLen, utf8 name, int32 valueByteLen, utf8 value</c>. The whole
/// buffer goes through <c>CryptProtectData</c>.
/// </para>
/// <para>
/// All interop is source-generated <c>[LibraryImport]</c> (Native-AOT safe), matching
/// <see cref="Elevation"/>: <c>DATA_BLOB</c> is fully blittable and the reserved /
/// descriptor pointers are passed as <see cref="IntPtr"/> nulls, so the generator
/// needs no runtime marshaller. No reflection, no new package reference.
/// </para>
/// </remarks>
internal static partial class ElevationSecretHandoff
{
    /// <summary>
    /// The reserved switch, including its <c>=</c>. Never documented as user-facing
    /// input: it is the machine-to-machine half of the elevated relaunch.
    /// </summary>
    internal const string Switch = "/SecretHandoff=";

    /// <summary>Envelope layout version. Bump only alongside a reader change.</summary>
    private const int EnvelopeVersion = 1;

    /// <summary>
    /// Sanity ceiling for a decrypted envelope. Parameter values are license keys and
    /// passwords, not payloads; a larger blob is a corrupt or hostile file, not ours.
    /// </summary>
    private const int MaxEnvelopeBytes = 64 * 1024;

    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;
    private const uint CRYPTPROTECT_LOCAL_MACHINE = 0x4;
    private const uint ProtectFlags = CRYPTPROTECT_UI_FORBIDDEN | CRYPTPROTECT_LOCAL_MACHINE;

    /// <summary>
    /// The argument vector to hand <see cref="Elevation.RelaunchElevatedAndWait"/>:
    /// <paramref name="args"/> with every <c>/P&lt;name&gt;=&lt;value&gt;</c> token
    /// whose name is in <see cref="ParsedCommandLine.SecretKeys"/> removed, every
    /// <em>inbound</em> <see cref="Switch"/> token removed, and — when this run
    /// actually carries a secret value — one freshly written <see cref="Switch"/>
    /// token appended. Returns <paramref name="args"/> itself, uncopied, when none of
    /// that applies, which is every run with no secret and no inbound switch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An inbound <see cref="Switch"/> token is always dropped, on every path.</b>
    /// The switch is caller-reachable — nothing stops a script from typing it — and
    /// <see cref="CommandLineParser"/> consumes and <em>deletes</em> the envelope it
    /// names while parsing this process's own argv. Forwarding that spent path to the
    /// elevated child would hand the child a switch pointing at a file that no longer
    /// exists, which its parser correctly refuses: a caller-reachable way to turn a
    /// legitimate per-machine install into exit 64. The parent re-writes its own
    /// envelope from the already-bound values, so a forwarded token could never be
    /// the right one anyway.
    /// </para>
    /// </remarks>
    /// <exception cref="UsageException">
    /// The envelope could not be protected or written. Fail-closed on purpose: the
    /// alternative is relaunching with the plaintext value on the command line, which
    /// is the defect this exists to remove. The message names the mechanism, never a
    /// value.
    /// </exception>
    internal static IReadOnlyList<string> PrepareRelaunchArgs(
        IReadOnlyList<string> args, ParsedCommandLine parsed)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(parsed);

        // Elevation itself is Windows-only (both entry points gate the relaunch on
        // OperatingSystem.IsWindows()), so off Windows this is never reached with a
        // secret in hand — and DPAPI does not exist to protect one. The inbound-switch
        // scrub still applies: it is about the vector's hygiene, not about crypto.
        if (!OperatingSystem.IsWindows())
        {
            return Rewrite(args, secretNames: null, handoffPath: null);
        }

        return PrepareRelaunchArgsWindows(args, parsed);
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> PrepareRelaunchArgsWindows(
        IReadOnlyList<string> args, ParsedCommandLine parsed)
    {
        if (parsed.SecretKeys.Count == 0)
        {
            return Rewrite(args, secretNames: null, handoffPath: null);
        }

        var secretNames = new HashSet<string>(parsed.SecretKeys, StringComparer.OrdinalIgnoreCase);

        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var kv in parsed.Values)
        {
            if (secretNames.Contains(kv.Key))
            {
                pairs.Add(kv);
            }
        }

        // Declared secret parameters, none of them actually supplied on this run:
        // nothing to hide, so write no envelope (but still scrub the vector).
        if (pairs.Count == 0)
        {
            return Rewrite(args, secretNames: null, handoffPath: null);
        }

        return Rewrite(args, secretNames, WriteEnvelope(pairs));
    }

    /// <summary>
    /// The one place the relaunch vector is rewritten: drop every token
    /// <see cref="IsDropped"/> rejects, then append our own <see cref="Switch"/> token
    /// if <paramref name="handoffPath"/> is non-null. Returns
    /// <paramref name="args"/> itself when there is nothing to drop and nothing to
    /// append, so the no-secret case stays the identity it has always been.
    /// </summary>
    private static IReadOnlyList<string> Rewrite(
        IReadOnlyList<string> args, HashSet<string>? secretNames, string? handoffPath)
    {
        var dropped = 0;
        foreach (var arg in args)
        {
            if (IsDropped(arg, secretNames))
            {
                dropped++;
            }
        }

        if (dropped == 0 && handoffPath is null)
        {
            return args;
        }

        var kept = new List<string>(args.Count - dropped + (handoffPath is null ? 0 : 1));
        foreach (var arg in args)
        {
            if (!IsDropped(arg, secretNames))
            {
                kept.Add(arg);
            }
        }

        if (handoffPath is not null)
        {
            kept.Add(Switch + handoffPath);
        }

        return kept;
    }

    /// <summary>
    /// Tokens that must not reach the elevated child: an inbound
    /// <see cref="Switch"/> (always — see <see cref="PrepareRelaunchArgs"/>'s remarks)
    /// and, when <paramref name="secretNames"/> is supplied, a <c>/P</c> token binding
    /// one of those names.
    /// </summary>
    private static bool IsDropped(string arg, HashSet<string>? secretNames) =>
        IsHandoffToken(arg)
        || (secretNames is not null && IsSecretParameterToken(arg, secretNames));

    /// <summary>True when <paramref name="arg"/> is a <see cref="Switch"/> token.</summary>
    private static bool IsHandoffToken(string arg) =>
        !string.IsNullOrEmpty(arg) && arg.StartsWith(Switch, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Decrypt the envelope at <paramref name="path"/>, delete it, and return its
    /// name→value pairs (case-insensitive keys). Returns <c>null</c> when the file is
    /// missing, oversized, not a DPAPI blob, or not a well-formed envelope. Nothing
    /// about the failure — and certainly no value — is logged here: the caller turns
    /// the <c>null</c> into a generic <see cref="UsageException"/>.
    /// </summary>
    /// <remarks>
    /// The read is destructive by design (single-use envelope), and the file is
    /// deleted whether or not it decrypted: a blob that failed once will not decrypt
    /// on a retry either, and leaving it behind only widens the window.
    /// </remarks>
    internal static Dictionary<string, string>? TryConsumeHandoff(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return ConsumeWindows(path);
    }

    /// <summary>
    /// Best-effort parent-side cleanup: delete the envelope named by any
    /// <see cref="Switch"/> token in <paramref name="relaunchArgs"/> if it is still
    /// there. Normally a no-op — the child deletes the file as it reads it — but a
    /// declined UAC prompt, or a child that died before parsing, would otherwise leave
    /// the envelope in <c>%TEMP%</c> until reboot.
    /// </summary>
    /// <param name="relaunchArgs">
    /// The vector <see cref="PrepareRelaunchArgs"/> produced.
    /// </param>
    /// <param name="childMayStillBeRunning">
    /// <c>true</c> when the relaunch may have created a child process this parent did
    /// <em>not</em> see exit —
    /// <see cref="Elevation.RelaunchElevatedAndWait(IReadOnlyList{string}, out bool, int)"/>
    /// reports this. Cleanup is then <b>skipped entirely</b>: deleting the envelope
    /// while the elevated child is still starting up would race it to the file and
    /// fail the very install the handoff exists to enable. An envelope leaked into
    /// <c>%TEMP%</c> is the strictly better outcome — it is DPAPI-protected, ACL'd to
    /// this user and the administrators group, and single-use.
    /// </param>
    /// <remarks>
    /// The gate is a required parameter rather than an optional one on purpose: a call
    /// site that forgets it would silently reintroduce the race, so the compiler asks.
    /// </remarks>
    internal static void CleanUp(IReadOnlyList<string>? relaunchArgs, bool childMayStillBeRunning)
    {
        if (relaunchArgs is null || childMayStillBeRunning)
        {
            return;
        }

        foreach (var arg in relaunchArgs)
        {
            if (IsHandoffToken(arg))
            {
                TryDelete(arg.Substring(Switch.Length));
            }
        }
    }

    /// <summary>
    /// True when <paramref name="arg"/> is a <c>/P&lt;name&gt;=…</c> token whose name
    /// is one of <paramref name="secretNames"/>. Mirrors
    /// <c>CommandLineParser.ParsePValue</c>'s tokenization: <c>/P</c> is
    /// case-insensitive, the name ends at the first <c>=</c>, and the name is matched
    /// case-insensitively against the schema's canonical spelling. A
    /// <c>/Poption.&lt;name&gt;=…</c> token can never match — an option is not a
    /// declared parameter, so its name is never in <see cref="ParsedCommandLine.SecretKeys"/>.
    /// </summary>
    private static bool IsSecretParameterToken(string arg, HashSet<string> secretNames)
    {
        if (string.IsNullOrEmpty(arg) || arg.Length < 4 || arg[0] != '/' || (arg[1] is not ('P' or 'p')))
        {
            return false;
        }

        var eq = arg.IndexOf('=', 2);
        if (eq <= 2)
        {
            return false;
        }

        return secretNames.Contains(arg.Substring(2, eq - 2));
    }

    // ── Envelope I/O ──────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private static string WriteEnvelope(List<KeyValuePair<string, string>> pairs)
    {
        var plaintext = Serialize(pairs);
        byte[]? envelope;
        try
        {
            envelope = Protect(plaintext);
        }
        finally
        {
            // The managed plaintext is the one copy of the secret this method owns.
            Array.Clear(plaintext);
        }

        if (envelope is null)
        {
            throw Unwritable(null);
        }

        var path = Path.Combine(
            Path.GetTempPath(), "sigil-elevate-" + Guid.NewGuid().ToString("N") + ".dpapi");

        try
        {
            // FileSystemAclExtensions.Create — creates the file WITH the DACL already
            // applied, the same reason StateDirectorySecurity.CreateHardened uses the
            // directory form: create-then-SetAccessControl leaves a window in which
            // %TEMP%'s inherited ACEs are live over a file that already has content.
            // FileMode.CreateNew — a Guid name never collides, and refusing rather
            // than truncating means a pre-planted file is an error, not a target.
            using var stream = new FileInfo(path).Create(
                FileMode.CreateNew,
                FileSystemRights.WriteData,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.None,
                EnvelopeSecurity());
            stream.Write(envelope, 0, envelope.Length);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException or PlatformNotSupportedException)
        {
            TryDelete(path);
            throw Unwritable(ex);
        }

        return path;
    }

    [SupportedOSPlatform("windows")]
    private static Dictionary<string, string>? ConsumeWindows(string path)
    {
        byte[]? envelope = null;
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 0 && info.Length <= MaxEnvelopeBytes)
            {
                envelope = File.ReadAllBytes(path);
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException)
        {
            envelope = null;
        }

        // Single-use: gone whether or not it decrypts (see the remarks above).
        TryDelete(path);

        if (envelope is null)
        {
            return null;
        }

        var plaintext = Unprotect(envelope);
        if (plaintext is null)
        {
            return null;
        }

        try
        {
            return Deserialize(plaintext);
        }
        finally
        {
            Array.Clear(plaintext);
        }
    }

    /// <summary>
    /// The envelope's create-time DACL: this user and <c>BUILTIN\Administrators</c>,
    /// inheritance discarded. <c>BUILTIN\Administrators</c> is the load-bearing ACE —
    /// the elevated child may be a different admin account than the parent, which is
    /// the same reason the blob is machine-scoped.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static FileSecurity EnvelopeSecurity()
    {
        var security = new FileSecurity();

        // isProtected: true, preserveInheritance: false — %TEMP% is per-user already,
        // but a redirected or pre-existing TEMP may grant more, and none of it should
        // survive onto a file holding a secret.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is SecurityIdentifier user)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                user, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }

    private static UsageException Unwritable(Exception? inner)
    {
        const string Message =
            "the elevated relaunch secret handoff could not be written — re-run the installer";
        return inner is null ? new UsageException(Message) : new UsageException(Message, inner);
    }

    private static void TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException)
        {
            // Best-effort by contract — an undeletable envelope must not fail a run.
        }
    }

    // ── Envelope serialization ────────────────────────────────────────────────

    private static byte[] Serialize(List<KeyValuePair<string, string>> pairs)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            // BinaryWriter writes int32 little-endian; lengths are explicit int32s
            // rather than BinaryWriter's 7-bit-encoded string prefix so the layout is
            // the documented one and not an implementation detail of BCL types.
            writer.Write(EnvelopeVersion);
            writer.Write(pairs.Count);
            foreach (var pair in pairs)
            {
                var name = Encoding.UTF8.GetBytes(pair.Key);
                writer.Write(name.Length);
                writer.Write(name);

                var value = Encoding.UTF8.GetBytes(pair.Value);
                writer.Write(value.Length);
                writer.Write(value);
            }
        }

        return buffer.ToArray();
    }

    private static Dictionary<string, string>? Deserialize(byte[] plaintext)
    {
        try
        {
            using var buffer = new MemoryStream(plaintext, writable: false);
            using var reader = new BinaryReader(buffer, Encoding.UTF8, leaveOpen: true);

            if (reader.ReadInt32() != EnvelopeVersion)
            {
                return null;
            }

            var count = reader.ReadInt32();
            if (count < 0 || count > 1024)
            {
                return null;
            }

            var result = new Dictionary<string, string>(count, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < count; i++)
            {
                var name = ReadString(reader, plaintext.Length);
                var value = ReadString(reader, plaintext.Length);
                if (name is null || value is null || name.Length == 0)
                {
                    return null;
                }
                result[name] = value;
            }

            return result;
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private static string? ReadString(BinaryReader reader, int ceiling)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > ceiling)
        {
            return null;
        }

        var bytes = reader.ReadBytes(length);
        return bytes.Length != length ? null : Encoding.UTF8.GetString(bytes);
    }

    // ── DPAPI interop ─────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private static unsafe byte[]? Protect(byte[] plaintext)
    {
        fixed (byte* p = plaintext)
        {
            var input = new DATA_BLOB { cbData = (uint)plaintext.Length, pbData = (IntPtr)p };
            if (!CryptProtectData(
                    in input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    ProtectFlags, out var output))
            {
                return null;
            }

            return CopyAndFree(ref output, scrub: false);
        }
    }

    [SupportedOSPlatform("windows")]
    private static unsafe byte[]? Unprotect(byte[] envelope)
    {
        fixed (byte* p = envelope)
        {
            var input = new DATA_BLOB { cbData = (uint)envelope.Length, pbData = (IntPtr)p };
            if (!CryptUnprotectData(
                    in input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out var output))
            {
                return null;
            }

            // scrub: the native buffer holds the decrypted secret, so zero it before
            // handing the page back to the heap.
            var plaintext = CopyAndFree(ref output, scrub: true);
            return plaintext is not null && plaintext.Length <= MaxEnvelopeBytes ? plaintext : null;
        }
    }

    /// <summary>
    /// Copy a <c>DATA_BLOB</c> DPAPI allocated for us into managed memory and
    /// <c>LocalFree</c> it. Returns <c>null</c> for an implausibly large blob rather
    /// than allocating on a hostile length.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static unsafe byte[]? CopyAndFree(ref DATA_BLOB blob, bool scrub)
    {
        try
        {
            if (blob.pbData == IntPtr.Zero || blob.cbData > MaxEnvelopeBytes * 4)
            {
                return null;
            }

            var result = new byte[blob.cbData];
            Marshal.Copy(blob.pbData, result, 0, (int)blob.cbData);
            return result;
        }
        finally
        {
            if (blob.pbData != IntPtr.Zero)
            {
                if (scrub && blob.cbData <= MaxEnvelopeBytes * 4)
                {
                    new Span<byte>((void*)blob.pbData, (int)blob.cbData).Clear();
                }
                _ = LocalFree(blob.pbData);
            }
        }
    }

    // Fully blittable — the LibraryImport source generator needs no runtime
    // marshaller for it (Native-AOT clean), same contract as Elevation's
    // SHELLEXECUTEINFOW.
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public uint cbData;
        public IntPtr pbData;
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(
        in DATA_BLOB pDataIn,
        IntPtr szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DATA_BLOB pDataOut);

    [SupportedOSPlatform("windows")]
    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(
        in DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DATA_BLOB pDataOut);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr hMem);
}
