using System.ComponentModel;
using System.Data;
using Microsoft.Extensions.Caching.Memory;
using Spectre.Console;
using Spectre.Console.Cli;
using SqlXl.Core;
using SqlXl.Models;

namespace SqlXl.Commands;

public class UpdateCommand : Command<UpdateCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("--table <TABLE>")]
        [Description("Domain table to target, e.g. dbo.Products or just Products (assumes dbo)")]
        public string Table { get; set; } = string.Empty;

        [CommandOption("--where <CLAUSE>")]
        [Description("SQL WHERE clause to filter rows for the template, e.g. \"CategoryID = 1\" (export mode only)")]
        public string Where { get; set; } = string.Empty;

        [CommandOption("--file <FILE>")]
        [Description("Excel file to import. Omit to generate a populated UPDATE template instead.")]
        public string FilePath { get; set; } = string.Empty;

        [CommandOption("--output <FILE>")]
        [Description("Output path for generated template (optional; defaults to TableName_update_YYYYMMDD.xlsx)")]
        public string OutputPath { get; set; } = string.Empty;

        [CommandOption("--connection <CONNSTR>")]
        [Description("SQL Server connection string")]
        public string ConnectionString { get; set; } = "Data Source=localhost;Database=SqlXlDemo;Integrated Security=true;TrustServerCertificate=true;";

        [CommandOption("--no-launch")]
        [Description("Do not open the generated Excel file (useful for agents and scripts)")]
        public bool NoLaunch { get; set; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(Table))
                return ValidationResult.Error("--table is required");
            if (!string.IsNullOrWhiteSpace(FilePath) && !File.Exists(FilePath))
                return ValidationResult.Error($"File not found: {FilePath}");
            if (!string.IsNullOrWhiteSpace(FilePath) && !string.IsNullOrWhiteSpace(Where))
                return ValidationResult.Error("--where is only valid in export mode (omit --file)");
            return ValidationResult.Success();
        }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var (schema, table) = ParseTable(settings.Table);
        bool isImport = !string.IsNullOrWhiteSpace(settings.FilePath);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(isImport
            ? $"Importing [cyan]{Markup.Escape(settings.FilePath)}[/] into [cyan]{Markup.Escape(schema)}.{Markup.Escape(table)}[/]..."
            : $"Generating UPDATE template for [cyan]{Markup.Escape(schema)}.{Markup.Escape(table)}[/]...");
        AnsiConsole.WriteLine();

        try
        {
            var cache = new MemoryCache(new MemoryCacheOptions());
            var dataService = new DataService(settings.ConnectionString, cache);

            // Look up or scaffold the BulkOpFeature for this table
            BulkOpFeature feature = null;
            AnsiConsole.Status().Start("Checking for existing feature configuration...", ctx =>
            {
                feature = dataService.GetBulkOpFeatureForTable(schema, table, "Update");
            });

            if (feature == null)
            {
                AnsiConsole.MarkupLine($"[grey]No existing feature found — scaffolding UPDATE feature for {Markup.Escape(schema)}.{Markup.Escape(table)}...[/]");
                AnsiConsole.Status().Start("Scaffolding (creating staging table, sproc, feature config)...", ctx =>
                {
                    dataService.ScaffoldUpdateFeature(schema, table);
                    feature = dataService.GetBulkOpFeatureForTable(schema, table, "Update");
                });
                AnsiConsole.MarkupLine("[grey]  Scaffolding complete.[/]");
                AnsiConsole.WriteLine();
            }

            AnsiConsole.MarkupLine($"Feature:  [cyan]{Markup.Escape(feature.UserFriendlyFeatureName)}[/]");
            AnsiConsole.MarkupLine($"Table:    [cyan]{Markup.Escape(feature.DomainSchemaName)}.{Markup.Escape(feature.DomainTableName)}[/]");
            AnsiConsole.MarkupLine($"Staging:  [cyan]{Markup.Escape(feature.StagingSchemaName)}.{Markup.Escape(feature.StagingTableName)}[/]");
            AnsiConsole.WriteLine();

            return isImport
                ? RunImport(settings, dataService, feature)
                : RunExport(settings, dataService, feature, schema, table);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.WriteException(ex);
            return 2;
        }
    }

    private static int RunExport(Settings settings, DataService dataService, BulkOpFeature feature, string schema, string table)
    {
        // Resolve --where clause to comma-separated PKs — schema/table/pkColumn
        // are all DB-sourced; only the WHERE clause itself comes from the user.
        string selectedIds = null;
        if (!string.IsNullOrWhiteSpace(settings.Where))
        {
            AnsiConsole.Status().Start("Resolving WHERE clause to matching row IDs...", ctx =>
            {
                string pkColumn = dataService.GetPrimaryKeyColumnName(schema, table);
                selectedIds = dataService.GetPrimaryKeyIdsForWhere(schema, table, pkColumn, settings.Where);
            });

            if (selectedIds == null)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning:[/] No rows matched WHERE {Markup.Escape(settings.Where)} — generating empty template.");
            }
            else
            {
                int rowCount = selectedIds.Split(',').Length;
                AnsiConsole.MarkupLine($"WHERE clause matched [cyan]{rowCount}[/] row(s).");
                AnsiConsole.WriteLine();
            }
        }

        DataSet templateData = null;
        AnsiConsole.Status().Start("Fetching template data from SQL Server...", ctx =>
        {
            templateData = dataService.CallGetExcelTemplateDataSproc(feature.ID, selectedIds);
        });

        byte[] excelBytes = null;
        AnsiConsole.Status().Start("Generating Excel file...", ctx =>
        {
            var generator = new ExcelTemplateGenerator();
            excelBytes = generator.GenerateExcelTemplate(templateData);
        });

        string outputPath = string.IsNullOrWhiteSpace(settings.OutputPath)
            ? $"{table}_update_{DateTime.Now:yyyyMMdd}.xlsx"
            : settings.OutputPath;

        File.WriteAllBytes(outputPath, excelBytes);

        AnsiConsole.MarkupLine("[green]Template generated successfully![/]");
        AnsiConsole.MarkupLine($"File:     [cyan]{Markup.Escape(Path.GetFullPath(outputPath))}[/]");
        AnsiConsole.MarkupLine($"Rows:     [cyan]{templateData.Tables[0].Rows.Count}[/]");
        AnsiConsole.MarkupLine($"Columns:  [cyan]{templateData.Tables[0].Columns.Count}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Edit the rows then run:");
        AnsiConsole.MarkupLine($"  [cyan]sqlxl update --table {Markup.Escape(settings.Table)} --file {Markup.Escape(outputPath)} --no-launch[/]");
        AnsiConsole.WriteLine();

        if (!settings.NoLaunch)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetFullPath(outputPath)) { UseShellExecute = true });

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
        AnsiConsole.Status().Start("Validating and updating via staging table...", ctx =>
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
                AnsiConsole.MarkupLine("[green]Update completed successfully![/]");
                AnsiConsole.MarkupLine($"  Rows updated: [cyan]{row["RowsUpdated"]}[/]");
                return 0;
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Update failed with validation errors:[/]");
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

    private static (string schema, string table) ParseTable(string input)
    {
        var parts = input.Split('.', 2);
        return parts.Length == 2
            ? (parts[0].Trim(), parts[1].Trim())
            : ("dbo", parts[0].Trim());
    }
}
