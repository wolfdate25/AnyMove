using System.Diagnostics;
using AnyMove.Config;
using AnyMove.Input;
using AnyMove.SystemIntegration;

namespace AnyMove.UI;

internal sealed class TrayApplication : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly AppSettings _settings;
    private readonly HookManager _hooks;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _winItem;
    private readonly ToolStripMenuItem _altItem;
    private readonly ToolStripMenuItem _autostartItem;

    public TrayApplication(AppSettings settings, HookManager hooks, bool elevated)
    {
        _settings = settings;
        _hooks = hooks;

        _enabledItem = new ToolStripMenuItem("사용", null, (_, _) => ToggleEnabled())
        {
            Checked = settings.Enabled,
        };
        _winItem = new ToolStripMenuItem("Win", null, (_, _) => SetModifier(ModifierKey.Win))
        {
            Checked = settings.Modifier == ModifierKey.Win,
        };
        _altItem = new ToolStripMenuItem("Alt", null, (_, _) => SetModifier(ModifierKey.Alt))
        {
            Checked = settings.Modifier == ModifierKey.Alt,
        };
        _autostartItem = new ToolStripMenuItem("자동 시작", null, (_, _) => ToggleAutostart())
        {
            Checked = settings.Autostart,
        };

        var modifierMenu = new ToolStripMenuItem("조합 키");
        modifierMenu.DropDownItems.Add(_winItem);
        modifierMenu.DropDownItems.Add(_altItem);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_enabledItem);
        menu.Items.Add(modifierMenu);
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem($"버전 {AppVersion}", null, (_, _) => OpenGitHub()));
        menu.Items.Add(new ToolStripMenuItem("종료", null, (_, _) => ExitThread()));

        _icon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        UpdateTooltip(elevated);
    }

    private void ToggleEnabled()
    {
        _hooks.Enabled = !_hooks.Enabled;
        _enabledItem.Checked = _hooks.Enabled;
        _settings.Enabled = _hooks.Enabled;
        SettingsStore.Save(_settings);
    }

    private void SetModifier(ModifierKey modifier)
    {
        _settings.Modifier = modifier;
        _winItem.Checked = modifier == ModifierKey.Win;
        _altItem.Checked = modifier == ModifierKey.Alt;
        SettingsStore.Save(_settings);
    }

    private void ToggleAutostart()
    {
        try
        {
            string exe = Application.ExecutablePath;
            SchedulerTask.SetEnabled(exe, !_settings.Autostart);
            _settings.Autostart = !_settings.Autostart;
            _autostartItem.Checked = _settings.Autostart;
            SettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "AnyMove", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void UpdateTooltip(bool elevated)
    {
        string mode = elevated ? string.Empty : " (제한 모드: 관리자 창 미지원)";
        _icon.Text = $"AnyMove {AppVersion}{mode}";
    }

    private static string AppVersion =>
        typeof(TrayApplication).Assembly.GetName().Version?.ToString(3) ?? "?";

    private const string GitHubReleasesUrl = "https://github.com/wolfdate25/AnyMove/releases";

    private static void OpenGitHub()
    {
        try
        {
            Process.Start(new ProcessStartInfo(GitHubReleasesUrl) { UseShellExecute = true });
        }
        catch
        {
            // 브라우저 실행 실패는 무시
        }
    }

    // 실행 파일에 박힌 아이콘을 그대로 쓴다. 단일 파일 게시에서도 항상 동작한다.
    private static Icon LoadAppIcon()
    {
        try
        {
            Icon? icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon is not null)
                return icon;
        }
        catch
        {
            // 추출 실패 시 기본 아이콘으로 폴백
        }
        return SystemIcons.Application;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _icon.Dispose();
        base.Dispose(disposing);
    }
}
