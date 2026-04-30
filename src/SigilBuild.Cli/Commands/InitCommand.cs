using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Threading.Tasks;

namespace SigilBuild.Cli.Commands;

public static class InitCommand
{
    public static Command Build()
    {
        var nonInteractive = new Option<bool>("--non-interactive", "Do not prompt; require all inputs as flags");
        var template = new Option<string>("--template", () => "minimal", "Template name: minimal | msix | azure-signing | full");
        var output = new Option<string>("--out", () => "sigil.yaml", "Output file path");
        var force = new Option<bool>("--force", "Overwrite if the output file already exists");
        var appId = new Option<string?>("--app-id", "Reverse-DNS app id");
        var appName = new Option<string?>("--app-name", "Display name");
        var version = new Option<string?>("--version", "SemVer version (e.g. 0.1.0)");
        var publisher = new Option<string?>("--publisher", "Publisher display name");

        var cmd = new Command("init", "Create a new sigil.yaml manifest");
        cmd.AddOption(nonInteractive);
        cmd.AddOption(template);
        cmd.AddOption(output);
        cmd.AddOption(force);
        cmd.AddOption(appId);
        cmd.AddOption(appName);
        cmd.AddOption(version);
        cmd.AddOption(publisher);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var ni = ctx.ParseResult.GetValueForOption(nonInteractive);
            var tmpl = ctx.ParseResult.GetValueForOption(template) ?? "minimal";
            var outPath = ctx.ParseResult.GetValueForOption(output) ?? "sigil.yaml";
            var forceVal = ctx.ParseResult.GetValueForOption(force);
            var aId = ctx.ParseResult.GetValueForOption(appId);
            var aName = ctx.ParseResult.GetValueForOption(appName);
            var aVer = ctx.ParseResult.GetValueForOption(version);
            var pub = ctx.ParseResult.GetValueForOption(publisher);

            if (File.Exists(outPath) && !forceVal)
            {
                Console.Error.WriteLine($"sigil: refuse to overwrite '{outPath}' (pass --force).");
                ctx.ExitCode = 1;
                return;
            }

            if (!ni)
            {
                aId ??= Prompt("Application id (e.g. com.example.MyApp)");
                aName ??= Prompt("Display name");
                aVer ??= Prompt("Version (SemVer, e.g. 0.1.0)");
                pub ??= Prompt("Publisher");
            }

            if (string.IsNullOrEmpty(aId) || string.IsNullOrEmpty(aName) ||
                string.IsNullOrEmpty(aVer) || string.IsNullOrEmpty(pub))
            {
                Console.Error.WriteLine("sigil: --app-id, --app-name, --version, --publisher are required in --non-interactive mode");
                ctx.ExitCode = 64;
                return;
            }

            var raw = LoadTemplate(tmpl);
            var rendered = raw
                .Replace("{APP_ID}", aId)
                .Replace("{APP_NAME}", aName)
                .Replace("{APP_VERSION}", aVer)
                .Replace("{PUBLISHER}", pub);

            await File.WriteAllTextAsync(outPath, rendered);
            Console.Out.WriteLine($"wrote {outPath}");
            ctx.ExitCode = 0;
        });

        return cmd;
    }

    private static string Prompt(string message)
    {
        Console.Error.Write($"{message}: ");
        return Console.ReadLine() ?? "";
    }

    private static string LoadTemplate(string name)
    {
        var resource = $"SigilBuild.Cli.Commands.Templates.{name}.yaml";
        var asm = typeof(InitCommand).Assembly;
        using var stream = asm.GetManifestResourceStream(resource)
            ?? throw new FileNotFoundException($"unknown template '{name}'");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
