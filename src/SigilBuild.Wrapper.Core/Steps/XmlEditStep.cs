namespace SigilBuild.Wrapper.Steps;

using System;
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
            current => XmlEditor.Set(current, xpath, attribute, value, _spec.CreateIfMissing));

        return Task.FromResult(result);
    }
}

internal static class XmlEditor
{
    public static string Set(string? content, string xpath, string? attribute, string value, bool createIfMissing)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        if (!string.IsNullOrWhiteSpace(content))
        {
            doc.LoadXml(content!);
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
