using System.Text;

namespace SqlXl.Core.SchemaInference;

public static class DdlEmitter
{
    public static string Emit(InferenceResult result, string schema, string table)
    {
        var sb = new StringBuilder();
        sb.Append("CREATE TABLE [")
          .Append(BracketEscape(schema))
          .Append("].[")
          .Append(BracketEscape(table))
          .AppendLine("] (");

        for (int i = 0; i < result.Columns.Count; i++)
        {
            var col = result.Columns[i];
            var name = string.IsNullOrWhiteSpace(col.ColumnName)
                ? $"Column{i + 1}"
                : col.ColumnName;

            sb.Append("    [")
              .Append(BracketEscape(name))
              .Append("] ")
              .Append(col.SqlType)
              .Append(' ')
              .Append(col.IsNullable ? "NULL" : "NOT NULL");

            if (i < result.Columns.Count - 1)
                sb.Append(',');
            sb.AppendLine();
        }

        sb.AppendLine(");");
        return sb.ToString();
    }

    private static string BracketEscape(string s) => (s ?? string.Empty).Replace("]", "]]");
}
