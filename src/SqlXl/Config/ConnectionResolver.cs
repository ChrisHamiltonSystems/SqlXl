using Microsoft.Data.SqlClient;
using Spectre.Console;

namespace SqlXl.Config;

public static class ConnectionResolver
{
    public static string Resolve(string explicitConnection, string profileOverride = null)
    {
        // 1. --connection flag (always wins)
        if (!string.IsNullOrWhiteSpace(explicitConnection))
            return explicitConnection;

        // 2. SQLXL_CONNECTION environment variable (useful for CI / scripting)
        var envConn = Environment.GetEnvironmentVariable("SQLXL_CONNECTION");
        if (!string.IsNullOrWhiteSpace(envConn))
            return envConn;

        var config = SqlXlConfig.Load();

        // 3. --profile flag (named override without switching the active profile)
        if (!string.IsNullOrWhiteSpace(profileOverride))
        {
            var connStr = config.GetConnectionString(profileOverride);
            PrintProfileLine(profileOverride, connStr);
            return connStr;
        }

        // 4. Active profile from config
        if (!string.IsNullOrWhiteSpace(config.ActiveProfile) && config.Profiles.Count > 0)
        {
            var connStr = config.GetConnectionString(config.ActiveProfile);
            PrintProfileLine(config.ActiveProfile, connStr);

            // Warn if a non-encrypted profile contains a plaintext password
            if (config.Profiles.TryGetValue(config.ActiveProfile, out var entry)
                && !entry.Encrypted && SqlXlConfig.HasPassword(connStr))
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Warning:[/] Profile '[cyan]{Markup.Escape(config.ActiveProfile)}[/]' contains unencrypted credentials. " +
                    $"Run `sqlxl init --connection \"...\" --profile {Markup.Escape(config.ActiveProfile)}` to encrypt them.");
                AnsiConsole.WriteLine();
            }

            return connStr;
        }

        // 5. Nothing configured — fail with an actionable message
        throw new InvalidOperationException(
            "No connection configured.\n" +
            "Run: sqlxl init --connection \"Server=myserver;Database=MyDB;Integrated Security=true;TrustServerCertificate=true;\"");
    }

    private static void PrintProfileLine(string profileName, string connStr)
    {
        try
        {
            var b = new SqlConnectionStringBuilder(connStr);
            AnsiConsole.MarkupLine($"[grey]Profile: {Markup.Escape(profileName)} → {Markup.Escape(b.DataSource)} / {Markup.Escape(b.InitialCatalog)}[/]");
        }
        catch
        {
            AnsiConsole.MarkupLine($"[grey]Profile: {Markup.Escape(profileName)}[/]");
        }
    }
}
