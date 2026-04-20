using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using SqlXl.Config;
using SqlXl.Helpers;

namespace SqlXl.Commands;

public class InitCommand : Command<InitCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("--connection <CONNSTR>")]
        [Description("SQL Server connection string for the database to initialize")]
        public string ConnectionString { get; set; } = string.Empty;

        [CommandOption("--profile <NAME>")]
        [Description("Name to save this connection under (default: \"default\")")]
        public string ProfileName { get; set; } = "default";

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

            // Save connection profile
            var config = SqlXlConfig.Load();
            var (wasEncrypted, isNew) = config.SetProfile(settings.ProfileName, settings.ConnectionString);

            // First profile automatically becomes active
            if (string.IsNullOrEmpty(config.ActiveProfile) || isNew && config.Profiles.Count == 1)
                config.ActiveProfile = settings.ProfileName;

            config.Save();

            string encryptedNote = wasEncrypted ? " [grey](credentials encrypted with Windows DPAPI)[/]" : string.Empty;
            string newOrUpdated = isNew ? "Saved" : "Updated";
            AnsiConsole.MarkupLine($"{newOrUpdated} profile [cyan]{Markup.Escape(settings.ProfileName)}[/].{encryptedNote}");

            if (config.ActiveProfile == settings.ProfileName)
                AnsiConsole.MarkupLine($"Active profile: [green]{Markup.Escape(settings.ProfileName)}[/]");

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Your database is ready. Try:");
            AnsiConsole.MarkupLine("  [cyan]sqlxl insert --table dbo.YourTable[/]");
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
