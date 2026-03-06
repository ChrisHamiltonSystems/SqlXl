using System.CommandLine;
using System.Data;
using SqlXl.Core;
using Microsoft.Extensions.Caching.Memory;

namespace SqlXl.Commands;

public static class ExportCommand
{
    public static Command Create()
    {
        var command = new Command("export", "Export data to Excel template from a BulkOpFeature");

        // Options
        var featureOption = new Option<int>(
            name: "--feature",
            description: "BulkOpFeature ID from ZZ_SlappFramework.BulkOpFeatures")
        {
            IsRequired = true
        };

        var outputOption = new Option<string>(
            name: "--output",
            description: "Output Excel file path (e.g., products.xlsx)")
        {
            IsRequired = true
        };

        var queryOption = new Option<string?>(
            name: "--query",
            description: "Optional SQL SELECT query to populate data (for UPDATE features)",
            getDefaultValue: () => null);

        var connectionOption = new Option<string>(
            name: "--connection",
            description: "SQL Server connection string",
            getDefaultValue: () => "Data Source=localhost;Database=TestDatabase001;Integrated Security=true;TrustServerCertificate=true;");

        command.AddOption(featureOption);
        command.AddOption(outputOption);
        command.AddOption(queryOption);
        command.AddOption(connectionOption);

        command.SetHandler(async (int featureId, string outputPath, string? query, string connectionString) =>
        {
            await ExecuteExport(featureId, outputPath, query, connectionString);
        }, featureOption, outputOption, queryOption, connectionOption);

        return command;
    }

    private static async Task ExecuteExport(int featureId, string outputPath, string? customQuery, string connectionString)
    {
        try
        {
            Console.WriteLine($"🔄 Exporting data for Feature ID {featureId}...");
            Console.WriteLine($"Connection: {connectionString}");
            Console.WriteLine($"Output: {outputPath}");
            Console.WriteLine();

            // Create DataService
            var cache = new MemoryCache(new MemoryCacheOptions());
            var dataService = new DataService(connectionString, cache);

            // Get the BulkOpFeature metadata
            var feature = await dataService.GetBulkOpFeatureByIdAsync(featureId);
            if (feature == null)
            {
                Console.WriteLine($"❌ Error: BulkOpFeature with ID {featureId} not found.");
                return;
            }

            Console.WriteLine($"Feature: {feature.UserFriendlyFeatureName}");
            Console.WriteLine($"Type: {feature.InsertUpdateDeleteOrCustom}");
            Console.WriteLine($"Table: {feature.DomainSchemaName}.{feature.DomainTableName}");
            Console.WriteLine();

            // Get template data (schema + FK dropdowns + metadata)
            DataSet templateData = await dataService.GetExcelTemplateDataAsync(featureId, customQuery);

            // Generate Excel file
            var generator = new ExcelTemplateGenerator();
            byte[] excelBytes = generator.GenerateExcelTemplate(templateData);

            // Write to file
            await File.WriteAllBytesAsync(outputPath, excelBytes);

            Console.WriteLine($"✅ Excel file generated successfully!");
            Console.WriteLine($"📁 File: {Path.GetFullPath(outputPath)}");
            Console.WriteLine($"📊 Rows: {templateData.Tables[0].Rows.Count}");
            Console.WriteLine($"📋 Columns: {templateData.Tables[0].Columns.Count}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error: {ex.Message}");
            Console.WriteLine($"Stack Trace: {ex.StackTrace}");
        }
    }
}
