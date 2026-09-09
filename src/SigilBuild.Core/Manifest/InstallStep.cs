using System.Collections.Generic;

namespace SigilBuild.Core.Manifest;

/// <summary>
/// Typed-graph representation of a single install-time step parsed from the
/// manifest's <c>install_steps:</c> / <c>pre_install:</c> / <c>post_install:</c>
/// blocks. Each MUST-tier step type from the Sprint 5a catalog is a sealed
/// nested record. Per-step parameter validation lives in
/// <see cref="SigilBuild.Core.Configuration.ManifestParser"/> rather than the
/// home-rolled JSON Schema validator (whose <c>additionalProperties: true</c>
/// on the schema-level step shape intentionally defers to this typed graph).
/// </summary>
public abstract record InstallStep(string Id, string? When, OnFailure OnFailure)
{
    /// <summary>
    /// True for steps that touch machine-global state (P11: scheduled tasks,
    /// COM registration, firewall rules) and therefore MUST run in
    /// <see cref="InstallScope.Machine"/>. Defaults to false for every existing
    /// step type; T11.1-T11.3 override this to true on their record types. The
    /// pack-time guard in <c>SigilBuild.Core.Configuration.MachineScopeGuard</c>
    /// emits SIG0310 for any such step when the manifest's resolved scope isn't
    /// <see cref="InstallScope.Machine"/> (SIG0310 — see
    /// <see cref="SigilBuild.Core.Diagnostics.DiagnosticCodes.SystemStepRequiresMachineScope"/>).
    /// </summary>
    public virtual bool RequiresMachineScope => false;

    /// <summary>
    /// Manifest <c>allow_outside_install_dir</c> (register row R16). Opts a single
    /// step out of the destination-containment rule, for the installers that
    /// legitimately write outside the installed application — a machine-wide
    /// config under <c>%ProgramData%</c> is the common one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declared on the base record rather than on each destination step: it is
    /// parsed once, in the same place as <c>when</c> and <c>on_failure</c>, and
    /// travels the blob wire as one field. Only the steps that actually write
    /// somewhere consult it — <c>file_copy</c>, <c>directory_create</c>,
    /// <c>file_delete</c>, <c>directory_delete</c>, <c>http_download</c>,
    /// <c>ini_write</c>, <c>json_edit</c>, <c>xml_edit</c> (eight) — and only those
    /// accept the key. On every other step type it is both reported as an
    /// unrecognized field (SIG0231) and left unapplied, so that diagnostic's
    /// "ignored" wording is literally true. This list must match
    /// <c>ManifestParser.ContainedDestinationStepTypes</c>.
    /// </para>
    /// <para>
    /// It does NOT relax the privileged-target rule on <c>service_install</c>,
    /// <c>scheduled_task_create</c>, <c>com_register</c> or <c>firewall_rule</c>
    /// (rows R3/R9) — those targets run with SYSTEM authority and have no opt-out
    /// — and it does not suppress the unresolved-token failure, which is a
    /// manifest typo under any policy.
    /// </para>
    /// </remarks>
    public bool AllowOutsideInstallDir { get; init; }

    public sealed record FileCopy(
        string Id,
        string From,
        string To,
        bool Overwrite,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure);

    public sealed record DirectoryCreate(
        string Id,
        string Path,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure);

    public sealed record FileDelete(
        string Id,
        string Path,
        string IfMissing,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure);

    public sealed record DirectoryDelete(
        string Id,
        string Path,
        bool Recursive,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure);

    public sealed record RegistryWrite(
        string Id,
        string Hive,
        string Key,
        string Name,
        string Type,
        object? Value,
        string View,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure);

    public sealed record RegistryDeleteValue(
        string Id,
        string Hive,
        string Key,
        string Name,
        string View,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure);

    public sealed record RegistryDeleteKey(
        string Id,
        string Hive,
        string Key,
        bool Recursive,
        string View,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure);

    public sealed record ShortcutCreate(
        string Id,
        string Target,
        string Location,
        string Name,
        IReadOnlyList<string>? Args,
        string? WorkingDir,
        string? Icon,
        string? Description,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure);

    public sealed record EnvSet(
        string Id,
        string Name,
        string Value,
        string Scope,
        string Action,
        string Separator,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure);

    public sealed record RunProgram(
        string Id,
        string Program,
        IReadOnlyList<string>? Args,
        bool Wait,
        string? Cwd,
        IReadOnlyList<int>? ExpectedExitCodes,
        int? TimeoutSeconds,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure);

