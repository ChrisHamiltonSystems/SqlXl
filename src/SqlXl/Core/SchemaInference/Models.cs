namespace SqlXl.Core.SchemaInference;

public enum InferenceMode
{
    Permissive,
    Strict
}

public enum DateFormatStyle
{
    Us,
    Iso
}

public enum InferredType
{
    Bit,
    Int,
    BigInt,
    Decimal,
    Float,
    DateTime2,
    NVarchar,
    NVarcharMax
}

public class InferenceOptions
{
    public int SampleSize { get; set; } = 1000;
    public double ConfidenceThreshold { get; set; } = 0.9;
    public int MaxVarchar { get; set; } = 255;
    public InferenceMode Mode { get; set; } = InferenceMode.Permissive;
    public DateFormatStyle DateFormat { get; set; } = DateFormatStyle.Us;
}

public class ColumnInference
{
    public string ColumnName { get; set; }
    public InferredType InferredType { get; set; }
    public bool IsNullable { get; set; }

    public int NVarcharLength { get; set; }
    public int DecimalPrecision { get; set; }
    public int DecimalScale { get; set; }

    public int SampleSize { get; set; }
    public int NullCount { get; set; }
    public int InvalidCount { get; set; }
    public double ValidRatio { get; set; }
    public double NullRatio { get; set; }

    public List<string> Warnings { get; set; } = new List<string>();

    public string SqlType => SqlTypeFormatter.Format(this);
}

public class InferenceResult
{
    public List<ColumnInference> Columns { get; set; } = new List<ColumnInference>();
}

internal static class SqlTypeFormatter
{
    public static string Format(ColumnInference c) => c.InferredType switch
    {
        InferredType.Bit         => "BIT",
        InferredType.Int         => "INT",
        InferredType.BigInt      => "BIGINT",
        InferredType.Decimal     => $"DECIMAL({c.DecimalPrecision},{c.DecimalScale})",
        InferredType.Float       => "FLOAT",
        InferredType.DateTime2   => "DATETIME2",
        InferredType.NVarchar    => $"NVARCHAR({c.NVarcharLength})",
        InferredType.NVarcharMax => "NVARCHAR(MAX)",
        _ => "NVARCHAR(255)"
    };
}
