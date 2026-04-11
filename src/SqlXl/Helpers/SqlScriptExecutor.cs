using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SqlXl.Helpers;

public static class SqlScriptExecutor
{
    /// <summary>
    /// Loads an embedded SQL script and executes it against the given connection string,
    /// splitting on GO batch separators as SQL Server requires.
    /// </summary>
    public static void ExecuteEmbeddedScript(string resourceName, string connectionString)
    {
        string sql = LoadEmbeddedSql(resourceName);
        ExecuteScript(sql, connectionString);
    }

    public static void ExecuteScript(string sql, string connectionString)
    {
        string[] batches = SplitOnGo(sql);

        using var conn = new SqlConnection(connectionString);
        conn.Open();

        foreach (string batch in batches)
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            using var cmd = new SqlCommand(batch, conn);
            cmd.CommandTimeout = 120;
            cmd.ExecuteNonQuery();
        }
    }

    public static string LoadEmbeddedSql(string resourceName)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string[] SplitOnGo(string sql) =>
        Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
}
