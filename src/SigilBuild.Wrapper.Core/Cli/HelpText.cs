namespace SigilBuild.Wrapper.Cli;

/// <summary>
/// The /? screen. Deliberately English (design D3): console output is the support
/// surface, and an admin grepping docs for "/lang=" should not get a translated
/// page. This is why CLI help does NOT flow through the localization catalog.
/// </summary>
public static class HelpText
{
    public static string Render() =>
        """
        Usage: Setup.exe [options]

          /silent, /S        install without the wizard
          /verysilent        install with no UI and no progress
          /Uninstall         uninstall
          /allusers          install for all users (elevates)
          /currentuser       install for the current user only
          /D=<path>          install directory
          /LOG[=<path>]      write an install log
          /lang=<tag>        force the wizard language
                             chrome ships in: en, uk
                             manifest screens may supply any tag
          /launch            launch the app when finished
          /closeapps         close blocking applications automatically
          /force-downgrade   allow installing over a newer version
          /PName=Value       set a declared parameter
          /?, /help          show this help

        Exit codes: 0 ok, 1 failed (rolled back), 2 cancelled, 3 downgrade blocked,
        4 files in use, 5 already running, 64 usage error, 3010 reboot required.
        """;
}
