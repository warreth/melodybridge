using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Server.Services;

/// <summary>
/// The app-wide interface mode. Existing databases default to Advanced so
/// an upgrade never hides tools; a first install starts Simple until the
/// user picks a mode on the welcome page.
/// </summary>
public sealed class UiModeService
{
    public const string SettingKey = "ui_mode";
    public const string Simple = "simple";
    public const string Advanced = "advanced";

    private readonly SettingsStore _settings;
    private readonly InstallState _install;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private bool? _isAdvanced;

    public UiModeService(SettingsStore settings, InstallState install)
    {
        _settings = settings;
        _install = install;
    }

    public event Action? Changed;

    public bool IsAdvanced => _isAdvanced ?? _install.DatabaseExistedAtStartup;

    public bool HasChosen { get; private set; }

    public async Task EnsureLoadedAsync()
    {
        if (_isAdvanced is not null) return;
        await _loadGate.WaitAsync();
        try
        {
            if (_isAdvanced is not null) return;
            var raw = await _settings.GetAsync(SettingKey, "");
            HasChosen = raw is Simple or Advanced;
            _isAdvanced = raw switch
            {
                Advanced => true,
                Simple => false,
                _ => _install.DatabaseExistedAtStartup,
            };
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public async Task SetAsync(bool advanced)
    {
        await _settings.SetAsync(SettingKey, advanced ? Advanced : Simple);
        _isAdvanced = advanced;
        HasChosen = true;
        Changed?.Invoke();
    }
}
