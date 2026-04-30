using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using System.Threading.Tasks;
using SigilBuild.Core.Configuration;
using SigilBuild.Core.Diagnostics;

namespace SigilBuild.Cli.Commands;

public static class ValidateCommand
{
    public static Command Build()
    {
        var pathArg = new Argument<string>("path", () => "sigil.yaml", "Path to the sigil.yaml manifest");
        var format = new Option<string>("--format", () => "text", "Output format: text | json");
        var cmd = new Command("validate", "Validate a sigil.yaml manifest");
        cmd.AddArgument(pathArg);
        cmd.AddOption(format);
        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var path = ctx.ParseResult.GetValueForArgument(pathArg);
            var fmt = ctx.ParseResult.GetValueForOption(format) ?? "text";
            var result = await ManifestLoader.LoadAsync(path, new ProcessEnvironmentReader());

            if (string.Equals(fmt, "json", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(Console.Out, path, result);
            }
            else
            {
                DiagnosticReporter.Write(Console.Error, result.Diagnostics, useColor: false);
                if (result.Manifest is not null)
                    Console.Out.WriteLine($"OK: {path}");
            }

            ctx.ExitCode = result.Manifest is null ? 1 : 0;
        });
        return cmd;
    }

    private static void WriteJson(System.IO.TextWriter writer, string path, LoadResult result)
    {
        var bufferStream = new System.IO.MemoryStream();
        using (var json = new Utf8JsonWriter(bufferStream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("path", path);
            json.WriteBoolean("valid", result.Manifest is not null);
            json.WriteStartArray("diagnostics");
            foreach (var d in result.Diagnostics)
            {
                json.WriteStartObject();
                json.WriteString("severity", d.Severity switch
                {
                    DiagnosticSeverity.Error => "error",
                    DiagnosticSeverity.Warning => "warning",
                    _ => "info",
                });
                json.WriteString("code", d.Code);
                json.WriteString("message", d.Message);
                json.WriteString("file", d.Location.File);
                json.WriteNumber("line", d.Location.Line);
                json.WriteNumber("column", d.Location.Column);
                json.WriteString("docsUrl", d.DocsUrl);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        writer.WriteLine(System.Text.Encoding.UTF8.GetString(bufferStream.ToArray()));
    }
}
