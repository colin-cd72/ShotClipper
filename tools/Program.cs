using Microsoft.Data.Sqlite;

var dbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Screener", "screener.db");

Console.WriteLine($"Database: {dbPath}");

using var conn = new SqliteConnection($"Data Source={dbPath}");
conn.Open();

// Upsert panel settings
// Values must be valid JSON (SettingsRepository uses JsonSerializer.Deserialize)
var settings = new Dictionary<string, string>
{
    ["panel.enabled"] = "true",
    ["panel.url"] = "\"https://shotclipper.4tmrw.net\"",
    ["panel.apiKey"] = "\"shotclipper2026\""
};

foreach (var (key, value) in settings)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT INTO settings (key, value, updated_at) VALUES ($key, $value, datetime('now')) ON CONFLICT(key) DO UPDATE SET value = $value, updated_at = datetime('now')";
    cmd.Parameters.AddWithValue("$key", key);
    cmd.Parameters.AddWithValue("$value", value);
    cmd.ExecuteNonQuery();
    Console.WriteLine($"  Set {key} = {value}");
}

// Verify
using var readCmd = conn.CreateCommand();
readCmd.CommandText = "SELECT key, value FROM settings WHERE key LIKE 'panel%'";
using var reader = readCmd.ExecuteReader();
Console.WriteLine("\nVerification:");
while (reader.Read())
    Console.WriteLine($"  {reader.GetString(0)} = {reader.GetString(1)}");
