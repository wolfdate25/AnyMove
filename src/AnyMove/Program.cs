using System.Security.Principal;
using AnyMove.Config;
using AnyMove.Input;
using AnyMove.SystemIntegration;
using AnyMove.UI;

namespace AnyMove;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // 단일 인스턴스
        using var mutex = new Mutex(true, "AnyMoveSingleInstance", out bool created);
        if (!created)
            return;

        AppSettings settings = SettingsStore.Load();

        // 트레이 체크 표시가 실제 스케줄러 등록 상태와 어긋나지 않게 시작 시 동기화한다.
        try
        {
            settings.Autostart = SchedulerTask.Exists();
        }
        catch
        {
            // 조회 실패 시 저장된 값을 그대로 사용한다.
        }

        using var hooks = new HookManager(settings);
        try
        {
            hooks.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"전역 후크 설치에 실패해 AnyMove를 시작할 수 없습니다.\n{ex.Message}",
                "AnyMove", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using var tray = new TrayApplication(settings, hooks, IsElevated());
        Application.Run(tray);

        SettingsStore.Save(settings);
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
