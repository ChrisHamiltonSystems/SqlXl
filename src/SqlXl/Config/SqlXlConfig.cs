using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace SqlXl.Config;

public class SqlXlConfig
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".sqlxl", "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [JsonPropertyName("activeProfile")]
    public string ActiveProfile { get; set; } = string.Empty;

    [JsonPropertyName("profiles")]
    public Dictionary<string, ProfileEntry> Profiles { get; set; } = new();

    public static string ConfigFilePath => ConfigPath;

    public static SqlXlConfig Load()
    {
        if (!File.Exists(ConfigPath))
            return new SqlXlConfig();

        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<SqlXlConfig>(json, JsonOptions) ?? new SqlXlConfig();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Config file is malformed ({ConfigPath}): {ex.Message}\n" +
                "Delete or fix it manually, then re-run `sqlxl init`.");
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
    }

    [SupportedOSPlatform("windows")]
    public string GetConnectionString(string profileName)
    {
        if (!Profiles.TryGetValue(profileName, out var entry))
            throw new InvalidOperationException(
                $"Profile '{profileName}' not found. Run `sqlxl connections list` to see available profiles.");

        if (!entry.Encrypted)
            return entry.ConnectionString;

        try
        {
            var blob = Convert.FromBase64String(entry.ConnectionString);
            var decrypted = ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException(
                $"Profile '{profileName}' could not be decrypted — it may have been modified outside of sqlxl.\n" +
                $"Fix: sqlxl init --connection \"...\" --profile {profileName}");
        }
    }

    // Returns (wasEncrypted, isNew)
    [SupportedOSPlatform("windows")]
    public (bool wasEncrypted, bool isNew) SetProfile(string profileName, string connectionString)
    {
        bool isNew = !Profiles.ContainsKey(profileName);
        bool hasPassword = HasPassword(connectionString);

        string stored;
        if (hasPassword)
        {
            var bytes = Encoding.UTF8.GetBytes(connectionString);
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            stored = Convert.ToBase64String(encrypted);
        }
        else
        {
            stored = connectionString;
        }

        Profiles[profileName] = new ProfileEntry { ConnectionString = stored, Encrypted = hasPassword };
        return (hasPassword, isNew);
    }

    public void RemoveProfile(string profileName)
    {
        if (!Profiles.ContainsKey(profileName))
            throw new InvalidOperationException(
                $"Profile '{profileName}' not found. Run `sqlxl connections list` to see available profiles.");

        Profiles.Remove(profileName);

        // If the removed profile was active, switch to another if one exists
        if (ActiveProfile == profileName)
            ActiveProfile = Profiles.Keys.FirstOrDefault() ?? string.Empty;
    }

    public void SetActiveProfile(string profileName)
    {
        if (!Profiles.ContainsKey(profileName))
            throw new InvalidOperationException(
                $"Profile '{profileName}' not found. Run `sqlxl connections list` to see available profiles.");
        ActiveProfile = profileName;
    }

    public static bool HasPassword(string connectionString)
    {
        try
        {
            return !string.IsNullOrEmpty(new SqlConnectionStringBuilder(connectionString).Password);
        }
        catch
        {
            return false;
        }
    }
}

public class ProfileEntry
{
    [JsonPropertyName("connectionString")]
    public string ConnectionString { get; set; } = string.Empty;

    [JsonPropertyName("encrypted")]
    public bool Encrypted { get; set; }
}
