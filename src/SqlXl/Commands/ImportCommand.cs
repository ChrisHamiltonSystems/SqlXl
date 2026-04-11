using System.ComponentModel;
using System.Data;
using Microsoft.Extensions.Caching.Memory;
using Spectre.Console;
using Spectre.Console.Cli;
using SqlXl.Core;
using SqlXl.Models;

namespace SqlXl.Commands;

public class ImportCommand : Command<ImportCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("--feature <ID>")]
        [Description("BulkOpFeature ID from SqlXl.BulkOpFeatures")]
        public int FeatureId { get; set; }

        [CommandOption("--file <FILE>")]
        [Description("Excel file to import. Omit to generate a template instead.")]
        public string FilePath { get; set; } = string.Empty;

        [CommandOption("--output <FILE>")]
        [Description("Output path for generated template (optional; defaults to Feature_YYYYMMDD.xlsx)")]
        public string OutputPath { get; set; } = string.Empty;

        [CommandOption("--connection <CONNSTR>")]
        [Description("SQL Server connection string")]
        public string ConnectionString { get; set; } = "Data Source=localhost;Database=SqlXlDemo;Integrated Security=true;TrustServerCertificate=true;";

        [CommandOption("--no-launch")]
        [Description("Do not open the generated Excel file (useful for agents and scripts)")]
        public bool NoLaunch { get; set; }

        public override ValidationResult Validate()
        {
            if (FeatureId <= 0)
                return ValidationResult.Error("--feature is required and must be a positive integer");
            if (!string.IsNullOrWhiteSpace(FilePath) && !File.Exists(FilePath))
                return ValidationResult.Error($"File not found: {FilePath}");
            return ValidationResult.Success();
        }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        bool isImport = !string.IsNullOrWhiteSpace(settings.FilePath);

        AnsiConsole.WriteLine();

        try
        {
            var cache = new MemoryCache(new MemoryCacheOptions());
            var dataService = new DataService(settings.ConnectionString, cache);

            BulkOpFeature feature = null;
            AnsiConsole.Status().Start("Fetching feature configuration...", ctx =>
            {
                feature = dataService.GetBulkOpFeature(settings.FeatureId);
            });

            if (feature == null)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] No BulkOpFeature found with ID {settings.FeatureId}.");
                AnsiConsole.MarkupLine("[grey]Run: SELECT ID, UserFriendlyFeatureName FROM SqlXl.BulkOpFeatures[/]");
                return 1;
            }

            AnsiConsole.MarkupLine(isImport
                ? $"Importing [cyan]{Markup.Escape(settings.FilePath)}[/] via feature [cyan]{Markup.Escape(feature.UserFriendlyFeatureName)}[/]..."
                : $"Generating template for [cyan]{Markup.Escape(feature.UserFriendlyFeatureName)}[/]...");
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine($"Feature:  [cyan]{Markup.Escape(feature.UserFriendlyFeatureName)}[/]");
            AnsiConsole.MarkupLine($"Type:     [cyan]{Markup.Escape(feature.InsertUpdateDeleteOrCustom)}[/]");
            AnsiConsole.MarkupLine($"Table:    [cyan]{Markup.Escape(feature.DomainSchemaName)}.{Markup.Escape(feature.DomainTableName)}[/]");
            AnsiConsole.MarkupLine($"Staging:  [cyan]{Markup.Escape(feature.StagingSchemaName)}.{Markup.Escape(feature.StagingTableName)}[/]");
            AnsiConsole.WriteLine();

            return isImport
                ? RunImport(settings, dataService, feature)
                : RunExport(settings, dataService, feature);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.WriteException(ex);
            return 2;
        }
    }

    private static int RunExport(Settings settings, DataService dataService, BulkOpFeature feature)
    {
        DataSet templateData = null;
        AnsiConsole.Status().Start("Fetching template data from SQL Server...", ctx =>
        {
            templateData = dataService.CallGetExcelTemplateDataSproc(feature.ID, null);
        });

        byte[] excelBytes = null;
        AnsiConsole.Status().Start("Generating Excel file...", ctx =>
        {
            var generator = new ExcelTemplateGenerator();
            excelBytes = generator.GenerateExcelTemplate(templateData);
        });

        string safeName = string.Concat(feature.UserFriendlyFeatureName
            .Split(Path.GetInvalidFileNameChars())).Replace(" ", "_");
        string outputPath = string.IsNullOrWhiteSpace(settings.OutputPath)
            ? $"{safeName}_{DateTime.Now:yyyyMMdd}.xlsx"
            : settings.OutputPath;

        File.WriteAllBytes(outputPath, excelBytes);

        AnsiConsole.MarkupLine("[green]Template generated successfully![/]");
        AnsiConsole.MarkupLine($"File:     [cyan]{Markup.Escape(Path.GetFullPath(outputPath))}[/]");
        AnsiConsole.MarkupLine($"Rows:     [cyan]{templateData.Tables[0].Rows.Count}[/]");
        AnsiConsole.MarkupLine($"Columns:  [cyan]{templateData.Tables[0].Columns.Count}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Fill in the template then run:");
        AnsiConsole.MarkupLine($"  [cyan]sqlxl import --feature {settings.FeatureId} --file {Markup.Escape(outputPath)}[/]");
        AnsiConsole.WriteLine();

        if (!settings.NoLaunch)
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(Path.GetFullPath(outputPath))
                { UseShellExecute = true });

        return 0;
    }

    private static int RunImport(Settings settings, DataService dataService, BulkOpFeature feature)
    {
        byte[] excelBytes = File.ReadAllBytes(settings.FilePath);
        AnsiConsole.MarkupLine($"File loaded: [cyan]{excelBytes.Length:N0} bytes[/]");

        DataSet templateData = null;
        AnsiConsole.Status().Start("Fetching expected structure from SQL Server...", ctx =>
        {
            templateData = dataService.CallGetExcelTemplateDataSproc(feature.ID, null);
        });

        DataTable expectedStructure = new DataTable();
        foreach (DataColumn col in templateData.Tables[0].Columns)
        {
            string dbColName = col.ColumnName.Split('|')[0].Trim();
            expectedStructure.Columns.Add(dbColName, col.DataType);
        }
        DataTable dropdownOptions = templateData.Tables.Count > 1 ? templateData.Tables[1] : new DataTable();

        ExcelImporter.ImportResult importResult = null;
        AnsiConsole.Status().Start("Parsing Excel file...", ctx =>
        {
            var importer = new ExcelImporter();
            importResult = importer.ImportFromExcel(excelBytes, expectedStructure, dropdownOptions);
        });

        if (!importResult.IsSuccessful || importResult.ValidationErrors.Count > 0)
        {
            AnsiConsole.MarkupLine("[red]Excel validation errors:[/]");
            foreach (var error in importResult.ValidationErrors)
                AnsiConsole.MarkupLine($"  [red]-[/] {Markup.Escape(error)}");
            return 1;
        }

        AnsiConsole.MarkupLine($"Parsed:   [cyan]{importResult.RowsProcessed} rows[/], [grey]{importResult.EmptyRowsSkipped} empty rows skipped[/]");
        AnsiConsole.WriteLine();

        DataSet result = null;
        AnsiConsole.Status().Start("Validating and processing via staging table...", ctx =>
        {
            result = BulkOpsHelper.ExecuteFeatureAsync(
                settings.ConnectionString,
                importResult.ImportedData,
                feature.ID,
                stopAfterThisManyErrors: 10,
                debug: false).GetAwaiter().GetResult();
        });

        if (result.Tables.Count > 0 && result.Tables[0].Rows.Count > 0)
        {
            var row = result.Tables[0].Rows[0];
            bool isSuccessful = row["IsSuccessful"]?.ToString()?.ToLower() == "true";

            if (isSuccessful)
            {
                AnsiConsole.MarkupLine("[green]Import completed successfully![/]");
                // Generic result reporting: display every column except IsSuccessful
                foreach (DataColumn col in result.Tables[0].Columns)
                {
                    if (col.ColumnName.Equals("IsSuccessful", StringComparison.OrdinalIgnoreCase))
                        continue;
                    AnsiConsole.MarkupLine($"  {Markup.Escape(col.ColumnName)}: [cyan]{Markup.Escape(row[col]?.ToString() ?? "")}[/]");
                }
                return 0;
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Import failed with validation errors:[/]");
                if (result.Tables.Count > 1)
                {
                    foreach (DataRow errorRow in result.Tables[1].Rows)
                        AnsiConsole.MarkupLine($"  [red]-[/] {Markup.Escape(errorRow["Msg"]?.ToString() ?? "")}");
                }
                return 1;
            }
        }

        return 0;
    }
}
