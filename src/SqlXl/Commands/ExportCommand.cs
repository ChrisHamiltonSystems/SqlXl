using System.ComponentModel;
using System.Data;
using Microsoft.Extensions.Caching.Memory;
using Spectre.Console;
using Spectre.Console.Cli;
using SqlXl.Core;

namespace SqlXl.Commands;

public class ExportCommand : Command<ExportCommand.Settings>
{
    public class Settings : ConnectionSettings
    {
        [CommandOption("--query <SQL>")]
        [Description("SELECT statement to export (validated via SqlXl infrastructure before execution)")]
        public string Query { get; set; } = string.Empty;

        [CommandOption("--output <FILE>")]
        [Description("Output Excel file path (optional; defaults to export_YYYYMMDD_HHmmss.xlsx)")]
        public string OutputPath { get; set; } = string.Empty;

        [CommandOption("--no-launch")]
        [Description("Do not open the generated Excel file (useful for agents and scripts)")]
        public bool NoLaunch { get; set; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(Query))
                return ValidationResult.Error("--query is required");
            return ValidationResult.Success();
        }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Exporting query results to Excel...");
        AnsiConsole.WriteLine();

        try
        {
            var connStr = settings.ResolveConnection();
            var cache = new MemoryCache(new MemoryCacheOptions());
            var dataService = new DataService(connStr, cache);

            DataTable data = null;
            AnsiConsole.Status().Start("Validating and executing query...", ctx =>
            {
                data = dataService.ExecuteSelectQuery(settings.Query);
            });

            byte[] excelBytes = null;
            AnsiConsole.Status().Start("Generating Excel file...", ctx =>
            {
                var generator = new ExcelTemplateGenerator();
                excelBytes = generator.GenerateSimpleExcel(data);
            });

            string outputPath = string.IsNullOrWhiteSpace(settings.OutputPath)
                ? $"export_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
                : settings.OutputPath;

            File.WriteAllBytes(outputPath, excelBytes);

            AnsiConsole.MarkupLine("[green]Export complete![/]");
            AnsiConsole.MarkupLine($"File:     [cyan]{Markup.Escape(Path.GetFullPath(outputPath))}[/]");
            AnsiConsole.MarkupLine($"Rows:     [cyan]{data.Rows.Count}[/]");
            AnsiConsole.MarkupLine($"Columns:  [cyan]{data.Columns.Count}[/]");
            AnsiConsole.WriteLine();

            if (!settings.NoLaunch)
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(Path.GetFullPath(outputPath))
                    { UseShellExecute = true });

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
