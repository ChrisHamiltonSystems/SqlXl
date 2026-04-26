namespace SqlXl.Config;

/// <summary>
/// Resolves the path to the SqlXL config file. Resolution order:
///   1. --config &lt;path&gt; flag (extracted from args at startup)
///   2. SQLXL_CONFIG environment variable
///   3. ~/.sqlxl/config.json (default)
/// </summary>
public static class ConfigLocator
{
    private static string _overridePath;

    public static string ResolvePath()
    {
        if (!string.IsNullOrWhiteSpace(_overridePath))
            return Path.GetFullPath(_overridePath);

        var env = Environment.GetEnvironmentVariable("SQLXL_CONFIG");
        if (!string.IsNullOrWhiteSpace(env))
            return Path.GetFullPath(env);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".sqlxl", "config.json");
    }

    public static void SetOverride(string path) => _overridePath = path;

    /// <summary>
    /// Strips "--config &lt;path&gt;" or "--config=&lt;path&gt;" from args.
    /// Last occurrence wins. Throws ArgumentException if --config is supplied without a value.
    /// </summary>
    public static (string[] cleanedArgs, string overridePath) ExtractFromArgs(string[] args)
    {
        var cleaned = new List<string>(args.Length);
        string overridePath = null;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg == "--config")
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException(
                        "--config requires a path argument, e.g. --config /path/to/config.json");
                overridePath = args[i + 1];
                i++;
                continue;
            }

            const string prefix = "--config=";
            if (arg.StartsWith(prefix, StringComparison.Ordinal))
            {
                var value = arg.Substring(prefix.Length);
                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException(
                        "--config requires a path argument, e.g. --config=/path/to/config.json");
                overridePath = value;
                continue;
            }

            cleaned.Add(arg);
        }

        return (cleaned.ToArray(), overridePath);
    }
}
