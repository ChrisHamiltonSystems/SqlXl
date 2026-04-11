using System.ComponentModel;
using System.Data;
using Microsoft.Extensions.Caching.Memory;
using Spectre.Console;
using Spectre.Console.Cli;
using SqlXl.Core;
using SqlXl.Models;

namespace SqlXl.Commands;

public class TestCommand : Command<TestCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("--table <TABLE>")]
        [Description("Domain table to test, e.g. dbo.Products or just Products (assumes dbo)")]
        public string Table { get; set; } = string.Empty;

        [CommandOption("--rows <N>")]
        [Description("Number of test rows to generate per feature (default: 5, max: 100)")]
        public int Rows { get; set; } = 5;

        [CommandOption("--connection <CONNSTR>")]
        [Description("SQL Server connection string")]
        public string ConnectionString { get; set; } = "Data Source=localhost;Database=SqlXlDemo;Integrated Security=true;TrustServerCertificate=true;";

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(Table))
                return ValidationResult.Error("--table is required");
            if (Rows < 1 || Rows > 100)
                return ValidationResult.Error("--rows must be between 1 and 100");
            return ValidationResult.Success();
        }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var (schema, table) = ParseTable(settings.Table);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"Testing [cyan]{Markup.Escape(schema)}.{Markup.Escape(table)}[/]...");
        AnsiConsole.MarkupLine("[yellow]Note:[/] test data will be committed to the database. Run against SqlXlDemo or a test database.");
        AnsiConsole.WriteLine();

        try
        {
            var cache = new MemoryCache(new MemoryCacheOptions());
            var dataService = new DataService(settings.ConnectionString, cache);

            // Find all configured features for this table
            List<BulkOpFeature> features = null;
            AnsiConsole.Status().Start("Looking up configured features...", ctx =>
            {
                features = dataService.GetAllBulkOpFeaturesForTable(schema, table);
            });

            if (features.Count == 0)
            {
                AnsiConsole.MarkupLine($"[red]No features found for {Markup.Escape(schema)}.{Markup.Escape(table)}.[/]");
                AnsiConsole.MarkupLine($"Run [cyan]sqlxl insert --table {Markup.Escape(settings.Table)}[/] first to scaffold a feature.");
                return 1;
            }

            AnsiConsole.MarkupLine($"Found [cyan]{features.Count}[/] feature(s). Running {settings.Rows} row(s) each.");
            AnsiConsole.WriteLine();

            // Instantiate BulkOpsHelper once (loads all features from DB)
            var bulkOpsSettings = new BulkOpsSettings
            {
                ConnectionString = settings.ConnectionString,
                StopAfterThisManyErrors = 10
            };
            var bulkOpsHelper = new BulkOpsHelper(bulkOpsSettings);

            int passed = 0;
            int failed = 0;

            foreach (var feature in features)
            {
                AnsiConsole.Markup($"  [[{feature.InsertUpdateDeleteOrCustom.ToUpper()}]] {Markup.Escape(feature.UserFriendlyFeatureName)} ... ");

                try
                {
                    DataTable testData = GenerateTestData(bulkOpsHelper, feature, settings.Rows);

                    DataSet result = BulkOpsHelper.ExecuteFeatureAsync(
                        settings.ConnectionString,
                        testData,
                        feature.ID,
                        stopAfterThisManyErrors: 10,
                        debug: false).GetAwaiter().GetResult();

                    if (result.Tables.Count > 0 && result.Tables[0].Rows.Count > 0)
                    {
                        var row = result.Tables[0].Rows[0];
                        bool isSuccessful = row["IsSuccessful"]?.ToString()?.ToLower() == "true";

                        if (isSuccessful)
                        {
                            AnsiConsole.MarkupLine($"[green]PASS[/] " +
                                $"(inserted: {row["RowsInserted"]}, " +
                                $"updated: {row["RowsUpdated"]}, " +
                                $"deleted: {row["RowsDeleted"]})");
                            passed++;
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("[red]FAIL[/]");
                            if (result.Tables.Count > 1)
                            {
                                foreach (DataRow errorRow in result.Tables[1].Rows)
                                    AnsiConsole.MarkupLine($"    [red]-[/] {Markup.Escape(errorRow["Msg"]?.ToString() ?? "")}");
                            }
                            failed++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]ERROR[/] {Markup.Escape(ex.Message)}");
                    failed++;
                }
            }

            AnsiConsole.WriteLine();
            if (failed == 0)
                AnsiConsole.MarkupLine($"[green]All {passed} feature(s) passed.[/]");
            else
                AnsiConsole.MarkupLine($"[red]{failed} failed[/], [green]{passed} passed[/].");
            AnsiConsole.WriteLine();

            return failed == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.WriteException(ex);
            return 2;
        }
    }

    private static DataTable GenerateTestData(BulkOpsHelper helper, BulkOpFeature feature, int rows)
    {
        var dataTable = feature.InsertUpdateDeleteOrCustom.ToLower() switch
        {
            "insert" => helper.GenerateValidTestDataForAn_INSERT_Feature(feature.ID, rows),
            "update" => helper.GenerateValidTestDataForAn_UPDATE_Feature(feature.ID, rows),
            "delete" => helper.GenerateValidTestDataForA_DELETE_Feature(feature.ID, rows),
            _ => throw new NotSupportedException($"Operation type '{feature.InsertUpdateDeleteOrCustom}' is not supported by sqlxl test.")
        };

        // ExecuteFeatureAsync generates its own RequestID — remove it from generated data
        if (dataTable.Columns.Contains("RequestID"))
            dataTable.Columns.Remove("RequestID");

        return dataTable;
    }

    private static (string schema, string table) ParseTable(string input)
    {
        var parts = input.Split('.', 2);
        return parts.Length == 2
            ? (parts[0].Trim(), parts[1].Trim())
            : ("dbo", parts[0].Trim());
    }
}