    /// <summary>
    /// Install-time HTTP download (P4, gap G5). Streams <see cref="Url"/> (HTTPS
    /// only) to <see cref="Dest"/>, verifies the SHA-256, and journals the write so
    /// a rollback deletes the downloaded file. <see cref="Sha256"/> is REQUIRED —
    /// the packer refuses to pack a download without it. Transient failures
    /// (network / timeout / 5xx) are retried up to <see cref="Retries"/> times with
    /// backoff; a checksum mismatch is not transient and fails immediately.
    /// </summary>
    public sealed record HttpDownload(
        string Id,
        string Url,
        string Dest,
        string Sha256,
        int? TimeoutSeconds,
        int? Retries,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure);

    /// <summary>
    /// Config-file edit (P8, gap G9): set <see cref="Key"/> under <see cref="Section"/>
    /// in an INI file, preserving all unrelated lines. Journaled — the whole prior
    /// file (or its absence) is snapshotted for byte-exact rollback.
    /// </summary>
    public sealed record IniWrite(
        string Id,
        string Path,
        string Section,
        string Key,
        string Value,
        bool CreateIfMissing,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure);

    /// <summary>
    /// Config-file edit (P8, gap G9): set the value at an RFC 6901 JSON
    /// <see cref="JsonPointer"/> in a JSON file (System.Text.Json DOM), creating
    /// intermediate objects as needed. Journaled for byte-exact rollback.
    /// </summary>
    /// <param name="ValueType">
    /// How <c>Value</c> is interpreted once every <c>${…}</c> / <c>{…}</c>
    /// substitution has happened — see <see cref="Manifest.JsonValueType"/>.
    /// Defaults to <see cref="Manifest.JsonValueType.Text"/> (register row R35),
    /// which is also what an omitted <c>value_type:</c> in the manifest means.
    /// </param>
    public sealed record JsonEdit(
        string Id,
        string Path,
        string JsonPointer,
        string Value,
        bool CreateIfMissing,
        string? When,
        OnFailure OnFailure,
        JsonValueType ValueType = JsonValueType.Text)
        : InstallStep(Id, When, OnFailure);

    /// <summary>
    /// Config-file edit (P8, gap G9): set the node (or <see cref="Attribute"/>)
    /// selected by <see cref="Xpath"/> in an XML file. A simple absolute element
    /// path is created when missing. Journaled for byte-exact rollback.
    /// </summary>
    public sealed record XmlEdit(
        string Id,
        string Path,
        string Xpath,
        string? Attribute,
        string Value,
        bool CreateIfMissing,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure);

    /// <summary>
    /// SHOULD-tier (post-MVP per the action catalog, promoted MUST-tier when
    /// the wrapper grew real installer support): create a Windows service
    /// pointing at <see cref="BinaryPath"/>. Unlike a <c>run_program sc.exe
    /// create</c> shellout, this step records a rollback that stops + deletes
    /// the service so <c>setup.exe /Uninstall</c> properly tears it down.
    /// </summary>
    public sealed record ServiceInstall(
        string Id,
        string Name,
        string BinaryPath,
        string DisplayName,
        string? Description,
        string StartType,        // auto | demand | disabled
        string ServiceAccount,   // LocalSystem (default) | NetworkService | LocalService
        bool StartAfterInstall,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure);

    /// <summary>
    /// P11 (T11.1), first of three machine-scope-only "system steps": creates a
    /// Windows Scheduled Task via <c>schtasks.exe /Create</c>, running as
    /// <c>SYSTEM</c> (<c>/RU SYSTEM</c>) — which is exactly why
    /// <see cref="RequiresMachineScope"/> is overridden to <c>true</c> here (see
    /// <see cref="SigilBuild.Core.Configuration.MachineScopeGuard"/> / SIG0310).
    /// Journals a <c>RollbackRecord.DeleteScheduledTask</c>
    /// (task name only) BEFORE the create so a mid-install crash and
    /// <c>setup.exe /Uninstall</c> both unwind the task.
    /// </summary>
    public sealed record ScheduledTaskCreate(
        string Id,
        string Name,
        string Program,
        string? Arguments,
        string Trigger,          // logon | daily | onstart
        string RunLevel,         // limited (default) | highest
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure)
    {
        public override bool RequiresMachineScope => true;
    }

