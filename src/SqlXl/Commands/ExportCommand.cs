using System.ComponentModel;
using System.Data;
using Microsoft.Extensions.Caching.Memory;
using Spectre.Console;
using Spectre.Console.Cli;
using SqlXl.Core;
using SqlXl.Models;

namespace SqlXl.Commands;

public class ExportCommand : Command<ExportCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("--feature <ID>")]
        [Description("BulkOpFeature ID from ZZ_SlappFramework.BulkOpFeatures")]
        public int? FeatureId { get; set; }

        [CommandOption("--output <FILE>")]
        [Description("Output Excel file path (e.g., products.xlsx)")]
        public string OutputPath { get; set; } = string.Empty;

        [CommandOption("--ids <IDS>")]
        [Description("Comma-separated primary key IDs to populate (for UPDATE features)")]
        public string SelectedIds { get; set; } = string.Empty;

        [CommandOption("--connection <CONNSTR>")]
        [Description("SQL Server connection string")]
        public string ConnectionString { get; set; } = "Data Source=localhost;Database=TestDatabase001;Integrated Security=true;TrustServerCertificate=true;";

        public override ValidationResult Validate()
        {
            if (FeatureId == null)
                return ValidationResult.Error("--feature is required");
            if (string.IsNullOrWhiteSpace(OutputPath))
                return ValidationResult.Error("--output is required");
            return ValidationResult.Success();
        }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        AnsiConsole.MarkupLine($"Exporting data for Feature ID [yellow]{settings.FeatureId}[/]...");
        AnsiConsole.WriteLine();

        try
        {
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
            AnsiConsole.Status().Start("Fetching template data from SQL Server...", ctx =>
            {
                string selectedIds = string.IsNullOrWhiteSpace(settings.SelectedIds) ? null : settings.SelectedIds;
                templateData = dataService.CallGetExcelTemplateDataSproc(settings.FeatureId!.Value, selectedIds);
            });

            byte[] excelBytes = null;
            AnsiConsole.Status().Start("Generating Excel file...", ctx =>
            {
                var generator = new ExcelTemplateGenerator();
                excelBytes = generator.GenerateExcelTemplate(templateData);
            });

            File.WriteAllBytes(settings.OutputPath, excelBytes);

            AnsiConsole.MarkupLine($"[green]Excel file generated successfully![/]");
            AnsiConsole.MarkupLine($"File:     [cyan]{Markup.Escape(Path.GetFullPath(settings.OutputPath))}[/]");
            AnsiConsole.MarkupLine($"Rows:     [cyan]{templateData.Tables[0].Rows.Count}[/]");
            AnsiConsole.MarkupLine($"Columns:  [cyan]{templateData.Tables[0].Columns.Count}[/]");

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
