using System.CommandLine;
using System.Data;
using SqlXl.Core;
using Microsoft.Extensions.Caching.Memory;

namespace SqlXl.Commands;

public static class ImportCommand
{
    public static Command Create()
    {
        var command = new Command("import", "Import Excel data to SQL Server via BulkOpFeature");

        // Options
        var fileOption = new Option<string>(
            name: "--file",
            description: "Excel file path to import (e.g., products.xlsx)")
        {
            IsRequired = true
        };

        var featureOption = new Option<int>(
            name: "--feature",
            description: "BulkOpFeature ID from ZZ_SlappFramework.BulkOpFeatures")
        {
            IsRequired = true
        };

        var connectionOption = new Option<string>(
            name: "--connection",
            description: "SQL Server connection string",
            getDefaultValue: () => "Data Source=localhost;Database=TestDatabase001;Integrated Security=true;TrustServerCertificate=true;");

        command.AddOption(fileOption);
        command.AddOption(featureOption);
        command.AddOption(connectionOption);

        command.SetHandler(async (string filePath, int featureId, string connectionString) =>
        {
            await ExecuteImport(filePath, featureId, connectionString);
        }, fileOption, featureOption, connectionOption);

        return command;
    }

    private static async Task ExecuteImport(string filePath, int featureId, string connectionString)
    {
        try
        {
            Console.WriteLine($"🔄 Importing data from Excel...");
            Console.WriteLine($"File: {filePath}");
            Console.WriteLine($"Feature ID: {featureId}");
            Console.WriteLine($"Connection: {connectionString}");
            Console.WriteLine();

            // Verify file exists
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"❌ Error: File not found: {filePath}");
                return;
            }

            // Read Excel file
            byte[] excelBytes = await File.ReadAllBytesAsync(filePath);
            Console.WriteLine($"✅ File loaded: {excelBytes.Length:N0} bytes");

            // Create services
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

            // Get expected structure and dropdown options
            var templateData = await dataService.GetExcelTemplateDataAsync(featureId, null);
            DataTable expectedStructure = templateData.Tables[0]; // Schema
            DataTable dropdownOptions = templateData.Tables[1];   // FK dropdowns

            // Import Excel data
            var importer = new ExcelImporter();
            var importResult = importer.ImportFromExcel(excelBytes, expectedStructure, dropdownOptions);

            if (!importResult.IsSuccessful || importResult.ValidationErrors.Count > 0)
            {
                Console.WriteLine($"⚠️  Excel validation errors:");
                foreach (var error in importResult.ValidationErrors)
                {
                    Console.WriteLine($"   - {error}");
                }
                return;
            }

            Console.WriteLine($"✅ Excel parsed successfully!");
            Console.WriteLine($"📊 Rows to process: {importResult.RowsProcessed}");
            Console.WriteLine($"⏭️  Empty rows skipped: {importResult.EmptyRowsSkipped}");
            Console.WriteLine();

            // Process via BulkOpsHelper
            Console.WriteLine("🔄 Validating and processing data...");
            var helper = new BulkOpsHelper(connectionString, dataService);
            var result = helper.ExecuteFeatureGivenDataTable(featureId, importResult.ImportedData);

            // Display results
            if (result.Tables.Count > 0 && result.Tables[0].Rows.Count > 0)
            {
                var resultRow = result.Tables[0].Rows[0];
                bool isSuccessful = resultRow["IsSuccessful"].ToString() == "true";

                if (isSuccessful)
                {
                    Console.WriteLine("✅ Import completed successfully!");
                    Console.WriteLine($"   Rows Inserted: {resultRow["RowsInserted"]}");
                    Console.WriteLine($"   Rows Updated: {resultRow["RowsUpdated"]}");
                    Console.WriteLine($"   Rows Deleted: {resultRow["RowsDeleted"]}");
                }
                else
                {
                    Console.WriteLine("❌ Import failed with validation errors:");
                    if (result.Tables.Count > 1)
                    {
                        foreach (DataRow errorRow in result.Tables[1].Rows)
                        {
                            Console.WriteLine($"   Row {errorRow["RowNumber"]}: {errorRow["ErrorMessage"]}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error: {ex.Message}");
            Console.WriteLine($"Stack Trace: {ex.StackTrace}");
        }
    }
}
