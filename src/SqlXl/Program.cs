using OfficeOpenXml;
using Spectre.Console.Cli;
using SqlXl.Commands;

// EPPlus license - set for non-commercial personal use (free)
ExcelPackage.License.SetNonCommercialPersonal("SqlXl");

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("sqlxl");
    config.AddCommand<InsertCommand>("insert")
        .WithDescription("Generate an INSERT template or import a filled template into a table");
    config.AddCommand<InitCommand>("init")
        .WithDescription("Install SqlXL infrastructure into an existing SQL Server database");
    config.AddCommand<DemoCommand>("demo")
        .WithDescription("Create the SqlXlDemo database with sample data (drops and recreates)");
    config.AddCommand<ExportCommand>("export")
        .WithDescription("Get Excel template starting point file for a BulkOpFeature");
    config.AddCommand<ImportCommand>("import")
        .WithDescription("Import Excel template file (with data) to SQL Server via a BulkOpFeature");
});

return app.Run(args);
