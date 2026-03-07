using OfficeOpenXml;
using Spectre.Console.Cli;
using SqlXl.Commands;

// EPPlus license - set for non-commercial personal use (free)
ExcelPackage.License.SetNonCommercialPersonal("SqlXl");

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("sqlxl");
    config.AddCommand<ExportCommand>("export")
        .WithDescription("Export data to an Excel template from a BulkOpFeature");
    config.AddCommand<ImportCommand>("import")
        .WithDescription("Import Excel data to SQL Server via a BulkOpFeature");
});

return app.Run(args);
