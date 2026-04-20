using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using SqlXl.Config;

namespace SqlXl.Commands;

public class ConnectionsRemoveCommand : Command<ConnectionsRemoveCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROFILE>")]
        [Description("Name of the profile to remove")]
        public string ProfileName { get; set; } = string.Empty;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        AnsiConsole.WriteLine();
        try
        {
            var config = SqlXlConfig.Load();
            bool wasActive = config.ActiveProfile == settings.ProfileName;

            config.RemoveProfile(settings.ProfileName);
            config.Save();

            AnsiConsole.MarkupLine($"Profile [cyan]{Markup.Escape(settings.ProfileName)}[/] removed.");

            if (wasActive)
            {
                if (!string.IsNullOrEmpty(config.ActiveProfile))
                    AnsiConsole.MarkupLine($"Active profile switched to [cyan]{Markup.Escape(config.ActiveProfile)}[/].");
                else
                    AnsiConsole.MarkupLine("[yellow]No active profile set. Run `sqlxl use <profile>` to select one.[/]");
            }

            AnsiConsole.WriteLine();
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.WriteLine();
            return 1;
        }
    }
}
