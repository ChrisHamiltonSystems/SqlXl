# SqlXL

A lightweight CLI tool for SQL Server data professionals to efficiently move data between SQL Server and Excel.

## What is SqlXL?

SqlXL is a command-line companion tool for databases using [SlappFramework](https://github.com/ChrisHamiltonSystems/SlappFramework) infrastructure. It provides fast, convenient commands for two key scenarios:

1. **Pull**: Export SQL query results to formatted Excel templates
2. **Push**: Import validated Excel data back to SQL Server

## Prerequisites

- SQL Server database with SlappFramework infrastructure installed
- Configured BulkOpFeatures in `ZZ_SlappFramework.BulkOpFeatures`
- .NET 8.0 Runtime

## Installation

```bash
# Install globally as a dotnet tool
dotnet tool install --global SqlXl
```

## Quick Start

```bash
# Pull data to Excel template
sqlxl pull --query "SELECT * FROM Employees WHERE DeptID = 5" --feature 10 --output data.xlsx

# Push edited data back to SQL Server
sqlxl push --file data.xlsx --feature 10
```

## Commands

### `pull` - Export data to Excel
Executes a SQL query and generates a formatted Excel file using an existing BulkOpFeature configuration.

### `push` - Import data from Excel
Validates and imports Excel data using SlappFramework's staging table validation.

## Status

🚧 **In Development** - Version 0.1.0

## License

TBD

## Author

Chris Hamilton
