using System.Collections.Generic;

namespace SigilBuild.Signing.Local;

public static class TimestampAuthority
{
    private static readonly string[] Defaults =
    {
        "http://timestamp.digicert.com",
        "http://timestamp.sectigo.com",
        "http://timestamp.globalsign.com/tsa/r6advanced1",
    };

    public static IEnumerable<string> Candidates(string? configured)
    {
        if (!string.IsNullOrEmpty(configured)) yield return configured!;
        foreach (var url in Defaults)
            if (configured != url) yield return url;
    }
}