    /// <summary>
    /// P11 (T11.2), second of three machine-scope-only "system steps" and the
    /// one AOT-risk step in P11: self-registers a COM DLL by loading it and
    /// invoking its exported <c>HRESULT DllRegisterServer(void)</c> through a
    /// C# unmanaged function pointer. <c>DllRegisterServer</c> writes
    /// machine-global registration (<c>HKLM\Software\Classes</c> / <c>HKCR\CLSID</c>),
    /// so <see cref="RequiresMachineScope"/> is overridden to <c>true</c> (see
    /// <see cref="SigilBuild.Core.Configuration.MachineScopeGuard"/> / SIG0310).
    /// Journals a <c>RollbackRecord.UnregisterCom</c> (DLL path only) BEFORE the
    /// register so a mid-install crash and <c>setup.exe /Uninstall</c> both call
    /// <c>DllUnregisterServer</c> — mirrors <see cref="ServiceInstall"/>'s
    /// <c>RemoveService</c> pattern.
    /// </summary>
    public sealed record ComRegister(
        string Id,
        string Path,
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure)
    {
        public override bool RequiresMachineScope => true;
    }

    /// <summary>
    /// P11 (T11.3), third and last of three machine-scope-only "system steps":
    /// creates a Windows Defender Firewall rule via
    /// <c>netsh advfirewall firewall add rule</c>. Firewall rules are
    /// machine-global (there is no per-user firewall policy store), so
    /// <see cref="RequiresMachineScope"/> is overridden to <c>true</c> (see
    /// <see cref="SigilBuild.Core.Configuration.MachineScopeGuard"/> / SIG0310).
    /// Journals a <c>RollbackRecord.DeleteFirewallRule</c> (rule name only)
    /// BEFORE the add so a mid-install crash and <c>setup.exe /Uninstall</c>
    /// both unwind the rule — mirrors <see cref="ServiceInstall"/>'s
    /// <c>RemoveService</c> pattern.
    /// </summary>
    /// <remarks>
    /// <see cref="Port"/> and <see cref="Protocol"/> are both optional, but
    /// netsh's <c>localport=</c> only makes sense alongside a <c>protocol=</c>
    /// (TCP/UDP); the parser defaults <see cref="Protocol"/> to <c>tcp</c>
    /// when <see cref="Port"/> is set and no protocol was given explicitly, so
    /// this typed field is only <c>null</c> when the manifest author left both
    /// unset (a whole-program rule with no port restriction).
    /// </remarks>
    public sealed record FirewallRule(
        string Id,
        string Name,
        string Direction,        // in | out
        string Action,           // allow | block
        string? Program,
        int? Port,
        string? Protocol,        // tcp | udp; parser-defaulted to "tcp" when Port is set
        string? When,
        OnFailure OnFailure)
        : InstallStep(Id, When, OnFailure)
    {
        public override bool RequiresMachineScope => true;
    }
}

/// <summary>
/// What the step engine should do when a step's primary action fails.
/// <list type="bullet">
///   <item><description><c>Rollback</c> — undo the journal up to (and including) this step.</description></item>
///   <item><description><c>Continue</c> — emit a warning and proceed with the next step.</description></item>
///   <item><description><c>Fail</c> — abort the install (default).</description></item>
/// </list>
/// </summary>
public enum OnFailure
{
    Rollback,
    Continue,
    Fail,
}

/// <summary>
/// How <c>json_edit</c> interprets its <c>value:</c> after substitution
/// (manifest <c>value_type:</c>, register row R35).
/// </summary>
/// <remarks>
/// <para>
/// The step used to run every resolved value through <c>JsonNode.Parse</c> and keep
/// whatever came back, documented as intentional literal inference. That is fine for
/// a literal the publisher typed and wrong for anything else: a value sourced from a
/// wizard field, a <c>registry_read</c> var or a <c>/P&lt;name&gt;=</c> argument could
/// arrive as <c>{"admin":true}</c>, <c>[1,2]</c>, <c>true</c> or <c>null</c> and be
/// written into the application's own configuration as structure where the manifest
/// author wrote — and reviewed — a string. Encoding was never the problem; the
/// resulting JSON is always well formed. The problem is that the shape of the written
/// node is chosen by whoever supplies the value.
/// </para>
/// <para>
/// <see cref="String"/> is therefore the default and today's behaviour is the opt-in.
/// It is not a compatibility break in the direction that matters: a manifest that
/// meant <c>true</c> and now gets <c>"true"</c> is a visible, correctable defect,
/// where the reverse is a silent type-confusion channel.
/// </para>
/// </remarks>
public enum JsonValueType
{
    /// <summary>
    /// Manifest <c>value_type: string</c> — write the resolved value as a JSON
    /// string, always and exactly. The default. (Spelled <c>Text</c> rather than
    /// <c>String</c> because CA1720 forbids a type name as an identifier; the
    /// manifest spelling is unaffected.)
    /// </summary>
    Text,

    /// <summary>
    /// Parse the resolved value as JSON and write the resulting node — number,
    /// boolean, <c>null</c>, array or object. A value that is not valid JSON fails
    /// the step rather than silently degrading to a string: with the intent declared,
    /// a parse failure is a manifest error worth surfacing.
    /// </summary>
    Json,
}
