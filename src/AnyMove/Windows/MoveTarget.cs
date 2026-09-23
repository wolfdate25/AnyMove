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

    public static bool TryResolve(POINT cursor, out MoveTarget? target)
    {
        target = null;

        IntPtr hit = WindowFromPoint(cursor);
        if (hit == IntPtr.Zero)
            return false;

        IntPtr root = GetAncestor(hit, GA_ROOT);
        if (root == IntPtr.Zero)
            return false;

        if (IsZoomed(root))
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

    // SetWindowPos 실패(창 파괴 등) 시 false를 반환하고, 호출자는 이동을 중단한다.
    public bool MoveTo(int cursorX, int cursorY)
    {
        // 드래그 도중 최대화된 창은 건드리지 않는다.
        if (IsZoomed(Hwnd))
            return false;
        return SetWindowPos(Hwnd, IntPtr.Zero, cursorX - _offsetX, cursorY - _offsetY,
            0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
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
