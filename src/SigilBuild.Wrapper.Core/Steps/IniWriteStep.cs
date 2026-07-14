namespace SigilBuild.Wrapper.Steps;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;

/// <summary>
/// <c>ini_write</c> step (P8, gap G9): set <c>key=value</c> under a section in an
/// INI file, preserving all unrelated lines (comments, blank lines, other keys and
/// sections). Hand-rolled and AOT-safe. Journaled for byte-exact rollback.
/// </summary>
internal sealed class IniWriteStep : IStep
{
    private readonly InstallStep.IniWrite _spec;

    public IniWriteStep(InstallStep.IniWrite spec) => _spec = spec;

    public Task<StepResult> RunAsync(StepContext ctx, RollbackJournal journal, CancellationToken ct)
    {
        var section = ctx.Resolve(_spec.Section);
        var key = ctx.Resolve(_spec.Key);
        var value = ctx.Resolve(_spec.Value);

        var result = ConfigFileEditor.Edit(
            ctx, journal, _spec.Path, _spec.CreateIfMissing,
            current => IniEditor.Set(current, section, key, value));

        return Task.FromResult(result);
    }
}

/// <summary>
/// Minimal, deterministic, AOT-safe INI reader/modifier/writer. Sets one
/// <c>key=value</c> under <c>section</c> (empty section = keys before the first
/// <c>[header]</c>), preserving every other line verbatim.
/// </summary>
internal static class IniEditor
{
    public static string Set(string? content, string section, string key, string value)
    {
        var newline = content is not null && content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\r\n";
        if (content is not null && !content.Contains("\r\n", StringComparison.Ordinal) && content.Contains('\n', StringComparison.Ordinal))
        {
            newline = "\n";
        }

        var lines = (content is null || content.Length == 0)
            ? new List<string>()
            : new List<string>(content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));

        // Find the section's line region [regionStart, regionEnd).
        int regionStart, regionEnd, headerIndex = -1;
        if (section.Length == 0)
        {
            regionStart = 0;
            regionEnd = FindNextHeader(lines, 0);
        }
        else
        {
            for (var i = 0; i < lines.Count; i++)
            {
                if (IsSectionHeader(lines[i], out var name) &&
                    string.Equals(name, section, StringComparison.OrdinalIgnoreCase))
                {
                    headerIndex = i;
                    break;
                }
            }

            if (headerIndex < 0)
            {
                // Section absent → append it (with a blank separator when needed).
                if (lines.Count > 0 && lines[^1].Trim().Length != 0)
                {
                    lines.Add(string.Empty);
                }
                lines.Add("[" + section + "]");
                lines.Add(key + "=" + value);
                return string.Join(newline, lines);
            }

            regionStart = headerIndex + 1;
            regionEnd = FindNextHeader(lines, regionStart);
        }

        // Replace an existing key in the region.
        for (var i = regionStart; i < regionEnd; i++)
        {
            if (IsKeyLine(lines[i], key))
            {
                lines[i] = key + "=" + value;
                return string.Join(newline, lines);
            }
        }

        // Key absent → insert after the last non-blank line of the region.
        var insertAt = regionEnd;
        while (insertAt > regionStart && lines[insertAt - 1].Trim().Length == 0)
        {
            insertAt--;
        }
        lines.Insert(insertAt, key + "=" + value);
        return string.Join(newline, lines);
    }

    private static int FindNextHeader(List<string> lines, int from)
    {
        for (var i = from; i < lines.Count; i++)
        {
            if (IsSectionHeader(lines[i], out _))
            {
                return i;
            }
        }
        return lines.Count;
    }

    private static bool IsSectionHeader(string line, out string name)
    {
        var t = line.Trim();
        if (t.Length >= 2 && t[0] == '[' && t[^1] == ']')
        {
            name = t.Substring(1, t.Length - 2).Trim();
            return true;
        }
        name = string.Empty;
        return false;
    }

    private static bool IsKeyLine(string line, string key)
    {
        var t = line.TrimStart();
        if (t.Length == 0 || t[0] == ';' || t[0] == '#')
        {
            return false;
        }
        var eq = t.IndexOf('=', StringComparison.Ordinal);
        if (eq < 0)
        {
            return false;
        }
        return string.Equals(t.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase);
    }
}
