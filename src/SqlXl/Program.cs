using Spectre.Console.Cli;
using SqlXl.Commands;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("sqlxl");
    config.AddCommand<TestCommand>("test")
        .WithDescription("Auto-generate and run test data against all configured features for a table");
    config.AddCommand<UpdateCommand>("update")
        .WithDescription("Generate an UPDATE template pre-populated with existing rows, or import a filled template");
    config.AddCommand<InsertCommand>("insert")
        .WithDescription("Generate an INSERT template or import a filled template into a table");
    config.AddCommand<InitCommand>("init")
        .WithDescription("Install SqlXL infrastructure into an existing SQL Server database");
    config.AddCommand<DemoCommand>("demo")
        .WithDescription("Create the SqlXlDemo database with sample data (drops and recreates)");
    config.AddCommand<ExportCommand>("export")
        .WithDescription("Export SQL query results to Excel (requires sqlxl init)");
    config.AddCommand<ImportCommand>("import")
        .WithDescription("Generate a template or import data via a custom BulkOpFeature (--feature N)");
});

return app.Run(args);
