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
        [CommandOption("--file <FILE>")]
        [Description("Excel file path to import (e.g., products.xlsx)")]
        public string FilePath { get; set; } = string.Empty;

        [CommandOption("--feature <ID>")]
        [Description("BulkOpFeature ID from ZZ_SlappFramework.BulkOpFeatures")]
        public int? FeatureId { get; set; }

        [CommandOption("--connection <CONNSTR>")]
        [Description("SQL Server connection string")]
        public string ConnectionString { get; set; } = "Data Source=localhost;Database=TestDatabase001;Integrated Security=true;TrustServerCertificate=true;";

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(FilePath))
                return ValidationResult.Error("--file is required");
            if (!File.Exists(FilePath))
                return ValidationResult.Error($"File not found: {FilePath}");
            if (FeatureId == null)
                return ValidationResult.Error("--feature is required");
            return ValidationResult.Success();
        }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        AnsiConsole.MarkupLine($"Importing [yellow]{Markup.Escape(settings.FilePath)}[/] via Feature ID [yellow]{settings.FeatureId}[/]...");
        AnsiConsole.WriteLine();

        try
        {
            byte[] excelBytes = File.ReadAllBytes(settings.FilePath);
            AnsiConsole.MarkupLine($"File loaded: [cyan]{excelBytes.Length:N0} bytes[/]");

            var cache = new MemoryCache(new MemoryCacheOptions());
            var dataService = new DataService(settings.ConnectionString, cache);

            BulkOpFeature feature = null;
            AnsiConsole.Status().Start("Fetching feature metadata...", ctx =>
            {
                feature = dataService.GetBulkOpFeature(settings.FeatureId!.Value);
            });

            if (feature == null)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] BulkOpFeature with ID {settings.FeatureId} not found.");
                return 1;
            }

            AnsiConsole.MarkupLine($"Feature:  [cyan]{Markup.Escape(feature.UserFriendlyFeatureName)}[/]");
            AnsiConsole.MarkupLine($"Type:     [cyan]{Markup.Escape(feature.InsertUpdateDeleteOrCustom)}[/]");
            AnsiConsole.MarkupLine($"Table:    [cyan]{Markup.Escape(feature.DomainSchemaName)}.{Markup.Escape(feature.DomainTableName)}[/]");
            AnsiConsole.WriteLine();

            DataSet templateData = null;
            AnsiConsole.Status().Start("Fetching expected structure from SQL Server...", ctx =>
            {
                templateData = dataService.CallGetExcelTemplateDataSproc(settings.FeatureId!.Value, null);
            });

            // Tables[0] column names use pipe-syntax (e.g. "ProductName|Product Name").
            // ExcelImporter expects plain DB column names, so strip the display-name portion.
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
                    settings.FeatureId!.Value,
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
                    AnsiConsole.MarkupLine($"  Rows inserted: [cyan]{row["RowsInserted"]}[/]");
                    AnsiConsole.MarkupLine($"  Rows updated:  [cyan]{row["RowsUpdated"]}[/]");
                    AnsiConsole.MarkupLine($"  Rows deleted:  [cyan]{row["RowsDeleted"]}[/]");
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
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }
}
