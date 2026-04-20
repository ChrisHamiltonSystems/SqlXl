using System.ComponentModel;
using Spectre.Console.Cli;
using SqlXl.Config;

namespace SqlXl.Commands;

/// <summary>
/// Base settings class for all commands that connect to SQL Server.
/// Provides --connection and --profile flags with automatic resolution logic.
/// </summary>
public abstract class ConnectionSettings : CommandSettings
{
    [CommandOption("--connection <CONNSTR>")]
    [Description("SQL Server connection string (overrides saved profile)")]
    public string ExplicitConnection { get; set; }

    [CommandOption("--profile <NAME>")]
    [Description("Named connection profile to use (overrides active profile)")]
    public string Profile { get; set; }

    public string ResolveConnection() => ConnectionResolver.Resolve(ExplicitConnection, Profile);
}
