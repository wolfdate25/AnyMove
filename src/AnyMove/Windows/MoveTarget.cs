using System.Diagnostics;
using System.Text;
using AnyMove.Native;
using static AnyMove.Native.NativeMethods;

namespace AnyMove.Windows;

// 이동 대상 최상위 창의 핸들과 커서 오프셋 스냅샷.
internal sealed class MoveTarget
{
    // 시스템 창: 이동 대상에서 제외
    private static readonly string[] ExcludedClasses =
    {
        "Shell_TrayWnd",        // 작업 표시줄
        "Shell_SecondaryTrayWnd", // 보조 모니터 작업 표시줄
        "DV2ControlHost",       // 시작 메뉴
        "Windows.UI.Core.CoreWindow", // 시작 메뉴·위젯(Win11)
        "MultitaskingViewFrame",// 작업 보기
        "Progman",              // 바탕 화면
        "WorkerW",              // 바탕 화면
    };

    public IntPtr Hwnd { get; }
    private readonly int _offsetX;
    private readonly int _offsetY;

    private MoveTarget(IntPtr hwnd, int offsetX, int offsetY)
    {
        Hwnd = hwnd;
        _offsetX = offsetX;
        _offsetY = offsetY;
    }

    public static bool TryResolve(POINT cursor, out MoveTarget? target, bool includeMaximized = false)
    {
        target = null;

        IntPtr hit = WindowFromPoint(cursor);
        if (hit == IntPtr.Zero)
            return false;

        IntPtr root = GetAncestor(hit, GA_ROOT);
        if (root == IntPtr.Zero)
            return false;

        // 네이티브 이동(시스템 루프)은 restore-drag 되므로 최대화 창을 허용한다.
        if (!includeMaximized && IsZoomed(root))
            return false; // 최대화 창 제외

        if (IsExcludedClass(root))
            return false;

        if (!GetWindowRect(root, out RECT rect))
            return false;

        if (IsFullScreen(root, rect))
            return false;

        target = new MoveTarget(root, cursor.X - rect.Left, cursor.Y - rect.Top);
        return true;
    }

    // 마지막으로 낸 요청이 실제 반영됐을 때만 다음을 낸다(self-clocking).
    // 비동기 지정은 이때 큐에 쌓이지 않으므로 후크 스레드를 막지 않는다.
    // 느린 펌프의 창도 drain 속도에 자동 추종한다.
    private static readonly long ForceIntervalTicks = Stopwatch.Frequency / 10; // 100ms
    private int _lastReqX;
    private int _lastReqY;
    private long _lastApplyTick;
    private bool _hasReq;

    // Alive가 false면 창이 사라진 것으로 호출자는 세션을 끝낸다.
    // Posted는 실제 요청 여부다(합침·drain 대기 중이면 false).
    public (bool Alive, bool Posted) TryMove(int cursorX, int cursorY, long now, long minIntervalTicks)
    {
        // 드래그 도중 최대화된 창은 건드리지 않는다.
        if (IsZoomed(Hwnd))
            return (false, false);
        if (now - _lastApplyTick < minIntervalTicks)
            return (true, false); // 주사율 합침
        if (GetWindowRect(Hwnd, out RECT r))
        {
            // 반영 확인 전에는 drain을 기다린다. 단 강제 주기가 지나면
            // (앱이 직접 옮겼거나 확인을 놓친 경우) 최신 좌표로 재동기화한다.
            if (_hasReq && (r.Left != _lastReqX || r.Top != _lastReqY)
                && now - _lastApplyTick < ForceIntervalTicks)
                return (true, false);
        }
        // fail-open: rect 읽기 실패해도 요청은 보낸다. 죽은 창이면 아래에서 감지된다.
        int winX = cursorX - _offsetX, winY = cursorY - _offsetY;
        if (!SetWindowPos(Hwnd, IntPtr.Zero, winX, winY,
                0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS))
            return (false, false);
        _lastReqX = winX;
        _lastReqY = winY;
        _lastApplyTick = now;
        _hasReq = true;
        return (true, true);
    }

    private static bool IsExcludedClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        if (GetClassName(hwnd, sb, sb.Capacity) == 0)
            return false;
        string name = sb.ToString();
        return ExcludedClasses.Contains(name);
    }

    private static bool IsFullScreen(IntPtr hwnd, RECT rect)
    {
        try
        {
            var bounds = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
            return rect.Left <= bounds.Left && rect.Top <= bounds.Top
                && rect.Right >= bounds.Right && rect.Bottom >= bounds.Bottom;
        }
        catch
        {
            return false;
        }
    }
}
