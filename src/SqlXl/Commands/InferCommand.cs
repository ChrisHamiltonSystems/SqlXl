using System.ComponentModel;
using System.Text.RegularExpressions;
using Spectre.Console.Cli;
using SqlXl.Core.SchemaInference;

namespace SqlXl.Commands;

public class InferCommand : Command<InferCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<INPUT>")]
        [Description("Path to the .xlsx file to infer schema from")]
        public string InputPath { get; set; } = string.Empty;

        [CommandOption("--table <NAME>")]
        [Description("Target table name (default: sanitized file basename)")]
        public string Table { get; set; } = string.Empty;

        [CommandOption("--schema <NAME>")]
        [Description("Target schema name (default: dbo)")]
        public string Schema { get; set; } = "dbo";

        [CommandOption("--sheet <NAME>")]
        [Description("Worksheet to read (required when workbook has more than one sheet)")]
        public string Sheet { get; set; } = string.Empty;

        [CommandOption("--sample-size <N>")]
        [Description("Number of data rows to sample (default: 1000)")]
        public int SampleSize { get; set; } = 1000;

        [CommandOption("--confidence-threshold <RATIO>")]
        [Description("Min valid-ratio for a type to be selected (default: 0.9)")]
        public double ConfidenceThreshold { get; set; } = 0.9;

        [CommandOption("--mode <MODE>")]
        [Description("permissive (invalid → NULL) or strict (any invalid forces NVARCHAR fallback)")]
        public string Mode { get; set; } = "permissive";

        [CommandOption("--max-varchar <N>")]
        [Description("Cap for inferred NVARCHAR length (default: 255)")]
        public int MaxVarchar { get; set; } = 255;

        [CommandOption("--date-format <STYLE>")]
        [Description("us (M/d/yyyy + ISO + long forms) or iso (ISO + long forms only). Default: us")]
        public string DateFormat { get; set; } = "us";

        [CommandOption("--output <PATH>")]
        [Description("Write generated DDL to a file (default: stdout)")]
        public string OutputPath { get; set; } = string.Empty;

        [CommandOption("--report <PATH>")]
        [Description("Write a JSON inference report to this path (optional)")]
        public string ReportPath { get; set; } = string.Empty;

        public override Spectre.Console.ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(InputPath))
                return Spectre.Console.ValidationResult.Error("input file path is required");
            if (!File.Exists(InputPath))
                return Spectre.Console.ValidationResult.Error($"File not found: {InputPath}");
            if (SampleSize < 1)
                return Spectre.Console.ValidationResult.Error("--sample-size must be >= 1");
            if (ConfidenceThreshold <= 0 || ConfidenceThreshold > 1)
                return Spectre.Console.ValidationResult.Error("--confidence-threshold must be in (0, 1]");
            if (MaxVarchar < 1 || MaxVarchar > 4000)
                return Spectre.Console.ValidationResult.Error("--max-varchar must be in [1, 4000]");
            return Spectre.Console.ValidationResult.Success();
        }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        try
        {
            var options = BuildOptions(settings);
            var table = string.IsNullOrWhiteSpace(settings.Table)
                ? DefaultTableName(settings.InputPath)
                : settings.Table.Trim();
            var schema = string.IsNullOrWhiteSpace(settings.Schema) ? "dbo" : settings.Schema.Trim();

            using var reader = new ExcelTabularReader(settings.InputPath);
            var sheetName = ResolveSheet(reader, settings.Sheet);

            Console.Error.WriteLine($"Reading sheet '{sheetName}' from {settings.InputPath}...");

            var data = reader.Read(sheetName, options.SampleSize);
            if (data.Headers.Count == 0)
            {
                Console.Error.WriteLine("Error: no columns detected (empty header row)");
                return 1;
            }
            if (data.Rows.Count == 0)
            {
                Console.Error.WriteLine("Error: no data rows found");
                return 1;
            }

            Console.Error.WriteLine($"Sampled {data.Rows.Count} of {data.TotalDataRowsScanned} non-empty rows; {data.Headers.Count} columns");

            var result = SchemaInferrer.Infer(data, options);
            var ddl = DdlEmitter.Emit(result, schema, table);

            EmitWarnings(result);

            if (!string.IsNullOrWhiteSpace(settings.OutputPath))
            {
                File.WriteAllText(settings.OutputPath, ddl);
                Console.Error.WriteLine($"DDL written to {Path.GetFullPath(settings.OutputPath)}");
            }
            else
            {
                Console.Out.Write(ddl);
                Console.Out.Flush();
            }

            if (!string.IsNullOrWhiteSpace(settings.ReportPath))
            {
                File.WriteAllText(settings.ReportPath, InferenceReport.ToJson(result));
                Console.Error.WriteLine($"Report written to {Path.GetFullPath(settings.ReportPath)}");
            }

            EmitNextSteps(settings, schema, table);
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
    }

    private static InferenceOptions BuildOptions(Settings s)
    {
        var mode = (s.Mode ?? "").Trim().ToLowerInvariant() switch
        {
            "strict"     => InferenceMode.Strict,
            "permissive" => InferenceMode.Permissive,
            ""           => InferenceMode.Permissive,
            _ => throw new ArgumentException($"--mode must be 'strict' or 'permissive' (got '{s.Mode}')")
        };

        var dateFormat = (s.DateFormat ?? "").Trim().ToLowerInvariant() switch
        {
            "us"  => DateFormatStyle.Us,
            "iso" => DateFormatStyle.Iso,
            ""    => DateFormatStyle.Us,
            _ => throw new ArgumentException($"--date-format must be 'us' or 'iso' (got '{s.DateFormat}')")
        };

        return new InferenceOptions
        {
            SampleSize = s.SampleSize,
            ConfidenceThreshold = s.ConfidenceThreshold,
            MaxVarchar = s.MaxVarchar,
            Mode = mode,
            DateFormat = dateFormat
        };
    }

    private static string ResolveSheet(ExcelTabularReader reader, string explicitSheet)
    {
        var sheets = reader.ListSheets();
        if (sheets.Count == 0)
            throw new InvalidOperationException("Workbook has no worksheets");

        if (!string.IsNullOrWhiteSpace(explicitSheet))
        {
            if (!reader.SheetExists(explicitSheet))
            {
                var list = string.Join("\n  ", sheets.Select(FormatSheetName));
                throw new ArgumentException(
                    $"Sheet '{explicitSheet}' not found. Available sheets:\n  {list}");
            }
            return explicitSheet;
        }

        if (sheets.Count == 1)
            return sheets[0].Name;

        var listAll = string.Join("\n  ", sheets.Select(FormatSheetName));
        throw new ArgumentException(
            $"Workbook has {sheets.Count} sheets. Specify one with --sheet:\n  {listAll}");
    }

    private static string FormatSheetName(SheetInfo s) =>
        s.IsHidden ? $"{s.Name} (hidden)" : s.Name;

    private static string DefaultTableName(string filePath)
    {
        var raw = Path.GetFileNameWithoutExtension(filePath) ?? "";
        var sanitized = Regex.Replace(raw, @"[^A-Za-z0-9_]", "_").Trim('_');
        if (sanitized.Length == 0)
            sanitized = "InferredTable";
        if (char.IsDigit(sanitized[0]))
            sanitized = "T_" + sanitized;
        return sanitized;
    }

    private static void EmitNextSteps(Settings settings, string schema, string table)
    {
        bool toFile = !string.IsNullOrWhiteSpace(settings.OutputPath);
        string ddlFile = toFile ? settings.OutputPath : "<your-ddl-file.sql>";
        string reviewLine = toFile
            ? $"Review the DDL in {settings.OutputPath}; edit if needed (PKs, indexes, type tightening)."
            : "Review the DDL above; edit if needed (PKs, indexes, type tightening).";

        Console.Error.WriteLine();
        Console.Error.WriteLine("Next steps:");
        Console.Error.WriteLine($"  1. {reviewLine}");
        Console.Error.WriteLine($"  2. Apply it:   sqlcmd -S <server> -d <database> -E -i {ddlFile}");
        Console.Error.WriteLine($"  3. Load data: sqlxl insert --table {schema}.{table} --file {settings.InputPath}");
        Console.Error.WriteLine();
    }

    private static void EmitWarnings(InferenceResult result)
    {
        bool any = false;
        foreach (var col in result.Columns)
        {
            foreach (var w in col.Warnings)
            {
                if (!any)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Inference notes (review before running DDL):");
                    any = true;
                }
                Console.Error.WriteLine($"  [{col.ColumnName}] {w}");
            }
        }
        if (any) Console.Error.WriteLine();
    }
}
