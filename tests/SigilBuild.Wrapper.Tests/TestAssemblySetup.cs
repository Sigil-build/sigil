using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using SigilBuild.Wrapper.Core.Localization;
using Xunit;

// P9 (Task 14): SessionLanguage (SigilBuild.Wrapper.Core.Localization) is
// process-wide static state now read from production code paths exercised
// across many of this assembly's test classes that never call
// ResolveSessionLanguage themselves — InstallSession's engine-prose messages
// (LaunchLabel, DowngradeBlockedMessage, engine.removing_previous/newer),
// PrerequisiteRunner's installing-prerequisite message, and StepContext's
// system.language (guarded separately on IsSet).
// In Release the SessionLanguage.Current guard falls back to Lang.En + logs;
// in Debug it throws (by design — see SessionLanguage.Current's remarks).
// Establish the same English default here, once, at assembly load — mirroring
// tests/SigilBuild.Installer.Host.Tests/TestAppBuilder.cs's identical fix for
// the same Task-13-shaped problem — and disable test parallelization so the
// "SessionLanguage" collection's explicit Set/Reset calls (SessionLanguageTests,
// SessionResolutionTests) can never race a different collection's implicit read
// of the assembly default.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SigilBuild.Wrapper.Tests;

internal static class TestAssemblySetup
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Test-assembly bootstrap: establishes the SessionLanguage default once at load, mirroring the Release-mode fallback so Debug test runs behave the same.")]
    internal static void InitializeSessionLanguage() => SessionLanguage.Set(Lang.En);
}
