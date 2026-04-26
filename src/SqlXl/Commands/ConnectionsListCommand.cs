using Microsoft.Data.SqlClient;
using Spectre.Console;
using Spectre.Console.Cli;
using SqlXl.Config;

namespace SqlXl.Commands;

public class ConnectionsListCommand : Command<ConnectionsListCommand.Settings>
{
    public class Settings : CommandSettings { }

    public override int Execute(CommandContext context, Settings settings)
    {
        AnsiConsole.WriteLine();

        SqlXlConfig config;
        try
        {
            config = SqlXlConfig.Load();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.WriteLine();
            return 1;
        }

        AnsiConsole.MarkupLine($"Config: [grey]{Markup.Escape(SqlXlConfig.ConfigFilePath)}[/]");
        AnsiConsole.WriteLine();

        if (config.Profiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No profiles configured.[/]");
            AnsiConsole.MarkupLine("Run: [cyan]sqlxl init --connection \"Server=myserver;Database=MyDB;Integrated Security=true;TrustServerCertificate=true;\"[/]");
            AnsiConsole.WriteLine();
            return 0;
        }

        foreach (var (name, entry) in config.Profiles)
        {
            bool isActive = name == config.ActiveProfile;
            string marker = isActive ? "[green]*[/] " : "  ";
            string nameLabel = isActive
                ? $"[green]{Markup.Escape(name)}[/]"
                : $"[cyan]{Markup.Escape(name)}[/]";

            string connInfo;
            if (entry.Encrypted)
            {
                try
                {
                    var builder = new SqlConnectionStringBuilder(config.GetConnectionString(name));
                    connInfo = $"{Markup.Escape(builder.DataSource)} / {Markup.Escape(builder.InitialCatalog)} [grey](SQL Auth, encrypted)[/]";
                }
                catch
                {
                    connInfo = "[red](encrypted — could not decrypt, profile may be corrupt)[/]";
                }
            }
            else
            {
                try
                {
                    var builder = new SqlConnectionStringBuilder(entry.ConnectionString);
                    connInfo = $"{Markup.Escape(builder.DataSource)} / {Markup.Escape(builder.InitialCatalog)} [grey](Windows Auth)[/]";

                    if (SqlXlConfig.HasPassword(entry.ConnectionString))
                        connInfo += " [yellow](unencrypted password — run `sqlxl init` again to encrypt)[/]";
                }
                catch
                {
                    connInfo = Markup.Escape(entry.ConnectionString);
                }
            }

            AnsiConsole.MarkupLine($"{marker}{nameLabel}  →  {connInfo}");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]* = active profile   |   sqlxl use <profile> to switch[/]");
        AnsiConsole.WriteLine();
        return 0;
    }
}
