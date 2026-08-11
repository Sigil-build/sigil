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
            current => IniEditor.Set(current, section, key, value),
            "ini_write", _spec.AllowOutsideInstallDir);

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
    /// <exception cref="ArgumentException">
    /// <paramref name="section"/>, <paramref name="key"/> or <paramref name="value"/>
    /// contains a carriage return or line feed, or begins with <c>[</c>
    /// (register row R32).
    /// </exception>
    public static string Set(string? content, string section, string key, string value)
    {
        // R32: section/key/value are ctx.Resolve-expanded and concatenated
        // verbatim into "key=value", so a value of "9\n[admin]\nenabled=true"
        // wrote arbitrary entries into another section — which matters as soon as
        // the value comes from a wizard field or a registry_read var rather than a
        // literal. Rejected rather than escaped: an INI has no escape for a
        // newline inside a value, and all three are pack-time-authored, so a hard
        // failure surfaces the mistake to the publisher instead of silently
        // mangling it. ConfigFileEditor turns the throw into a step failure with
        // the file left untouched.
        RejectLineInjection("section", section);
        RejectLineInjection("key", key);
        RejectLineInjection("value", value);

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

    /// <summary>
    /// Refuse the three shapes that let an <c>ini_write</c> field write something
    /// other than the one <c>key=value</c> it declares (R32): a carriage return or
    /// line feed, which starts a new INI line, and a leading <c>[</c>, which is
    /// how a section header begins.
    /// </summary>
    private static void RejectLineInjection(string field, string text)
    {
        if (text.Contains('\r', StringComparison.Ordinal) || text.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"ini_write: '{field}' must not contain a carriage return or line feed — it would " +
                $"inject additional lines into the INI file (got '{Describe(text)}')",
                field);
        }

        if (text.Length > 0 && text[0] == '[')
        {
            throw new ArgumentException(
                $"ini_write: '{field}' must not begin with '[' — that is how an INI section header " +
                $"starts (got '{text}')",
                field);
        }
    }

    /// <summary>
    /// Render a rejected value with its line breaks made visible, so the failure
    /// message shows what was wrong instead of splitting itself across lines.
    /// </summary>
    private static string Describe(string text) =>
        text.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

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
