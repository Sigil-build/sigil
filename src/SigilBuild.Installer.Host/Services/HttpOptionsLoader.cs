using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Wrapper.Engine;

namespace SigilBuild.Installer.Host.Services;

/// <summary>
/// A single dropdown option fetched from a parameter's <c>source</c> endpoint
/// — <see cref="Label"/> is shown to the user, <see cref="Value"/> is the
/// canonical id forwarded to the installer subprocess via <c>/Name=Value</c>.
/// </summary>
public sealed record HttpOption(string Label, string Value);

/// <summary>
/// Fetches dynamic ComboBox options for an install-time parameter declared with
/// a <c>source</c> block. Hits an HTTPS endpoint at install time, parses the
/// JSON response, and projects each item to a (label, value) pair.
/// </summary>
/// <remarks>
/// AOT-safe: uses <see cref="JsonDocument"/> (DOM walk only) rather than
/// reflective type deserialization. No source generators required because the
/// shape is dynamic (caller supplies the property names).
/// </remarks>
public static class HttpOptionsLoader
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// GETs <paramref name="url"/>, parses the JSON body, and returns the
    /// option list. Throws on network/HTTP/parse errors — the caller (the
    /// Install Options view's attach handler) is expected to catch and log.
    /// </summary>
    /// <remarks>
    /// Uses the one shared <see cref="SigilHttpClient"/> (P4) — system proxy,
    /// pooled connections — with a per-request 10 s timeout via a linked CTS.
    /// </remarks>
    public static async Task<IReadOnlyList<HttpOption>> LoadAsync(
        string url, string itemsPath, string labelProperty, string valueProperty,
        CancellationToken ct)
    {
        // R8: re-check the scheme HERE, not only at pack time. SIG0323 validates the
        // URL as written in the manifest; this is the URL actually about to be
        // requested, after token substitution — a `source.url` assembled from
        // parameter values is not knowable at pack time, and the values this fetch
        // returns are substituted into install steps that run elevated. Refuse before
        // the GET rather than after, so nothing cleartext is ever put on the wire.
        if (url is null || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            InstallerLog.Error(
                $"HttpOptionsLoader: refusing to fetch parameter options over a non-https URL ('{url}')");
            throw new InvalidOperationException(
                $"parameter source URL must be https:// (got '{url}')");
        }

        InstallerLog.Info($"HttpOptionsLoader: GET {url}");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RequestTimeout);
        using var response = await SigilHttpClient.Shared.GetAsync(url, timeoutCts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        return ParseJson(json, itemsPath, labelProperty, valueProperty);
    }

    /// <summary>
    /// Pure JSON-to-options projection — separated from <see cref="LoadAsync"/>
    /// so tests can exercise the parser without spinning up an HTTP listener.
    /// Returns an empty list when <paramref name="itemsPath"/> is missing or
    /// not an array; silently skips items whose <paramref name="valueProperty"/>
    /// is missing or empty (a value-less option is unselectable so emitting it
    /// would only confuse users).
    /// </summary>
    public static IReadOnlyList<HttpOption> ParseJson(
        string json, string itemsPath, string labelProperty, string valueProperty)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(itemsPath, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            InstallerLog.Error($"HttpOptionsLoader: items_path '{itemsPath}' missing or not an array");
            return Array.Empty<HttpOption>();
        }
        var list = new List<HttpOption>();
        foreach (var item in arr.EnumerateArray())
        {
            var label = item.TryGetProperty(labelProperty, out var l) ? JsonValueToString(l) : "";
            var value = item.TryGetProperty(valueProperty, out var v) ? JsonValueToString(v) : "";
            if (!string.IsNullOrEmpty(value))
                list.Add(new HttpOption(label, value));
        }
        return list;
    }

    /// <summary>
    /// Stringify any JSON scalar (string/number/bool) so the dropdown can use
    /// integer ids, GUID strings, or true/false flags interchangeably. The
    /// previous implementation called <c>GetString()</c> unconditionally and
    /// crashed with <c>InvalidOperationException</c> when the configured
    /// <c>value_property</c> pointed at a numeric column.
    /// </summary>
    private static string JsonValueToString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "",
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => el.GetRawText(),
    };
}
