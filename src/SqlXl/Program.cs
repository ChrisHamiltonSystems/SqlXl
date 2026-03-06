using System.CommandLine;
using SqlXl.Commands;

// Create root command
var rootCommand = new RootCommand("SqlXL - SQL Server ↔ Excel CLI tool for data professionals");

// Add commands
rootCommand.AddCommand(ExportCommand.Create());
rootCommand.AddCommand(ImportCommand.Create());

// Execute
return await rootCommand.InvokeAsync(args);
