// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;

namespace SigilBuild.Installer.Host.ViewModels;

/// <summary>
/// What the shared Close-applications screen (<c>CloseAppsView</c>) binds to. The
/// install wizard and the uninstall wizard both implement it, so the two show the
/// same screen, strings and busy/failed states. (R103)
/// </summary>
public interface ICloseAppsGate : INotifyPropertyChanged
{
    /// <summary>The applications currently blocking the run, one description each (name + pid).</summary>
    ObservableCollection<string> Blockers { get; }

    /// <summary>True when the last "Close for me" left applications running.</summary>
    bool CloseAppsFailed { get; }

    /// <summary>True when Retry and Close-for-me can be pressed — not while a close is in flight.</summary>
    bool CanActOnBlockers { get; }

    /// <summary>True while "Close for me" is waiting on the Restart Manager.</summary>
    bool IsClosingApps { get; }

    /// <summary>"Retry" — the user closed the apps themselves; re-scan and continue when clear.</summary>
    void RetryBlockers();

    /// <summary>"Close for me" — close the blockers off the UI thread, re-scan, continue when clear.</summary>
    Task CloseBlockingAppsAsync();
}

/// <summary>
/// The off-UI-thread half of "Close for me", shared by both wizards. (R94, R103)
/// </summary>
internal static class CloseAppsWork
{
    /// <summary>
    /// Run <paramref name="close"/> and then <paramref name="scan"/> on a thread-pool
    /// thread, returning what is still running. A graceful <c>RmShutdown</c> blocks for
    /// as long as the Restart Manager waits on the applications, so it must never run
    /// on the dispatcher. The re-scan is a fresh Restart Manager session, so the list
    /// names the processes running now. A throwing close is reported by the re-scan,
    /// never by a crash.
    /// </summary>
    public static Task<IReadOnlyList<string>> CloseThenRescanAsync(Action? close, Func<IReadOnlyList<string>>? scan)
        => Task.Run(() =>
        {
#pragma warning disable CA1031 // A failed close is reported by the re-scan below, never by a crash.
            try
            {
                close?.Invoke();
            }
            catch (Exception)
            {
                // The re-scan decides what the user sees next.
            }
#pragma warning restore CA1031
            return scan?.Invoke() ?? (IReadOnlyList<string>)Array.Empty<string>();
        });
}
