using System.Globalization;

namespace SqlXl.Core.SchemaInference;

public static class SchemaInferrer
{
    private const int SqlServerMaxDecimalPrecision = 38;

    private static readonly HashSet<string> NullTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "null", "n/a", "-"
    };

    public static InferenceResult Infer(TabularData data, InferenceOptions options)
    {
        var result = new InferenceResult();

        for (int colIdx = 0; colIdx < data.Headers.Count; colIdx++)
        {
            var col = AnalyzeColumn(data.Headers[colIdx], colIdx, data, options);
            result.Columns.Add(col);
        }

        DetectDuplicateNames(result);
        return result;
    }

    private static ColumnInference AnalyzeColumn(string header, int idx, TabularData data, InferenceOptions opts)
    {
        var acc = new ColumnAccumulator { Header = header };

        int sampleCap = Math.Min(data.Rows.Count, opts.SampleSize);
        for (int r = 0; r < sampleCap; r++)
        {
            ObserveValue(data.Rows[r][idx], acc, opts.DateFormat);
        }
        acc.Total = sampleCap;

        return SelectType(acc, opts);
    }

    private static void ObserveValue(object raw, ColumnAccumulator acc, DateFormatStyle dateStyle)
    {
        if (IsNullValue(raw))
        {
            acc.NullCount++;
            return;
        }

        var stringForm = StringifyForLength(raw);
        if (stringForm.Length > acc.MaxStringLength)
            acc.MaxStringLength = stringForm.Length;

        if (TypeEvaluators.TryParseBit(raw))     acc.BitValid++;
        if (TypeEvaluators.TryParseInt(raw))     acc.IntValid++;
        if (TypeEvaluators.TryParseBigInt(raw))  acc.BigIntValid++;
        if (TypeEvaluators.TryParseDecimal(raw, out int intDigits, out int fracDigits))
        {
            acc.DecimalValid++;
            if (intDigits  > acc.DecimalMaxIntDigits)  acc.DecimalMaxIntDigits  = intDigits;
            if (fracDigits > acc.DecimalMaxFracDigits) acc.DecimalMaxFracDigits = fracDigits;
        }
        if (TypeEvaluators.TryParseFloat(raw)) acc.FloatValid++;
        if (TypeEvaluators.TryParseDateTime2(raw, dateStyle)) acc.DateTime2Valid++;
    }

    private static bool IsNullValue(object raw)
    {
        if (raw == null) return true;
        if (raw is string s)
        {
            var t = s.Trim();
            if (t.Length == 0) return true;
            if (NullTokens.Contains(t)) return true;
        }
        return false;
    }

    private static string StringifyForLength(object raw)
    {
        if (raw == null) return "";
        if (raw is string s)   return s.Trim();
        if (raw is DateTime dt) return dt.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);
        if (raw is double d)    return d.ToString("R", CultureInfo.InvariantCulture);
        if (raw is bool b)      return b ? "True" : "False";
        return Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "";
    }

    private static ColumnInference SelectType(ColumnAccumulator acc, InferenceOptions opts)
    {
        int nonNull = acc.Total - acc.NullCount;
        var col = new ColumnInference
        {
            ColumnName = acc.Header,
            SampleSize = acc.Total,
            NullCount = acc.NullCount,
            IsNullable = acc.NullCount > 0,
            NullRatio = acc.Total > 0 ? (double)acc.NullCount / acc.Total : 0
        };

        if (string.IsNullOrWhiteSpace(col.ColumnName))
            col.Warnings.Add("Column has no header — rename in Excel before importing");

        if (nonNull == 0)
        {
            col.InferredType = InferredType.NVarchar;
            col.NVarcharLength = 255;
            col.IsNullable = true;
            col.ValidRatio = 0;
            col.Warnings.Add("All sampled values are null — defaulting to NVARCHAR(255)");
            return col;
        }

        double threshold = opts.Mode == InferenceMode.Strict ? 1.0 : opts.ConfidenceThreshold;
        double Ratio(int valid) => (double)valid / nonNull;

        int decimalPrecision = acc.DecimalMaxIntDigits + acc.DecimalMaxFracDigits;
        bool decimalFitsInSqlServer = decimalPrecision <= SqlServerMaxDecimalPrecision;

        if (Ratio(acc.BitValid) >= threshold)
        {
            col.InferredType = InferredType.Bit;
            col.ValidRatio   = Ratio(acc.BitValid);
            col.InvalidCount = nonNull - acc.BitValid;
        }
        else if (Ratio(acc.IntValid) >= threshold)
        {
            col.InferredType = InferredType.Int;
            col.ValidRatio   = Ratio(acc.IntValid);
            col.InvalidCount = nonNull - acc.IntValid;
        }
        else if (Ratio(acc.BigIntValid) >= threshold)
        {
            col.InferredType = InferredType.BigInt;
            col.ValidRatio   = Ratio(acc.BigIntValid);
            col.InvalidCount = nonNull - acc.BigIntValid;
        }
        else if (Ratio(acc.DecimalValid) >= threshold && decimalFitsInSqlServer)
        {
            col.InferredType      = InferredType.Decimal;
            col.ValidRatio        = Ratio(acc.DecimalValid);
            col.InvalidCount      = nonNull - acc.DecimalValid;
            col.DecimalPrecision  = Math.Max(decimalPrecision, 1);
            col.DecimalScale      = acc.DecimalMaxFracDigits;
        }
        else if (Ratio(acc.FloatValid) >= threshold)
        {
            col.InferredType = InferredType.Float;
            col.ValidRatio   = Ratio(acc.FloatValid);
            col.InvalidCount = nonNull - acc.FloatValid;
            if (Ratio(acc.DecimalValid) >= threshold && !decimalFitsInSqlServer)
                col.Warnings.Add($"DECIMAL precision {decimalPrecision} exceeds SQL Server max ({SqlServerMaxDecimalPrecision}) — falling back to FLOAT");
        }
        else if (Ratio(acc.DateTime2Valid) >= threshold)
        {
            col.InferredType = InferredType.DateTime2;
            col.ValidRatio   = Ratio(acc.DateTime2Valid);
            col.InvalidCount = nonNull - acc.DateTime2Valid;
        }
        else
        {
            ApplyNVarchar(col, acc, opts);
        }

        if (opts.Mode == InferenceMode.Permissive
            && col.InvalidCount > 0
            && col.InferredType != InferredType.NVarchar
            && col.InferredType != InferredType.NVarcharMax)
        {
            col.Warnings.Add($"{col.InvalidCount} value(s) did not parse as {col.InferredType.ToString().ToUpperInvariant()} — would become NULL on import (permissive mode); marking column NULLABLE");
            col.IsNullable = true;
        }

        return col;
    }

    private static void ApplyNVarchar(ColumnInference col, ColumnAccumulator acc, InferenceOptions opts)
    {
        col.ValidRatio   = 1.0;
        col.InvalidCount = 0;

        if (acc.MaxStringLength > 4000)
        {
            col.InferredType = InferredType.NVarcharMax;
        }
        else
        {
            col.InferredType   = InferredType.NVarchar;
            col.NVarcharLength = Math.Max(1, Math.Min(acc.MaxStringLength, opts.MaxVarchar));
        }
    }

    private static void DetectDuplicateNames(InferenceResult result)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in result.Columns)
        {
            if (string.IsNullOrWhiteSpace(col.ColumnName)) continue;
            if (seen.TryGetValue(col.ColumnName, out _))
                col.Warnings.Add($"Duplicate column name '{col.ColumnName}' — SQL Server will reject this DDL until renamed");
            seen[col.ColumnName] = 1;
        }
    }

    private class ColumnAccumulator
    {
        public string Header;
        public int Total;
        public int NullCount;
        public int BitValid;
        public int IntValid;
        public int BigIntValid;
        public int DecimalValid;
        public int DecimalMaxIntDigits;
        public int DecimalMaxFracDigits;
        public int FloatValid;
        public int DateTime2Valid;
        public int MaxStringLength;
    }
}
