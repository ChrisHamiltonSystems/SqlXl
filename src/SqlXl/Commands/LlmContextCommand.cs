using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.SqlClient;
using Spectre.Console.Cli;
using SqlXl.Config;

namespace SqlXl.Commands;

public class LlmContextCommand : Command<LlmContextCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("--format <FORMAT>")]
        [Description("Output format: text (default) or json")]
        public string Format { get; set; } = "text";

        [CommandOption("--include-state")]
        [Description("Query the active DB: configured features, domain tables (requires active profile or --connection)")]
        public bool IncludeState { get; set; }

        [CommandOption("--connection <CONNSTR>")]
        [Description("SQL Server connection string (used with --include-state)")]
        public string ExplicitConnection { get; set; }

        [CommandOption("--profile <NAME>")]
        [Description("Named connection profile (used with --include-state)")]
        public string Profile { get; set; }

        public override Spectre.Console.ValidationResult Validate()
        {
            var fmt = (Format ?? "text").Trim().ToLowerInvariant();
            if (fmt is not ("text" or "json"))
                return Spectre.Console.ValidationResult.Error("--format must be 'text' or 'json'");
            return Spectre.Console.ValidationResult.Success();
        }
    }

    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public override int Execute(CommandContext context, Settings settings)
    {
        var format = (settings.Format ?? "text").Trim().ToLowerInvariant();
        var now = DateTime.UtcNow;
        var ver = typeof(LlmContextCommand).Assembly.GetName().Version;
        var version = ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.3.0";
        var profilePath = ConfigLocator.ResolvePath();

        object activeState = null;
        if (settings.IncludeState)
        {
            try { activeState = QueryActiveState(settings); }
            catch (Exception ex) { Console.Error.WriteLine($"Warning: --include-state failed: {ex.Message}"); }
        }

        if (format == "json")
            EmitJson(now, version, profilePath, activeState);
        else
            EmitText(now, version, profilePath);

        return 0;
    }

    private static object QueryActiveState(Settings settings)
    {
        var connStr = ConnectionResolver.Resolve(settings.ExplicitConnection, settings.Profile);
        var csb = new SqlConnectionStringBuilder(connStr);
        var config = SqlXlConfig.Load();
        var profileName = !string.IsNullOrWhiteSpace(settings.Profile) ? settings.Profile
            : !string.IsNullOrWhiteSpace(settings.ExplicitConnection) ? "(ad-hoc)"
            : config.ActiveProfile ?? "default";

        using var conn = new SqlConnection(connStr);
        conn.Open();

        var features = conn.Query(@"
            SELECT ID, UserFriendlyFeatureName, InsertUpdateDeleteOrCustom,
                   DomainSchemaName, DomainTableName, StagingSchemaName, StagingTableName,
                   SprocToProcessPerfectStagedData
            FROM SqlXl.BulkOpFeatures ORDER BY ID")
            .Select(r => new
            {
                id = (int)r.ID,
                name = (string)r.UserFriendlyFeatureName,
                type = (string)r.InsertUpdateDeleteOrCustom,
                domain_table = $"{r.DomainSchemaName}.{r.DomainTableName}",
                staging_table = $"{r.StagingSchemaName}.{r.StagingTableName}",
                sproc = (string)r.SprocToProcessPerfectStagedData
            }).ToList();

        var domainTables = conn.Query<string>(@"
            SELECT DISTINCT TABLE_SCHEMA + '.' + TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_SCHEMA NOT IN ('SqlXl', 'sys')
            ORDER BY 1").ToList();

        return new
        {
            profile = new { name = profileName, server = csb.DataSource, database = csb.InitialCatalog },
            configured_features = features,
            domain_tables = domainTables
        };
    }

    private static void EmitJson(DateTime at, string version, string profilePath, object activeState)
    {
        var doc = JsonNode.Parse(JsonTemplate)!.AsObject();
        doc["generated_at"] = at.ToString("yyyy-MM-ddTHH:mm:ssZ");
        doc["sqlxl_version"] = version;
        doc["docs_url"] = "https://github.com/ChrisHamiltonSystems/SqlXl";
        doc["connection_model"]!.AsObject()["profile_storage_path"] = profilePath;
        if (activeState != null)
            doc["active_state"] = JsonSerializer.SerializeToNode(activeState);
        Console.Out.WriteLine(doc.ToJsonString(IndentedOptions));
    }

    private static void EmitText(DateTime at, string version, string profilePath)
    {
        Console.Out.Write(TextTemplate
            .Replace("__VERSION__", version)
            .Replace("__GENERATED_AT__", at.ToString("yyyy-MM-ddTHH:mm:ssZ"))
            .Replace("__DOCS_URL__", "https://github.com/ChrisHamiltonSystems/SqlXl")
            .Replace("__PROFILE_PATH__", profilePath));
    }

    // ─── Templates (runtime values substituted via Replace / JsonNode assignment) ─────────

    private const string JsonTemplate = """
{
  "format_version": 1,
  "sqlxl_version": "1.3.0",
  "generated_at": "1970-01-01T00:00:00Z",
  "docs_url": "https://github.com/ChrisHamiltonSystems/SqlXl",
  "summary": "sqlxl is a SQL Server <-> Excel bridge. Three core flows: template-driven DML (insert/update/import), query export (export), and schema inference (infer). Extensibility via BulkOpFeature rows in the target database.",

  "connection_model": {
    "profile_storage_path": "<profile_storage_path>",
    "default_profile_name": "default",
    "override_flags": [
      {
        "name": "--connection",
        "value_type": "connection_string",
        "value_placeholder": "<CONNSTR>",
        "required": false,
        "description": "Ad-hoc connection string. Overrides --profile and the active profile."
      },
      {
        "name": "--profile",
        "value_type": "string",
        "value_placeholder": "<NAME>",
        "required": false,
        "description": "Named saved profile. Overrides the active profile."
      }
    ]
  },

  "commands": [
    {
      "name": "init",
      "summary": "Connect to a SQL Server database, install SqlXL infrastructure, and save the connection profile.",
      "synopses": ["sqlxl init --connection <CONNSTR> [--profile <NAME>]"],
      "flags": [
        {
          "name": "--connection",
          "value_type": "connection_string",
          "value_placeholder": "<CONNSTR>",
          "required": true,
          "description": "SQL Server connection string for the database to initialize."
        },
        {
          "name": "--profile",
          "value_type": "string",
          "value_placeholder": "<NAME>",
          "required": false,
          "default": "default",
          "description": "Name to save this connection under."
        }
      ],
      "behaviors": [
        "Installs the SqlXl.* schema (BulkOpFeatures, ColumnUIConfigurations, DebugLog, Meta_Columns, RequestContext, SavedQueries) into the target database.",
        "Idempotent: re-running updates the profile and re-applies infrastructure."
      ],
      "destructive": false
    },

    {
      "name": "use",
      "summary": "Set the active connection profile.",
      "synopses": ["sqlxl use <PROFILE>"],
      "positional_args": [
        { "name": "PROFILE", "value_type": "string", "required": true, "description": "Name of the profile to activate." }
      ],
      "flags": [],
      "examples": [
        { "command": "sqlxl use prod", "comment": "Switch the active profile to 'prod'." }
      ]
    },

    {
      "name": "connections",
      "summary": "Manage saved connection profiles.",
      "synopses": ["sqlxl connections list", "sqlxl connections remove <PROFILE>"],
      "flags": [],
      "subcommands": [
        {
          "name": "connections list",
          "summary": "List all saved connection profiles.",
          "synopses": ["sqlxl connections list"],
          "flags": []
        },
        {
          "name": "connections remove",
          "summary": "Remove a saved connection profile.",
          "synopses": ["sqlxl connections remove <PROFILE>"],
          "positional_args": [
            { "name": "PROFILE", "value_type": "string", "required": true, "description": "Profile to remove." }
          ],
          "flags": []
        }
      ]
    },

    {
      "name": "insert",
      "summary": "Generate an INSERT template, or import a filled template into a table.",
      "synopses": [
        "sqlxl insert --table <T> [--output <PATH>] [--no-launch]",
        "sqlxl insert --table <T> --file <XLSX> [--no-launch]"
      ],
      "flags": [
        { "name": "--table", "value_type": "string", "value_placeholder": "<TABLE>", "required": true, "description": "Domain table, e.g. dbo.Products or just Products (assumes dbo)." },
        { "name": "--file", "value_type": "path", "value_placeholder": "<FILE>", "required": false, "description": "Excel file to import. Omit to generate a template." },
        { "name": "--output", "value_type": "path", "value_placeholder": "<FILE>", "required": false, "description": "Output path for generated template. Default: <Table>_insert_YYYYMMDD.xlsx." },
        { "name": "--no-launch", "value_type": "bool", "required": false, "default": false, "description": "Do not auto-open the generated Excel file. USE THIS in agents/scripts." },
        { "name": "--connection", "value_type": "connection_string", "value_placeholder": "<CONNSTR>", "required": false, "description": "Override profile." },
        { "name": "--profile", "value_type": "string", "value_placeholder": "<NAME>", "required": false, "description": "Override active profile." }
      ],
      "behaviors": [
        "If no BulkOpFeature exists for the table, AUTO-SCAFFOLDS one: creates staging table SqlXl.Staging_<Table>_ForInserts, sproc <Table>_InsertFromStaging, and a BulkOpFeatures row of type 'Insert'.",
        "Generated templates exclude IDENTITY/PK columns from the Data sheet (they remain in the Metadata sheet with IsPrimaryKey=YES).",
        "Templates are 3-sheet workbooks: Data, DropdownOptions, Metadata."
      ],
      "examples": [
        { "command": "sqlxl insert --table dbo.Products --output t.xlsx --no-launch", "comment": "Generate template; auto-scaffolds feature on first run." },
        { "command": "sqlxl insert --table dbo.Products --file t.xlsx --no-launch", "comment": "Import a filled template." }
      ]
    },

    {
      "name": "update",
      "summary": "Generate an UPDATE template pre-populated with existing rows, or import a filled template.",
      "synopses": [
        "sqlxl update --table <T> [--where <CLAUSE>] [--output <PATH>] [--no-launch]",
        "sqlxl update --table <T> --file <XLSX> [--no-launch]"
      ],
      "flags": [
        { "name": "--table", "value_type": "string", "value_placeholder": "<TABLE>", "required": true, "description": "Domain table." },
        { "name": "--where", "value_type": "sql", "value_placeholder": "<CLAUSE>", "required": false, "scope": "template_generation", "description": "SQL WHERE clause to filter rows for the template (template generation only — IGNORED on import)." },
        { "name": "--file", "value_type": "path", "value_placeholder": "<FILE>", "required": false, "description": "Excel file to import. Omit to generate." },
        { "name": "--output", "value_type": "path", "value_placeholder": "<FILE>", "required": false, "description": "Default: <Table>_update_YYYYMMDD.xlsx." },
        { "name": "--no-launch", "value_type": "bool", "required": false, "description": "Do not auto-open Excel." }
      ],
      "behaviors": [
        "Template is pre-populated with existing rows matching --where (or all rows if omitted).",
        "Import is keyed by primary key — PK columns must be present and unchanged in the imported file."
      ]
    },

    {
      "name": "import",
      "summary": "Generate a template or import data via a custom BulkOpFeature.",
      "synopses": [
        "sqlxl import --feature <ID> [--output <PATH>] [--no-launch]",
        "sqlxl import --feature <ID> --file <XLSX> [--no-launch]"
      ],
      "flags": [
        { "name": "--feature", "value_type": "int", "value_placeholder": "<ID>", "required": true, "description": "BulkOpFeature ID from SqlXl.BulkOpFeatures." },
        { "name": "--file", "value_type": "path", "value_placeholder": "<FILE>", "required": false, "description": "Excel file to import." },
        { "name": "--output", "value_type": "path", "value_placeholder": "<FILE>", "required": false, "description": "Default: Feature_YYYYMMDD.xlsx." },
        { "name": "--no-launch", "value_type": "bool", "required": false, "description": "Do not auto-open Excel." }
      ],
      "behaviors": [
        "Generic entry point for any BulkOpFeature (Insert, Update, Delete, or Custom).",
        "Use this when invoking a manually-authored Custom feature, or when targeting a specific feature ID."
      ]
    },

    {
      "name": "export",
      "summary": "Export SQL query results to Excel.",
      "synopses": ["sqlxl export --query <SQL> [--output <PATH>] [--no-launch]"],
      "flags": [
        { "name": "--query", "value_type": "sql", "value_placeholder": "<SQL>", "required": true, "description": "SELECT statement (validated via SqlXl infrastructure before execution)." },
        { "name": "--output", "value_type": "path", "value_placeholder": "<FILE>", "required": false, "description": "Default: export_YYYYMMDD_HHmmss.xlsx." },
        { "name": "--no-launch", "value_type": "bool", "required": false, "description": "Do not auto-open Excel." }
      ],
      "prerequisites": ["init"],
      "behaviors": [
        "Single-sheet workbook output: column headers in row 1, data below.",
        "Query is validated against SqlXl infrastructure before execution — `init` must have been run on the target DB."
      ]
    },

    {
      "name": "test",
      "summary": "Auto-generate and run test data against all configured features for a table.",
      "synopses": ["sqlxl test --table <T> [--rows <N>]"],
      "flags": [
        { "name": "--table", "value_type": "string", "value_placeholder": "<TABLE>", "required": true, "description": "Domain table to test." },
        { "name": "--rows", "value_type": "int", "value_placeholder": "<N>", "required": false, "default": 1, "description": "Test rows per feature. Max: 100." }
      ],
      "behaviors": [
        "Synthesizes rows respecting unique constraints both against existing data AND across the generated batch.",
        "Test data is COMMITTED to the database — run only against disposable DBs.",
        "Each configured feature runs independently; one feature failing does not stop others.",
        "With small tables and narrow unique-column value spaces, --rows N (N>1) frequently collides. Use --rows 1 to verify pipeline."
      ],
      "destructive": true
    },

    {
      "name": "infer",
      "summary": "Read an Excel file and emit a CREATE TABLE statement inferred from the data.",
      "synopses": ["sqlxl infer <INPUT> [OPTIONS]"],
      "positional_args": [
        { "name": "INPUT", "value_type": "path", "required": true, "description": "Path to the .xlsx file to infer schema from." }
      ],
      "flags": [
        { "name": "--table", "value_type": "string", "value_placeholder": "<NAME>", "required": false, "description": "Target table name. Default: sanitized file basename." },
        { "name": "--schema", "value_type": "string", "value_placeholder": "<NAME>", "required": false, "default": "dbo", "description": "Target schema name." },
        { "name": "--sheet", "value_type": "string", "value_placeholder": "<NAME>", "required": false, "description": "Worksheet to read (REQUIRED when workbook has more than one sheet)." },
        { "name": "--sample-size", "value_type": "int", "value_placeholder": "<N>", "required": false, "default": 1000, "description": "Number of data rows to sample." },
        { "name": "--confidence-threshold", "value_type": "float", "value_placeholder": "<RATIO>", "required": false, "default": 0.9, "description": "Min valid-ratio for a type to be selected." },
        { "name": "--mode", "value_type": "enum", "enum_values": ["permissive", "strict"], "required": false, "description": "permissive (invalid -> NULL) or strict (any invalid forces NVARCHAR fallback)." },
        { "name": "--max-varchar", "value_type": "int", "value_placeholder": "<N>", "required": false, "default": 255, "description": "Cap for inferred NVARCHAR length." },
        { "name": "--date-format", "value_type": "enum", "enum_values": ["us", "iso"], "required": false, "default": "us", "description": "us (M/d/yyyy + ISO + long forms) or iso (ISO + long forms only)." },
        { "name": "--output", "value_type": "path", "value_placeholder": "<PATH>", "required": false, "description": "Write generated DDL to a file. Default: stdout." },
        { "name": "--report", "value_type": "path", "value_placeholder": "<PATH>", "required": false, "description": "Write a JSON inference report to this path." }
      ],
      "behaviors": [
        "Pure read operation — no DB connection needed.",
        "DDL goes to stdout unless --output is given (pipeable to sqlcmd)."
      ]
    },

    {
      "name": "demo",
      "summary": "Create the SqlXlDemo database with sample data (drops and recreates).",
      "synopses": ["sqlxl demo --connection <CONNSTR> [--yes]"],
      "flags": [
        { "name": "--connection", "value_type": "connection_string", "value_placeholder": "<CONNSTR>", "required": true, "description": "SQL Server connection string for the server that will host SqlXlDemo." },
        { "name": "--yes", "value_type": "bool", "required": false, "description": "Skip the confirmation prompt." }
      ],
      "behaviors": [
        "Drops and recreates the SqlXlDemo database — all existing data lost.",
        "Installs SqlXL infrastructure and seeds 11 domain tables plus 1 pre-configured Custom feature ('Assign User Roles')."
      ],
      "destructive": true
    },

    {
      "name": "llm-context",
      "summary": "Emit a versioned, machine-readable reference document for the installed sqlxl binary.",
      "synopses": [
        "sqlxl llm-context [--format json|text]",
        "sqlxl llm-context --format json --include-state"
      ],
      "flags": [
        { "name": "--format", "value_type": "enum", "enum_values": ["text", "json"], "required": false, "default": "text", "description": "Output format: text (markdown) or json (machine-readable)." },
        { "name": "--include-state", "value_type": "bool", "required": false, "description": "Query the active DB and append live state: active profile, configured features, domain tables." },
        { "name": "--connection", "value_type": "connection_string", "value_placeholder": "<CONNSTR>", "required": false, "description": "Override profile (used with --include-state)." },
        { "name": "--profile", "value_type": "string", "value_placeholder": "<NAME>", "required": false, "description": "Named profile (used with --include-state)." }
      ],
      "behaviors": [
        "No DB connection required unless --include-state is passed.",
        "JSON output validates against the sqlxl-llm-context-v1 schema."
      ]
    }
  ],

  "template_structure": {
    "sheets": [
      {
        "name": "Data",
        "purpose": "Fillable rows. Columns from GetRowsToEdit_SelectStatement.",
        "columns": [],
        "dynamic_columns": true
      },
      {
        "name": "DropdownOptions",
        "purpose": "FK / lookup values. One row per allowable option per FK column.",
        "columns": ["ForColumn", "OptionText"],
        "dynamic_columns": false
      },
      {
        "name": "Metadata",
        "purpose": "Per-column schema info for the import side.",
        "columns": ["DbColumnName", "ExcelColumnName", "SqlDataType", "IsPrimaryKey"],
        "dynamic_columns": false
      }
    ],
    "fk_label_format": "<id> - <label>",
    "column_aliasing_syntax": "[DbCol|ExcelHeader]",
    "identity_pk_excluded_from_data_sheet": true
  },

  "bulk_op_feature_schema": [
    { "name": "ID", "sql_type": "int", "nullable": false, "is_primary_key": true, "description": "Feature ID — passed to `import --feature`." },
    { "name": "UserFriendlyFeatureName", "sql_type": "nvarchar", "nullable": false, "description": "Display name." },
    { "name": "InsertUpdateDeleteOrCustom", "sql_type": "nvarchar", "nullable": false, "description": "'Insert' | 'Update' | 'Delete' | 'Custom'." },
    { "name": "DomainSchemaName", "sql_type": "nvarchar", "nullable": false },
    { "name": "DomainTableName", "sql_type": "nvarchar", "nullable": false },
    { "name": "StagingSchemaName", "sql_type": "nvarchar", "nullable": false },
    { "name": "StagingTableName", "sql_type": "nvarchar", "nullable": false },
    { "name": "GetRowsToChooseFrom_SelectStatement", "sql_type": "nvarchar(max)", "nullable": true, "description": "Feeds the DropdownOptions sheet." },
    { "name": "GetRowsToEdit_SelectStatement", "sql_type": "nvarchar(max)", "nullable": false, "description": "Defines the Data sheet shape; supports [Col|Alias] aliasing syntax." },
    { "name": "SprocToProcessPerfectStagedData", "sql_type": "nvarchar", "nullable": false, "description": "Sproc that promotes validated staged rows into the domain table." },
    { "name": "MenuDisplayRanking", "sql_type": "int", "nullable": true, "description": "UI ordering hint." }
  ],

  "agent_best_practices": [
    {
      "rule": "Always pass --no-launch on commands that produce .xlsx",
      "rationale": "Without it, Excel opens AND locks the file, breaking subsequent re-imports.",
      "applies_to": ["insert", "update", "import", "export"]
    },
    {
      "rule": "Ensure the file is closed in Excel before --file imports",
      "rationale": "The tool errors out on locked files.",
      "applies_to": ["insert", "update", "import"]
    },
    {
      "rule": "Parse the `File: <path>` line from stdout to capture output paths",
      "rationale": "Use the absolute path printed by the tool, not your guess of cwd.",
      "applies_to": []
    },
    {
      "rule": "For destructive ops, always pass --yes",
      "rationale": "The interactive confirmation prompt blocks non-TTY callers.",
      "applies_to": ["demo"]
    },
    {
      "rule": "Exit code 0 = success, non-zero = failure",
      "rationale": "Diagnostics (including detailed validation errors) are on stderr.",
      "applies_to": []
    }
  ],

  "workflows": [
    {
      "name": "round_trip_insert",
      "title": "Round-trip insert (auto-scaffolded)",
      "description": "Insert new rows into a table that has no pre-configured BulkOpFeature. The first call auto-scaffolds the feature.",
      "steps": [
        { "action": "command", "command": "sqlxl insert --table dbo.Products --output t.xlsx --no-launch", "comment": "Generate template; auto-scaffolds feature on first run." },
        { "action": "manual", "instruction": "Edit t.xlsx and add rows to the Data sheet. For FK columns, use values from DropdownOptions in `<id> - <label>` format. Close Excel." },
        { "action": "command", "command": "sqlxl insert --table dbo.Products --file t.xlsx --no-launch", "comment": "Import the filled template." }
      ]
    },
    {
      "name": "filtered_update",
      "title": "Filtered update via WHERE",
      "steps": [
        { "action": "command", "command": "sqlxl update --table dbo.Products --where \"CategoryID = 1\" --output u.xlsx --no-launch" },
        { "action": "manual", "instruction": "Edit u.xlsx values in place; keep PK columns unchanged." },
        { "action": "command", "command": "sqlxl update --table dbo.Products --file u.xlsx --no-launch" }
      ]
    },
    {
      "name": "custom_feature_import",
      "title": "Drive a Custom BulkOpFeature",
      "steps": [
        { "action": "command", "command": "sqlxl import --feature 1 --output assignments.xlsx --no-launch" },
        { "action": "manual", "instruction": "Fill rows per the feature's column shape." },
        { "action": "command", "command": "sqlxl import --feature 1 --file assignments.xlsx --no-launch" }
      ]
    },
    {
      "name": "infer_and_init",
      "title": "Infer DDL from spreadsheet, build the table, then init",
      "steps": [
        { "action": "command", "command": "sqlxl infer customers.xlsx --schema sales --table Customers --output ddl.sql" },
        { "action": "command", "command": "sqlcmd -S localhost -d MyDb -E -i ddl.sql", "comment": "Apply the generated DDL." },
        { "action": "command", "command": "sqlxl init --connection \"Server=localhost;Database=MyDb;Trusted_Connection=True;\" --profile mydb" }
      ]
    }
  ],

  "gotchas": [
    "`update --where` applies only to template generation, not import. Import is keyed by PK.",
    "`init` is required before `export` — query validation depends on SqlXl infrastructure.",
    "`infer` writes DDL to stdout by default; pass --output to write a file.",
    "Tables can be `dbo.Foo` or `Foo` (assumes `dbo`).",
    "`test` commits data to the DB — never run against production.",
    "`test --rows N>1` often collides on unique constraints in small tables; not a tool bug.",
    "Auto-scaffolding (on first `insert` for a table) creates DB objects as a side-effect — note this when running against shared databases.",
    "The `[Col|Alias]` aliasing syntax in GetRowsToEdit_SelectStatement is sqlxl-specific, not standard SQL."
  ],

  "builtin_schema": [
    { "schema": "SqlXl", "name": "BulkOpFeatures", "purpose": "Feature registry — one row per declarative bulk-op definition." },
    { "schema": "SqlXl", "name": "ColumnUIConfigurations", "purpose": "Per-column UI hints (display formatting, dropdown sources)." },
    { "schema": "SqlXl", "name": "Meta_Columns", "purpose": "Schema metadata cache." },
    { "schema": "SqlXl", "name": "RequestContext", "purpose": "Per-request execution context, used during imports." },
    { "schema": "SqlXl", "name": "SavedQueries", "purpose": "Persisted SELECT statements." },
    { "schema": "SqlXl", "name": "DebugLog", "purpose": "Diagnostic log written by sprocs during processing." },
    { "schema": "SqlXl", "name": "Staging_<Name>", "purpose": "Per-feature landing zone for imports.", "is_staging_template": true }
  ]
}
""";

    private const string TextTemplate = """
# sqlxl llm-context

```
sqlxl_version:  __VERSION__
format_version: 1
generated_at:   __GENERATED_AT__
docs_url:       __DOCS_URL__
```

This is a dense, agent-targeted reference for the `sqlxl` CLI. Everything an
LLM needs to operate the tool fluently is below — no external lookups required.

---

## 1. Mental model

`sqlxl` is a **SQL Server ⇄ Excel bridge** for SQL Server.

Three core flows:

1. **Template-driven DML** — `insert`, `update`, `import` generate Excel
   templates that humans fill in, then re-import.
2. **Query export** — `export` runs a SELECT and writes results to .xlsx.
3. **Schema inference** — `infer` reads an .xlsx and emits a `CREATE TABLE`
   statement.

Plus: `test` (synthetic data harness), `demo` (sample database), and
connection-profile management (`init`, `use`, `connections`).

Extensibility is via **BulkOpFeature** rows in the target database — server-side
declarative bulk-op definitions (see §6).

---

## 2. Connection model

| Command           | Purpose                                                 |
|-------------------|---------------------------------------------------------|
| `init`            | Install SqlXl infrastructure into a DB; save a profile  |
| `use <name>`      | Activate a saved profile                                |
| `connections list`| List all saved profiles                                 |
| `connections remove <name>` | Delete a profile                              |

Every operational command accepts:

- `--connection <connstr>` — ad-hoc connection string (overrides profile)
- `--profile <name>`       — named profile (overrides active profile)

If neither is given, the **active profile** is used.

Profile storage path: `__PROFILE_PATH__`

---

## 3. Command reference

### `init`

```
sqlxl init --connection <CONNSTR> [--profile <NAME>]
```

| Flag            | Required | Description                                            |
|-----------------|----------|--------------------------------------------------------|
| `--connection`  | yes      | Target SQL Server                                       |
| `--profile`     | no       | Profile name (default: `default`)                       |

**Behavior:** Installs the `SqlXl.*` schema (BulkOpFeatures, ColumnUIConfigurations,
DebugLog, Meta_Columns, RequestContext, SavedQueries, plus staging tables) into
the target database. Idempotent — re-running updates the profile and re-applies
infrastructure.

**Required for:** `export` (validates queries via SqlXl infrastructure).

---

### `use`

```
sqlxl use <PROFILE>
```

Activates the named profile. Subsequent commands without `--connection` /
`--profile` will use it.

---

### `connections`

```
sqlxl connections list
sqlxl connections remove <PROFILE>
```

---

### `insert`

```
sqlxl insert --table <T> [--output <PATH>] [--no-launch]              # generate template
sqlxl insert --table <T> --file <XLSX> [--no-launch]                  # import filled template
```

| Flag            | Description                                                |
|-----------------|------------------------------------------------------------|
| `--table`       | `dbo.Foo` or `Foo` (defaults to `dbo`)                     |
| `--file`        | Filled template to import. Omit to generate.               |
| `--output`      | Template output path (default: `<Table>_insert_YYYYMMDD.xlsx`) |
| `--no-launch`   | Don't auto-open Excel (USE THIS in agents/scripts)         |
| `--connection`  | Override profile                                           |
| `--profile`     | Override active profile                                    |

**Behavior:**
- If no `BulkOpFeature` exists for `<T>`, **auto-scaffolds one**:
  - Creates staging table `SqlXl.Staging_<Table>_ForInserts`
  - Creates sproc `<Table>_InsertFromStaging`
  - Inserts a `BulkOpFeatures` row of type `Insert`
- Generated templates **exclude IDENTITY/PK columns**.
- Templates have 3 sheets (see §5).

---

### `update`

```
sqlxl update --table <T> [--where "<CLAUSE>"] [--output <PATH>] [--no-launch]
sqlxl update --table <T> --file <XLSX> [--no-launch]
```

| Flag            | Description                                                |
|-----------------|------------------------------------------------------------|
| `--table`       | Target domain table                                        |
| `--where`       | SQL WHERE clause to filter rows for the template (TEMPLATE GENERATION ONLY — ignored on import) |
| `--file`        | Filled template to import                                  |
| `--output`      | Template output path (default: `<Table>_update_YYYYMMDD.xlsx`) |
| `--no-launch`   | Don't auto-open Excel                                      |

**Behavior:** Template is **pre-populated with existing rows** matching `--where`
(or all rows if omitted). User edits values in place; import applies updates
keyed by primary key.

---

### `import`

```
sqlxl import --feature <ID> [--output <PATH>] [--no-launch]
sqlxl import --feature <ID> --file <XLSX> [--no-launch]
```

| Flag            | Description                                                |
|-----------------|------------------------------------------------------------|
| `--feature`     | `BulkOpFeatures.ID` from `SqlXl.BulkOpFeatures`            |
| `--file`        | Filled template to import                                  |
| `--output`      | Default: `Feature_YYYYMMDD.xlsx`                           |
| `--no-launch`   | Don't auto-open Excel                                      |

**Behavior:** Generic entry point for any `BulkOpFeature` — `Insert`, `Update`,
`Delete`, or `Custom`. Use this when you want to drive a manually-authored
`Custom` feature, or invoke a specific feature ID rather than the default for
a table.

---

### `export`

```
sqlxl export --query "<SELECT>" [--output <PATH>] [--no-launch]
```

| Flag            | Description                                                |
|-----------------|------------------------------------------------------------|
| `--query`       | SELECT statement (validated via SqlXl infrastructure first)|
| `--output`      | Default: `export_YYYYMMDD_HHmmss.xlsx`                     |
| `--no-launch`   | Don't auto-open Excel                                      |

**Requires:** `sqlxl init` to have been run on the target DB.

**Output:** Single-sheet workbook with column headers in row 1, data below.

---

### `test`

```
sqlxl test --table <T> [--rows <N>]
```

| Flag            | Description                                                |
|-----------------|------------------------------------------------------------|
| `--table`       | Target table                                               |
| `--rows`        | Rows per feature (default: 1, max: 100)                    |

**Behavior:** For every `BulkOpFeature` configured against `<T>`, generates
synthetic rows and runs the full pipeline. Uniqueness-aware: respects unique
constraints against both existing data AND across the generated batch. Each
feature runs independently — one failing does not stop others.

**Note:** Test data is **committed to the database**. Run only against a
disposable DB.

**Common failure mode:** with small tables having narrow unique-column value
spaces, `--rows N` (N>1) often collides. Use `--rows 1` to verify pipeline,
larger N to stress-test.

---

### `infer`

```
sqlxl infer <INPUT> [OPTIONS]
```

| Flag                       | Description                                                              |
|----------------------------|--------------------------------------------------------------------------|
| `--table <NAME>`           | Target table name (default: sanitized file basename)                     |
| `--schema <NAME>`          | Target schema (default: `dbo`)                                           |
| `--sheet <NAME>`           | Worksheet to read (REQUIRED if workbook has multiple sheets)             |
| `--sample-size <N>`        | Rows to sample for type inference (default: 1000)                        |
| `--confidence-threshold <RATIO>` | Min valid-ratio for a type to be selected (default: 0.9)           |
| `--mode <MODE>`            | `permissive` (invalid → NULL) or `strict` (any invalid forces NVARCHAR)  |
| `--max-varchar <N>`        | Cap inferred NVARCHAR length (default: 255)                              |
| `--date-format <STYLE>`    | `us` (M/d/yyyy + ISO + long forms) or `iso` (ISO + long only). Default: `us` |
| `--output <PATH>`          | Write DDL to file (default: stdout)                                      |
| `--report <PATH>`          | Write JSON inference report                                              |

**Behavior:** Pure read operation. No DB connection needed. Pipeable —
DDL goes to stdout unless `--output` given.

---

### `demo`

```
sqlxl demo --connection <CONNSTR> [--yes]
```

**Drops and recreates `SqlXlDemo`** on the target server with sample data
(11 domain tables, 1 pre-configured `Custom` feature: "Assign User Roles").
`--yes` skips the confirmation prompt.

---

### `llm-context`

```
sqlxl llm-context [--format text|json] [--include-state]
```

Emits this reference document. Pass `--format json` for the machine-readable
version. Pass `--include-state` to also query the active DB for live state
(configured features, domain tables).

---

## 4. Two-phase template/import workflow

The `insert`, `update`, and `import` commands all follow the same shape:

```
PHASE 1 (generate):  omit --file       → produces .xlsx template
PHASE 2 (import):    pass --file <X>   → loads template into staging, then sproc
                                          processes valid rows into domain table
```

Round-trip example:

```pwsh
sqlxl insert --table dbo.Products --output template.xlsx --no-launch
# (human edits template.xlsx, closes Excel)
sqlxl insert --table dbo.Products --file template.xlsx --no-launch
```

---

## 5. Template structure (3-sheet contract)

Every generated template is a workbook with **exactly three sheets**:

| Sheet              | Purpose                                                                        |
|--------------------|--------------------------------------------------------------------------------|
| `Data`             | Fillable rows. Columns from `GetRowsToEdit_SelectStatement`                    |
| `DropdownOptions`  | FK / lookup values. Columns: `[ForColumn, OptionText]`. Format: `"<id> - <label>"` |
| `Metadata`         | Per-column schema. Columns: `[DbColumnName, ExcelColumnName, SqlDataType, IsPrimaryKey]` |

**Column-aliasing syntax** in `GetRowsToEdit_SelectStatement`:
`[DbCol|ExcelHeader]` — left side is the db column, right is the Excel header.

**FK dropdown format:** `"1 - Electronics"`, `"3 - Books"`. Friendly label
preserves the underlying ID — agents writing rows should emit values in this
exact format for FK columns.

**Identity/PK columns** are omitted from the `Data` sheet for `Insert` features
(but present in `Metadata` with `IsPrimaryKey=YES`).

---

## 6. BulkOpFeature schema

Definition lives in `SqlXl.BulkOpFeatures`:

| Column                                | Type            | Description                                                                |
|---------------------------------------|-----------------|----------------------------------------------------------------------------|
| `ID`                                  | int (PK)        | Feature ID — passed to `import --feature`                                  |
| `UserFriendlyFeatureName`             | nvarchar        | Display name                                                               |
| `InsertUpdateDeleteOrCustom`          | nvarchar        | `Insert` \| `Update` \| `Delete` \| `Custom`                               |
| `DomainSchemaName` / `DomainTableName`| nvarchar        | Target table                                                               |
| `StagingSchemaName` / `StagingTableName` | nvarchar     | Staging landing zone                                                       |
| `GetRowsToChooseFrom_SelectStatement` | nvarchar(max)   | Feeds the `DropdownOptions` sheet                                          |
| `GetRowsToEdit_SelectStatement`       | nvarchar(max)   | Defines the `Data` sheet shape (use `[Col\|Alias]` syntax)                 |
| `SprocToProcessPerfectStagedData`     | nvarchar        | Sproc that promotes validated staged rows → domain                         |
| `MenuDisplayRanking`                  | int             | UI ordering hint                                                           |

**Authoring a Custom feature:**

1. Create staging table `SqlXl.Staging_<Name>` with the columns the template
   should expose.
2. Create sproc that reads from staging and writes wherever needed.
3. Insert a row into `SqlXl.BulkOpFeatures` with `InsertUpdateDeleteOrCustom='Custom'`.
4. Drive it via `sqlxl import --feature <ID>`.

---

## 7. Agent-mode best practices

| Rule                                                                                | Why                                                                |
|-------------------------------------------------------------------------------------|--------------------------------------------------------------------|
| Always pass `--no-launch` on commands that produce .xlsx                             | Without it, Excel opens AND locks the file, breaking re-imports    |
| Before `--file` import, ensure file is closed in Excel                               | Tool errors out on locked file                                     |
| Parse `File: <path>` line from stdout to capture output paths                        | Use absolute paths printed by the tool, not your guess of cwd      |
| Exit code 0 = success, non-zero = failure                                            | Diagnostics are on stderr                                          |
| For destructive ops (`demo`), always pass `--yes`                                    | Confirmation prompt blocks non-TTY callers                         |
| Use `--output <abs-path>` rather than relying on default filename patterns           | Defaults change less than you'd think, but explicit is safer       |

---

## 8. Common workflows

### Round-trip insert (auto-scaffolded)

```pwsh
sqlxl insert --table dbo.Products --output t.xlsx --no-launch
# write rows into t.xlsx Data sheet (use FK format "id - label" for category columns)
sqlxl insert --table dbo.Products --file t.xlsx --no-launch
```

### Filtered update

```pwsh
sqlxl update --table dbo.Products --where "CategoryID = 1" --output u.xlsx --no-launch
# edit u.xlsx
sqlxl update --table dbo.Products --file u.xlsx --no-launch
```

### Custom feature import

```pwsh
sqlxl import --feature 1 --output assignments.xlsx --no-launch
# fill rows
sqlxl import --feature 1 --file assignments.xlsx --no-launch
```

### Infer DDL from spreadsheet, then build the table

```pwsh
sqlxl infer customers.xlsx --schema sales --table Customers --output ddl.sql
sqlcmd -S localhost -d MyDb -E -i ddl.sql
sqlxl init --connection "Server=localhost;Database=MyDb;Trusted_Connection=True;" --profile mydb
```

### Ad-hoc query export

```pwsh
sqlxl export --query "SELECT * FROM dbo.Orders WHERE OrderDate > '2026-01-01'" --output orders.xlsx --no-launch
```

---

## 9. Gotchas

- `update --where` applies only to **template generation**, not import. Import is keyed by PK.
- `init` is required before `export` — query validation depends on SqlXl infrastructure.
- `infer` writes DDL to **stdout** by default — pipe-friendly. `--output` to file.
- Tables can be `dbo.Foo` or `Foo` (assumes `dbo`).
- `test` commits data — never run against production.
- `test` with `--rows N>1` often collides on unique constraints in small tables; not a tool bug.
- Auto-scaffolding (on first `insert` for a table) creates DB objects as a side-effect — note this when running against shared databases.
- The `[Col|Alias]` aliasing syntax in `GetRowsToEdit_SelectStatement` is sqlxl-specific, not standard SQL.

---

## 10. Built-in `SqlXl.*` schema (created by `init`)

| Table                                 | Purpose                                                       |
|---------------------------------------|---------------------------------------------------------------|
| `SqlXl.BulkOpFeatures`                | Feature registry (see §6)                                     |
| `SqlXl.ColumnUIConfigurations`        | Per-column UI hints (display formatting, dropdown sources)    |
| `SqlXl.Meta_Columns`                  | Schema metadata cache                                         |
| `SqlXl.RequestContext`                | Per-request execution context (used during imports)           |
| `SqlXl.SavedQueries`                  | Persisted SELECT statements                                   |
| `SqlXl.DebugLog`                      | Diagnostic log written by sprocs                              |
| `SqlXl.Staging_<Name>`                | One per feature — landing zone for imports                    |

---

## 11. Quick reference (commands + key flags)

```
sqlxl init --connection <C> [--profile <N>]
sqlxl use <PROFILE>
sqlxl connections list | remove <PROFILE>

sqlxl insert --table <T> [--output <P>] [--no-launch]            # gen template (auto-scaffolds feature)
sqlxl insert --table <T> --file <X>   [--no-launch]              # import

sqlxl update --table <T> [--where <W>] [--output <P>] [--no-launch]
sqlxl update --table <T> --file <X>                [--no-launch]

sqlxl import --feature <ID> [--output <P>] [--no-launch]
sqlxl import --feature <ID> --file <X>     [--no-launch]

sqlxl export --query <SQL> [--output <P>] [--no-launch]

sqlxl test   --table <T> [--rows <N>]

sqlxl infer  <INPUT> [--table <N>] [--schema <S>] [--sheet <S>] [--sample-size <N>]
                     [--confidence-threshold <R>] [--mode permissive|strict]
                     [--max-varchar <N>] [--date-format us|iso]
                     [--output <P>] [--report <P>]

sqlxl demo   --connection <C> [--yes]

sqlxl llm-context [--format text|json] [--include-state]
```

---

## 12. Versioning

This document's structure is versioned. `format_version: 1` is the initial
contract. Breaking changes (removing/renaming sections, changing JSON shape
under `--format json`) require bumping `format_version`. Additive changes
(new commands, new flags, new gotchas appended) do not.

Agents parsing this output should:

1. Read `format_version` first.
2. Locate sections by their `## N. <title>` headers (numbered, stable order).
3. Treat tables as the source of truth for flag/column lists.
""";
}
