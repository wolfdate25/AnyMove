using System.Text.Json;

namespace AnyMove.Config;

internal enum ModifierKey
{
    Win,
    Alt,
}

internal sealed class AppSettings
{
    public ModifierKey Modifier { get; set; } = ModifierKey.Win;
    public bool Enabled { get; set; } = true;
    public bool Autostart { get; set; } = true;
}

internal static class SettingsStore
{
    private static string FilePath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AnyMove");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // 손상된 설정은 기본값으로 시작
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // 저장 실패는 무시 (상주 동작에 영향 없음)
        }
    }
}
