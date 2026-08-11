using System;
using System.IO;
using System.Xml;
using FluentAssertions;
using SigilBuild.Wrapper.Steps;
using Xunit;

namespace SigilBuild.Wrapper.Tests.Steps;

/// <summary>
/// P8: pure transform tests for the INI / JSON / XML editors — no filesystem. The
/// step wrappers add snapshot/journal/write around these.
/// </summary>
public class ConfigEditorTests
{
    // ── INI ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Ini_replaces_an_existing_key_and_preserves_other_lines()
    {
        var result = IniEditor.Set("; header\n[app]\nx=1\ny=2\n", "app", "x", "9");
        result.Should().Contain("; header");
        result.Should().Contain("x=9");
        result.Should().Contain("y=2");
        result.Should().NotContain("x=1");
    }

    [Fact]
    public void Ini_adds_a_new_key_in_an_existing_section()
    {
        var result = IniEditor.Set("[app]\nx=1\n", "app", "z", "3");
        result.Should().Contain("x=1");
        result.Should().Contain("z=3");
    }

    [Fact]
    public void Ini_adds_a_missing_section()
    {
        var result = IniEditor.Set("[app]\nx=1\n", "logging", "level", "debug");
        result.Should().Contain("[logging]");
        result.Should().Contain("level=debug");
    }

    [Fact]
    public void Ini_edits_the_global_section()
    {
        var result = IniEditor.Set("g=1\n[app]\nx=1\n", "", "g", "2");
        result.Should().Contain("g=2");
        result.Should().Contain("[app]");
        result.Should().Contain("x=1");
    }

    [Fact]
    public void Ini_creates_content_from_nothing()
    {
        IniEditor.Set(null, "app", "k", "v").Should().Contain("[app]").And.Contain("k=v");
    }

    [Fact]
    public void Ini_key_match_is_case_insensitive()
        => IniEditor.Set("[a]\nKey=old\n", "a", "key", "new").Should().Contain("key=new").And.NotContain("old");

    // ── JSON ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Json_edits_an_existing_pointer()
        => JsonEditor.Set("""{"a":{"b":1}}""", "/a/b", "2").Should().Contain("\"b\": 2");

    [Fact]
    public void Json_adds_a_new_key()
        => JsonEditor.Set("""{"a":1}""", "/b", "hi").Should().Contain("\"b\": \"hi\"");

    [Fact]
    public void Json_creates_nested_objects_along_the_pointer()
    {
        var result = JsonEditor.Set("{}", "/a/b/c", "1");
        result.Should().Contain("\"a\"").And.Contain("\"b\"").And.Contain("\"c\": 1");
    }

    [Theory]
    [InlineData("true", "true")]
    [InlineData("42", "42")]
    [InlineData("plain", "\"plain\"")]
    public void Json_value_is_typed_when_it_parses_as_json_else_a_string(string value, string expectedJson)
        => JsonEditor.Set("{}", "/k", value).Should().Contain("\"k\": " + expectedJson);

    [Fact]
    public void Json_creates_document_from_nothing()
        => JsonEditor.Set(null, "/a", "1").Should().Contain("\"a\": 1");

    // ── XML ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Xml_edits_element_text()
        => XmlEditor.Set("<root><a>old</a></root>", "/root/a", null, "new", false)
            .Should().Contain("<a>new</a>");

    [Fact]
    public void Xml_sets_an_attribute()
        => XmlEditor.Set("<root><a/></root>", "/root/a", "id", "5", false)
            .Should().Contain("id=\"5\"");

    [Fact]
    public void Xml_creates_a_nested_element_path()
    {
        var result = XmlEditor.Set("<root/>", "/root/a/b", null, "v", createIfMissing: true);
        result.Should().Contain("<a>").And.Contain("<b>v</b>");
    }

    [Fact]
    public void Xml_creates_document_from_nothing()
        => XmlEditor.Set(null, "/config/x", null, "v", createIfMissing: true)
            .Should().Contain("<config>").And.Contain("<x>v</x>");

    [Fact]
    public void Xml_missing_node_without_create_throws()
    {
        var act = () => XmlEditor.Set("<root/>", "/root/missing", null, "v", createIfMissing: false);
        act.Should().Throw<InvalidOperationException>();
    }

    // ── XML: XXE posture (R33) ───────────────────────────────────────────────

    /// <summary>
    /// Register row R33. The internal DTD subset was parsed with no expansion cap, so
    /// a config file the elevated installer edits could bill it for an unbounded
    /// entity expansion (billion laughs) before a single byte was written. This is the
    /// test that fails at the parent commit: the resolver default already blocked
    /// EXTERNAL entities there, so only the DTD assertion proves anything.
    /// </summary>
    [Fact]
    public void Xml_edit_refuses_a_document_declaring_an_internal_dtd_subset()
    {
        const string Xml = """
            <?xml version="1.0"?>
            <!DOCTYPE lolz [ <!ENTITY lol "lol"> <!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;"> ]>
            <config><item>&lol2;</item></config>
            """;

        var act = () => XmlEditor.Set(Xml, "/config/item", null, "value", createIfMissing: false);

        act.Should().Throw<XmlException>(
            "DtdProcessing must be Prohibit — an internal DTD subset in a config file the " +
            "elevated installer reads is an unbounded entity-expansion (billion laughs) " +
            "vector, and the XmlResolver default never covered it");
    }

    [Fact]
    public void Xml_edit_refuses_a_dtd_even_with_no_entities_at_all()
    {
        const string Xml = """
            <!DOCTYPE config>
            <config><item>x</item></config>
            """;

        var act = () => XmlEditor.Set(Xml, "/config/item", null, "value", createIfMissing: false);

        act.Should().Throw<XmlException>(
            "the invariant is 'no DTD reaches this parser', not 'no expensive DTD' — a " +
            "reviewer must be able to check it by reading one line");
    }

    /// <summary>
    /// The external-entity half. This already passed at the parent commit — .NET 10
    /// defaults <c>XmlResolver</c> to <c>null</c> — and it is here precisely so that a
    /// future framework or <c>AppContext</c> change that revokes the default cannot do
    /// it silently.
    /// </summary>
    [Fact]
    public void Xml_edit_never_resolves_an_external_entity()
    {
        var secret = Path.Combine(Path.GetTempPath(), "sigil-r33-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(secret, "TOP-SECRET-CANARY");
        try
        {
            var xml =
                "<?xml version=\"1.0\"?>\n" +
                "<!DOCTYPE config [ <!ENTITY xxe SYSTEM \"file:///" +
                secret.Replace('\\', '/') + "\"> ]>\n" +
                "<config><item>&xxe;</item></config>";

            var act = () => XmlEditor.Set(xml, "/config/item", null, "value", createIfMissing: false);

            act.Should().Throw<XmlException>(
                "an external entity must never be dereferenced by a parse running at high " +
                "integrity — that is file disclosure and SSRF in one field");
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Fact]
    public void Xml_edit_still_preserves_declaration_comments_and_whitespace()
    {
        const string Xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <!-- keep me -->
            <config>
              <item>old</item>
            </config>
            """;

        var result = XmlEditor.Set(Xml, "/config/item", null, "new", createIfMissing: false);

        result.Should().StartWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        result.Should().Contain("<!-- keep me -->");
        result.Should().Contain("\n  <item>new</item>");
    }
}
