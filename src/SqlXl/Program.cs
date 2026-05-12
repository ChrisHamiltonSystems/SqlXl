using Spectre.Console;
using Spectre.Console.Cli;
using SqlXl.Commands;
using SqlXl.Config;

string[] cleanedArgs;
try
{
    var (cleaned, overridePath) = ConfigLocator.ExtractFromArgs(args);
    ConfigLocator.SetOverride(overridePath);
    cleanedArgs = cleaned;
}
catch (ArgumentException ex)
{
    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
    return 1;
}

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("sqlxl");
    config.AddCommand<InitCommand>("init")
        .WithDescription("Connect to a SQL Server database, install SqlXL infrastructure, and save the connection profile");
    config.AddCommand<UseCommand>("use")
        .WithDescription("Set the active connection profile (e.g. sqlxl use prod)");
    config.AddBranch("connections", connections =>
    {
        connections.SetDescription("Manage saved connection profiles");
        connections.AddCommand<ConnectionsListCommand>("list")
            .WithDescription("List all saved connection profiles");
        connections.AddCommand<ConnectionsRemoveCommand>("remove")
            .WithDescription("Remove a saved connection profile");
    });
    config.AddCommand<InsertCommand>("insert")
        .WithDescription("Generate an INSERT template or import a filled template into a table");
    config.AddCommand<UpdateCommand>("update")
        .WithDescription("Generate an UPDATE template pre-populated with existing rows, or import a filled template");
    config.AddCommand<ImportCommand>("import")
        .WithDescription("Generate a template or import data via a custom BulkOpFeature (--feature N)");
    config.AddCommand<ExportCommand>("export")
        .WithDescription("Export SQL query results to Excel (requires sqlxl init)");
    config.AddCommand<TestCommand>("test")
        .WithDescription("Auto-generate and run test data against all configured features for a table");
    config.AddCommand<InferCommand>("infer")
        .WithDescription("Read an Excel file and emit a CREATE TABLE statement inferred from the data");
    config.AddCommand<DemoCommand>("demo")
        .WithDescription("Create the SqlXlDemo database with sample data (drops and recreates)");
    config.AddCommand<LlmContextCommand>("llm-context")
        .WithDescription("Emit a versioned, machine-readable reference document for the installed sqlxl binary");
});

return app.Run(cleanedArgs);
