using System.Diagnostics;

namespace AnyMove.SystemIntegration;

// 작업 스케줄러 로그온 태스크(가장 높은 권한)로 자동 시작 등록/해제.
// UAC 프롬프트 없이 상승 실행으로 시작하기 위한 수단이다.
internal static class SchedulerTask
{
    private const string TaskName = "AnyMove";

    public static bool Exists()
    {
        return Run("schtasks", $"/Query /TN \"{TaskName}\"") == 0;
    }

    public static void SetEnabled(string exePath, bool enabled)
    {
        if (enabled)
        {
            string args = $"/Create /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\"\" /SC ONLOGON /RL HIGHEST /F";
            if (Run("schtasks", args) != 0)
                throw new InvalidOperationException("자동 시작 등록에 실패했습니다.");
        }
        else if (Exists())
        {
            Run("schtasks", $"/Delete /TN \"{TaskName}\" /F");
        }
    }

    private static int Run(string fileName, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            p?.WaitForExit(10000);
            return p?.ExitCode ?? -1;
        }
        catch
        {
            return -1;
        }
    }
}
