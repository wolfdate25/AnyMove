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
    private System.Windows.Forms.Timer? _retryTimer;
    private int _retryCount;

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
        bool ready = IsTaskbarReady();
        TrayLog($"start elevated={elevated} taskbarReady={ready}");
        _icon = CreateNotifyIcon(ready);
        if (!ready)
            StartRetryTimer();
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
        // 브로드캐스트를 받으면 재시도 타이머는 임무 종료.
        StopRetryTimer();
        TrayLog("taskbarCreated received, recreating icon");
        RecreateIcon();
    }

    private void RecreateIcon()
    {
        try
        {
            NotifyIcon old = _icon;
            _icon = CreateNotifyIcon(true);
            old.Dispose();
            TrayLog("icon recreated visible=true");
        }
        catch (Exception ex)
        {
            TrayLog($"recreate failed: {ex.GetType().Name}");
        }
    }

    // 브로드캐스트를 놓친 경우를 대비한 안전망. 작업 표시줄이 보이면 등록하고 멈춘다.
    private void StartRetryTimer()
    {
        StopRetryTimer();
        _retryCount = 0;
        _retryTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _retryTimer.Tick += (_, _) =>
        {
            _retryCount++;
            if (IsTaskbarReady())
            {
                TrayLog($"retry success after {_retryCount} tries");
                StopRetryTimer();
                RecreateIcon();
            }
            else if (_retryCount >= 24)
            {
                TrayLog("retry gave up after 24 tries");
                StopRetryTimer();
            }
        };
        _retryTimer.Start();
        TrayLog("retry timer started");
    }

    private void StopRetryTimer()
    {
        _retryTimer?.Stop();
        _retryTimer?.Dispose();
        _retryTimer = null;
    }

    // 키 입력은 절대 기록하지 않는다. 트레이 생명주기(등록/재시도)만 남긴다.
    private static void TrayLog(string message)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "AnyMove-tray.log");
            var info = new FileInfo(path);
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
            if (info.Exists && info.Length > 102400)
                File.WriteAllText(path, line);
            else
                File.AppendAllText(path, line);
        }
        catch
        {
            // 로깅 실패는 무시
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
            StopRetryTimer();
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
