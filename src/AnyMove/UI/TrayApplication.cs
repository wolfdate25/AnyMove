using System.Diagnostics;
using AnyMove.Config;
using AnyMove.Input;
using AnyMove.Native;
using AnyMove.SystemIntegration;

namespace AnyMove.UI;

internal sealed class TrayApplication : ApplicationContext
{
    private NotifyIcon _icon;
    private readonly AppSettings _settings;
    private readonly HookManager _hooks;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _winItem;
    private readonly ToolStripMenuItem _altItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly TaskbarCreatedWindow? _taskbarWindow;
    private readonly bool _elevated;

    public TrayApplication(AppSettings settings, HookManager hooks, bool elevated)
    {
        _settings = settings;
        _hooks = hooks;
        _elevated = elevated;

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

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_enabledItem);
        _menu.Items.Add(modifierMenu);
        _menu.Items.Add(_autostartItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem($"버전 {AppVersion}", null, (_, _) => OpenGitHub()));
        _menu.Items.Add(new ToolStripMenuItem("종료", null, (_, _) => ExitThread()));

        // 탐색기가 아직 없으면(로그온 경합) 숨긴 채로 두고 TaskbarCreated를 기다린다.
        _icon = CreateNotifyIcon(IsTaskbarReady());
        // TaskbarCreated를 받으면 아이콘을 통째로 다시 만든다(탐색기 재시작에도 대응).
        _taskbarWindow = TaskbarCreatedWindow.TryCreate(RestoreIcon);
    }

    private NotifyIcon CreateNotifyIcon(bool visible)
    {
        return new NotifyIcon
        {
            Icon = LoadAppIcon(),
            ContextMenuStrip = _menu,
            Text = TooltipText(),
            Visible = visible,
        };
    }

    private void RestoreIcon()
    {
        try
        {
            NotifyIcon old = _icon;
            _icon = CreateNotifyIcon(true);
            old.Dispose();
        }
        catch
        {
            // 재등록 실패 시 다음 TaskbarCreated에서 다시 시도한다.
        }
    }

    private static bool IsTaskbarReady()
    {
        try
        {
            return NativeMethods.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero;
        }
        catch
        {
            return true;
        }
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

    private string TooltipText()
    {
        string mode = _elevated ? string.Empty : " (제한 모드: 관리자 창 미지원)";
        return $"AnyMove {AppVersion}{mode}";
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
        {
            _taskbarWindow?.DestroyHandle();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }

    // 작업 표시줄 생성 브로드캐스트를 받는 메시지 전용 창.
    private sealed class TaskbarCreatedWindow : NativeWindow
    {
        private readonly uint _taskbarCreatedMsg;
        private readonly Action _onTaskbarCreated;

        private TaskbarCreatedWindow(uint taskbarCreatedMsg, Action onTaskbarCreated)
        {
            _taskbarCreatedMsg = taskbarCreatedMsg;
            _onTaskbarCreated = onTaskbarCreated;
            CreateHandle(new CreateParams());
        }

        public static TaskbarCreatedWindow? TryCreate(Action onTaskbarCreated)
        {
            try
            {
                uint msg = NativeMethods.RegisterWindowMessage("TaskbarCreated");
                return msg == 0 ? null : new TaskbarCreatedWindow(msg, onTaskbarCreated);
            }
            catch
            {
                return null;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == _taskbarCreatedMsg)
                _onTaskbarCreated();
            base.WndProc(ref m);
        }
    }
}
