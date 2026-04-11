using System.ComponentModel;
using Microsoft.Data.SqlClient;
using Spectre.Console;
using Spectre.Console.Cli;
using SqlXl.Helpers;

namespace SqlXl.Commands;

public class DemoCommand : Command<DemoCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("--connection <CONNSTR>")]
        [Description("SQL Server connection string for the server that will host SqlXlDemo")]
        public string ConnectionString { get; set; } = "Data Source=localhost;Integrated Security=true;TrustServerCertificate=true;";

        [CommandOption("--yes")]
        [Description("Skip the confirmation prompt and proceed immediately")]
        public bool SkipConfirmation { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]WARNING:[/] This will [red]DROP and RECREATE[/] the [cyan]SqlXlDemo[/] database.");
        AnsiConsole.MarkupLine("[yellow]         All existing data in SqlXlDemo will be permanently lost.[/]");
        AnsiConsole.WriteLine();

        if (!settings.SkipConfirmation)
        {
            if (!AnsiConsole.Confirm("Proceed?", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                return 0;
            }
            AnsiConsole.WriteLine();
        }

        try
        {
            // Step 1: run CreateDemoDatabase.sql against master
            // (the script drops and recreates SqlXlDemo, so we can't be connected to it)
            var masterConnStr = SwapDatabase(settings.ConnectionString, "master");

            AnsiConsole.Status().Start("Creating SqlXlDemo database and sample data...", ctx =>
            {
                SqlScriptExecutor.ExecuteEmbeddedScript("SqlXl.sql.CreateDemoDatabase.sql", masterConnStr);
            });

            AnsiConsole.MarkupLine("[green]  SqlXlDemo database created.[/]");

            // Step 2: run CreateInfrastructure.sql against SqlXlDemo
            var demoConnStr = SwapDatabase(settings.ConnectionString, "SqlXlDemo");

            AnsiConsole.Status().Start("Installing SqlXL infrastructure...", ctx =>
            {
                SqlScriptExecutor.ExecuteEmbeddedScript("SqlXl.sql.CreateInfrastructure.sql", demoConnStr);
            });

            AnsiConsole.MarkupLine("[green]  SqlXL infrastructure installed.[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[green]SqlXlDemo is ready![/] You can now run commands against it, for example:");
            AnsiConsole.MarkupLine($"  [cyan]sqlxl insert --table dbo.Products --connection \"{demoConnStr}\"[/]");
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

    private static string SwapDatabase(string connectionString, string database)
    {
        var csb = new SqlConnectionStringBuilder(connectionString);
        csb.InitialCatalog = database;
        return csb.ConnectionString;
    }
}
