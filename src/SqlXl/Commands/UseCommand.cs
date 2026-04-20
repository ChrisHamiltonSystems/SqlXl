using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using SqlXl.Config;

namespace SqlXl.Commands;

public class UseCommand : Command<UseCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROFILE>")]
        [Description("Name of the profile to activate")]
        public string ProfileName { get; set; } = string.Empty;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        AnsiConsole.WriteLine();
        try
        {
            var config = SqlXlConfig.Load();
            config.SetActiveProfile(settings.ProfileName);
            config.Save();
            AnsiConsole.MarkupLine($"Active profile set to [cyan]{Markup.Escape(settings.ProfileName)}[/].");
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
