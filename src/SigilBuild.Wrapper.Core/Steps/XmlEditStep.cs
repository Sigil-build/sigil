namespace SigilBuild.Wrapper.Steps;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;

/// <summary>
/// <c>xml_edit</c> step (P8, gap G9): set the element (or an <c>attribute</c> on it)
/// selected by <c>xpath</c> in an XML file. Uses <see cref="XmlDocument"/> with the
/// AOT-safe XPath subset. When the node is absent and <c>create_if_missing</c> is
/// set, a <em>simple absolute element path</em> (<c>/a/b/c</c>) is created; a
/// complex xpath that matches nothing fails. Journaled for byte-exact rollback;
/// output is re-serialized (original formatting not preserved — documented).
/// </summary>
internal sealed class XmlEditStep : IStep
{
    private readonly InstallStep.XmlEdit _spec;

    public XmlEditStep(InstallStep.XmlEdit spec) => _spec = spec;

    public Task<StepResult> RunAsync(StepContext ctx, RollbackJournal journal, CancellationToken ct)
    {
        var xpath = ctx.Resolve(_spec.Xpath);
        var attribute = _spec.Attribute is null ? null : ctx.Resolve(_spec.Attribute);
        var value = ctx.Resolve(_spec.Value);

        var result = ConfigFileEditor.Edit(
            ctx, journal, _spec.Path, _spec.CreateIfMissing,
            current => XmlEditor.Set(current, xpath, attribute, value, _spec.CreateIfMissing),
            "xml_edit", _spec.AllowOutsideInstallDir);

        return Task.FromResult(result);
    }
}

internal static class XmlEditor
{
    /// <summary>
    /// The XXE posture of every <c>xml_edit</c> parse, stated rather than inherited
    /// (register row R33).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="XmlReaderSettings.XmlResolver"/> = <c>null</c>.</b> On .NET 10 this
    /// is already the default for both <see cref="XmlDocument"/> and
    /// <see cref="XmlReaderSettings"/>, so external-entity file disclosure and SSRF are
    /// blocked today whether we say so or not. Saying so is the point: an unasserted
    /// framework default can be revoked by a future framework, an
    /// <c>AppContext</c> switch, or a runtimeconfig knob, and this parse runs inside an
    /// elevated process over a file the manifest chose. It is set on the document too,
    /// not only the reader, because the document is what would resolve anything on a
    /// later <c>Save</c>/validate path.
    /// </para>
    /// <para>
    /// <b><see cref="DtdProcessing.Prohibit"/>.</b> The resolver default never covered
    /// the <em>internal</em> DTD subset, which was parsed and expanded with no cap — the
    /// billion-laughs shape (<c>&lt;!ENTITY lol2 "&amp;lol;&amp;lol;&amp;lol;…"&gt;</c>)
    /// costs the elevated installer memory and time before any edit happens, and per
    /// register row R16 the target file can sit somewhere an attacker writes. Prohibit
    /// makes a document that so much as declares a <c>&lt;!DOCTYPE&gt;</c> an
    /// <see cref="XmlException"/>, which <c>ConfigFileEditor</c> surfaces as a step
    /// failure with the file untouched.
    /// </para>
    /// <para>
    /// <b>Prohibit rather than a capped Parse</b> is a deliberate strictness choice: no
    /// shipped example, and no configuration format the guides teach editing, declares a
    /// DTD, so the refusal costs nothing real, and "no DTD reaches this parser" is an
    /// invariant a reviewer can check by reading one line. A capped
    /// <see cref="DtdProcessing.Parse"/> with
    /// <see cref="XmlReaderSettings.MaxCharactersFromEntities"/> would accept more
    /// documents at the cost of a number to tune and a bound to argue about.
    /// </para>
    /// <para>
    /// <see cref="XmlReaderSettings.IgnoreWhitespace"/> stays <c>false</c> (the default),
    /// which is what keeps <see cref="XmlDocument.PreserveWhitespace"/> meaningful, and
    /// comments / processing instructions are likewise preserved so a config file
    /// survives a round trip through this step.
    /// </para>
    /// </remarks>
    private static XmlReaderSettings SecureReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreWhitespace = false,
        IgnoreComments = false,
        IgnoreProcessingInstructions = false,
    };

    public static string Set(string? content, string xpath, string? attribute, string value, bool createIfMissing)
    {
        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        if (!string.IsNullOrWhiteSpace(content))
        {
            using var text = new StringReader(content!);
            using var reader = XmlReader.Create(text, SecureReaderSettings());
            doc.Load(reader);
        }

        var node = doc.SelectSingleNode(xpath) as XmlElement;
        if (node is null)
        {
            if (!createIfMissing)
            {
                throw new InvalidOperationException($"xml_edit: xpath '{xpath}' matched no element");
            }
            node = CreateSimplePath(doc, xpath)
                ?? throw new InvalidOperationException(
                    $"xml_edit: xpath '{xpath}' matched no element and could not be created " +
                    "(only a simple absolute element path like /a/b/c is auto-created)");
        }

        if (!string.IsNullOrEmpty(attribute))
        {
            node.SetAttribute(attribute, value);
        }
        else
        {
            node.InnerText = value;
        }

        // OuterXml preserves the declaration node verbatim (unlike Save(TextWriter),
        // which would rewrite the declaration's encoding to match the writer).
        return doc.OuterXml;
    }

    /// <summary>
    /// Create the element chain for a simple absolute path <c>/a/b/c</c> (element
    /// names only — no predicates, axes, wildcards, or <c>//</c>). Returns the
    /// deepest element, or <c>null</c> when the xpath is not a simple path.
    /// </summary>
    private static XmlElement? CreateSimplePath(XmlDocument doc, string xpath)
    {
        if (string.IsNullOrEmpty(xpath) || xpath[0] != '/')
        {
            return null;
        }
        var parts = xpath.Substring(1).Split('/');
        if (parts.Length == 0)
        {
            return null;
        }

        XmlNode parent = doc;
        XmlElement? current = null;
        foreach (var part in parts)
        {
            if (part.Length == 0 || !IsSimpleName(part))
            {
                return null; // not a simple element name → give up.
            }

            var child = FindChildElement(parent, part);
            if (child is null)
            {
                if (parent is XmlDocument && doc.DocumentElement is not null && !string.Equals(doc.DocumentElement.Name, part, StringComparison.Ordinal))
                {
                    return null; // a different root already exists — don't fabricate a second one.
                }
                child = doc.CreateElement(part);
                parent.AppendChild(child);
            }
            current = child;
            parent = child;
        }
        return current;
    }

    private static XmlElement? FindChildElement(XmlNode parent, string name)
    {
        foreach (XmlNode n in parent.ChildNodes)
        {
            if (n is XmlElement e && string.Equals(e.Name, name, StringComparison.Ordinal))
            {
                return e;
            }
        }
        return null;
    }

    private static bool IsSimpleName(string s)
    {
        foreach (var c in s)
        {
            if (!char.IsLetterOrDigit(c) && c is not '_' and not '-' and not '.' and not ':')
            {
                return false;
            }
        }
        return char.IsLetter(s[0]) || s[0] == '_';
    }
}
