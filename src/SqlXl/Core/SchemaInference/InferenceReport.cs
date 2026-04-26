using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlXl.Core.SchemaInference;

public static class InferenceReport
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    public static string ToJson(InferenceResult result)
    {
        var dto = new ReportDto
        {
            Columns = result.Columns.Select(c => new ColumnDto
            {
                Name          = c.ColumnName,
                InferredType  = c.SqlType,
                IsNullable    = c.IsNullable,
                ValidRatio    = Math.Round(c.ValidRatio, 4),
                NullRatio     = Math.Round(c.NullRatio, 4),
                InvalidCount  = c.InvalidCount,
                SampleSize    = c.SampleSize,
                Warnings      = c.Warnings.Count > 0 ? c.Warnings : null
            }).ToList()
        };

        return JsonSerializer.Serialize(dto, SerializerOptions);
    }

    private class ReportDto
    {
        [JsonPropertyName("columns")]
        public List<ColumnDto> Columns { get; set; }
    }

    private class ColumnDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("inferred_type")]
        public string InferredType { get; set; }

        [JsonPropertyName("is_nullable")]
        public bool IsNullable { get; set; }

        [JsonPropertyName("valid_ratio")]
        public double ValidRatio { get; set; }

        [JsonPropertyName("null_ratio")]
        public double NullRatio { get; set; }

        [JsonPropertyName("invalid_count")]
        public int InvalidCount { get; set; }

        [JsonPropertyName("sample_size")]
        public int SampleSize { get; set; }

        [JsonPropertyName("warnings")]
        public List<string> Warnings { get; set; }
    }
}
