using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using SqlXl.Helpers;

namespace SqlXl.Commands;

public class InitCommand : Command<InitCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("--connection <CONNSTR>")]
        [Description("SQL Server connection string for the database to initialize")]
        public string ConnectionString { get; set; } = string.Empty;

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
                return ValidationResult.Error("--connection is required");
            return ValidationResult.Success();
        }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Installing SqlXL infrastructure...");
        AnsiConsole.WriteLine();

        try
        {
            AnsiConsole.Status().Start("Connecting and running setup scripts...", ctx =>
            {
                SqlScriptExecutor.ExecuteEmbeddedScript("SqlXl.sql.CreateInfrastructure.sql", settings.ConnectionString);
            });

            AnsiConsole.MarkupLine("[green]SqlXL infrastructure installed successfully![/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Your database is ready. Try:");
            AnsiConsole.MarkupLine($"  [cyan]sqlxl insert --table dbo.YourTable --connection \"{settings.ConnectionString}\"[/]");
            AnsiConsole.WriteLine();

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.WriteException(ex);
            return 2;
        }
    }
}
