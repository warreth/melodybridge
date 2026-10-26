namespace MelodyBridge.Server.Services;

/// <summary>
/// The one stored switch behind every "advanced" surface: the extra
/// Settings fields, the playlist import options and the maintenance page.
/// Kept as a plain setting so a restart reads the same value everywhere.
/// </summary>
public static class AdvancedMode
{
    public const string SettingKey = "advanced_ui";
}
