using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Spectre.Console;
using Spectre.Console.Cli;

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
            // Connect to master — the script drops/recreates SqlXlDemo,
            // so we cannot be connected to SqlXlDemo itself.
            var csb = new SqlConnectionStringBuilder(settings.ConnectionString);
            csb.InitialCatalog = "master";
            string masterConnStr = csb.ConnectionString;

            string sql = LoadEmbeddedSql("SqlXl.sql.CreateDemoDatabase.sql");
            string[] batches = SplitOnGo(sql);

            AnsiConsole.Status().Start("Creating SqlXlDemo database...", ctx =>
            {
                using var conn = new SqlConnection(masterConnStr);
                conn.Open();

                foreach (string batch in batches)
                {
                    if (string.IsNullOrWhiteSpace(batch)) continue;
                    using var cmd = new SqlCommand(batch, conn);
                    cmd.CommandTimeout = 60;
                    cmd.ExecuteNonQuery();
                }
            });

            AnsiConsole.MarkupLine("[green]SqlXlDemo created successfully![/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("You can now run commands against it, for example:");
            AnsiConsole.MarkupLine($"  [cyan]sqlxl insert --table dbo.Products --connection \"{BuildDemoConnStr(settings.ConnectionString)}\"[/]");
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

    private static string LoadEmbeddedSql(string resourceName)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string[] SplitOnGo(string sql) =>
        Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private static string BuildDemoConnStr(string original)
    {
        var csb = new SqlConnectionStringBuilder(original);
        csb.InitialCatalog = "SqlXlDemo";
        return csb.ConnectionString;
    }
}
