namespace SigilBuild.Wrapper.Engine;

/// <summary>
/// App-level metadata embedded in <c>SIGIL_BLOB_V1</c> for use as install-time
/// template substitutions. Exposed via <c>${app.name}</c>, <c>${app.version}</c>,
/// <c>${app.publisher}</c>, <c>${app.id}</c> etc. in install_steps. Without
/// this, registry_write / shortcut_create / run_program steps that reference
/// app metadata write the literal placeholder text to disk (silent corruption).
/// </summary>
internal sealed record AppMetadata(
    string Id,
    string Name,
    string Version,
    string Publisher,
    string? Description,
    string? Homepage)
{
    public static AppMetadata Empty { get; } =
        new("<unset>", "<unset>", "0.0.0", "<unset>", null, null);
}
