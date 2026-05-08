namespace SigilBuild.Wrapper.Expressions;

/// <summary>
/// Probe helper for the <c>registry_exists(hive, key, name)</c>
/// expression function.
/// </summary>
internal static class RegistryHelper
{
    // TODO(Task 15): real implementation reading HKLM/HKCU keys via
    // Microsoft.Win32.Registry — wiring lands alongside RegistryWriteStep.
    // Until then this is a deliberate stub returning false so the closed
    // function table stays complete and `registry_exists(...)` parses and
    // evaluates without throwing.
    public static bool Exists(string? hive, string? key, string? name) => false;
}
