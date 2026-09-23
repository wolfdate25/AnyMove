using System.Runtime.InteropServices;
using AnyMove.Config;
using AnyMove.Native;
using AnyMove.Windows;
using static AnyMove.Native.NativeMethods;

namespace AnyMove.Input;

// Idle <-> Moving 상태머신을 가진 저수준 후크 관리자.
// 후크 콜백은 설치 스레드(전용 메시지 펌프)에서 실행된다.
internal sealed class HookManager : IDisposable
{
    private readonly AppSettings _settings;
    private Thread? _thread;
    private uint _threadId;
    private readonly ManualResetEventSlim _ready = new(false);
    private bool _disposed;

    private HookProc? _mouseProc;
    private HookProc? _keyboardProc;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;

    private bool _moving;
    private MoveTarget? _target;
    private bool _hadSession; // 현재 Win 누름 동안 이동 세션이 있었는지
    private Exception? _startError;

    public bool Enabled
    {
        get => _settings.Enabled;
        set
        {
            _settings.Enabled = value;
            if (!value)
            {
                EndSession();
                _hadSession = false;
            }
        }
    }

    public HookManager(AppSettings settings)
    {
        _settings = settings;
    }

    public void Start()
    {
        if (_thread is not null)
            return;
        _mouseProc = OnMouse;
        _keyboardProc = OnKeyboard;
        _thread = new Thread(HookThread) { IsBackground = true, Name = "AnyMoveHook" };
        _thread.Start();
        _ready.Wait();
        if (_startError is not null)
            throw new InvalidOperationException(_startError.Message, _startError);
    }

    private void HookThread()
    {
        _threadId = GetCurrentThreadId();
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc!, GetModuleHandle(null), 0);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc!, GetModuleHandle(null), 0);
        if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            if (_mouseHook != IntPtr.Zero)
                UnhookWindowsHookEx(_mouseHook);
            if (_keyboardHook != IntPtr.Zero)
                UnhookWindowsHookEx(_keyboardHook);
            _mouseHook = IntPtr.Zero;
            _keyboardHook = IntPtr.Zero;
            _startError = new InvalidOperationException($"전역 후크 설치에 실패했습니다. (Win32 오류 {error})");
            _ready.Set();
            return;
        }
        _ready.Set();

        while (GetMessage(out MSG _, IntPtr.Zero, 0, 0) > 0) { }
    }

    private bool IsModifierDown()
    {
        // Win 키 이벤트는 항상 통과시키므로 OS 비동기 상태가 물리 상태와 일치한다.
        return _settings.Modifier == ModifierKey.Win
            ? IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN)
            : IsKeyDown(VK_MENU);
    }

    private IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Enabled)
        {
            int msg = wParam.ToInt32();
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            if (!_moving)
            {
                // 타이틀바 드래그(NCLBUTTONDOWN + HTCAPTION)도 세션으로 취급한다.
                // 닫기·최대화 등 캡션 버튼은 hit-test로 제외해 네이티브 동작을 보존한다.
                bool isDragStart = msg == WM_LBUTTONDOWN
                    || (msg == WM_NCLBUTTONDOWN && wParam.ToInt32() == HTCAPTION);
                if (isDragStart && IsModifierDown())
                {
                    if (MoveTarget.TryResolve(data.pt, out var target) && target is not null)
                    {
                        _target = target;
                        _moving = true;
                        _hadSession = true;
                        return (IntPtr)1; // 원본 클릭 삼킴
                    }
                    // 이동 불가 창: 이벤트 그대로 전달
                }
            }
            else
            {
                // 릴리스가 다른 창의 비클라이언트 영역에서 일어나면 NCLBUTTONUP으로 온다.
                if (!IsModifierDown() || msg == WM_LBUTTONUP || msg == WM_NCLBUTTONUP)
                {
                    EndSession();
                    return (IntPtr)1;
                }
                // 비클라이언트 이동도 통과시킨다. 삼키면 경계를 지날 때 커서가 끊긴다.
                if (msg == WM_MOUSEMOVE || msg == WM_NCMOUSEMOVE)
                {
                    try
                    {
                        // SetWindowPos 실패(창 파괴 등) 시 이동을 중단하고 일반 입력으로 폴백한다.
                        if (_target is null || !_target.MoveTo(data.pt.X, data.pt.Y))
                        {
                            EndSession();
                            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
                        }
                    }
                    catch
                    {
                        EndSession();
                        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
                    }
                    // 이동 메시지는 반드시 통과시킨다. 저수준 후크에서 삼키면
                    // 커서 위치 갱신까지 버려져 커서가 멈춰 버린다.
                    // 앱은 down을 못 받았으므로 move만 받아도 클릭·드래그 오동작은 없다.
                    return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
                }
                return (IntPtr)1; // 이동 중 다른 버튼 입력 삼킴
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr OnKeyboard(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        int msg = wParam.ToInt32();

        // 조합 키(down/up)는 항상 통과시킨다. 셸이 보는 쌍이 물리와 항상 일치하므로
        // 상태 고착이 구조적으로 불가능하다.
        // Win: 드래그 흔적을 지워 시작 메뉴가 뜨지 않게 한다.
        // Alt: Alt 단독 up은 메뉴 바 포커스를 유발하므로, 드래그 뒤 up에서 흔적을 지운다.
        bool isWinUp = _settings.Modifier == ModifierKey.Win
            && (kb.vkCode == VK_LWIN || kb.vkCode == VK_RWIN)
            && (msg == WM_KEYUP || msg == WM_SYSKEYUP);
        bool isAltUp = _settings.Modifier == ModifierKey.Alt
            && (kb.vkCode == VK_MENU || kb.vkCode == VK_LMENU || kb.vkCode == VK_RMENU)
            && (msg == WM_KEYUP || msg == WM_SYSKEYUP);
        if ((isWinUp || isAltUp) && _hadSession)
        {
            // 실제 드래그가 끝난 뒤의 up이다. 무해한 F24 탭으로 눌렀던 흔적을
            // 지우고, 실제 up은 통과시켜 래치를 푼다.
            // 순서가 뒤바뀌어도 시작 메뉴/메뉴 포커스가 생길 뿐 고착·팬텀 조합은 생기지 않는다.
            TapInertKey();
            _hadSession = false;
            EndSession();
        }

        // 이동 중 Esc: 취소하고 삼킴
        if ((msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            && kb.vkCode == VK_ESCAPE && _moving)
        {
            EndSession();
            return (IntPtr)1;
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    // 시작 메뉴 방지에 쓰는 무해 키 탭. F24는 바인딩이 사실상 없고
    // Shift와 달리 고정 키 대화상자를 유발하지 않는다.
    private static void TapInertKey()
    {
        try
        {
            var inputs = new[]
            {
                new INPUT
                {
                    type = INPUT_KEYBOARD,
                    u = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_F24 } },
                },
                new INPUT
                {
                    type = INPUT_KEYBOARD,
                    u = new INPUTUNION
                    {
                        ki = new KEYBDINPUT { wVk = VK_F24, dwFlags = KEYEVENTF_KEYUP },
                    },
                },
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }
        catch
        {
            // 실패해도 실제 up은 통과되므로 고착은 없다. 시작 메뉴가 뜰 수 있다.
        }
    }

    private void EndSession()
    {
        _moving = false;
        _target = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_threadId != 0)
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(2000);
        if (_mouseHook != IntPtr.Zero)
            UnhookWindowsHookEx(_mouseHook);
        if (_keyboardHook != IntPtr.Zero)
            UnhookWindowsHookEx(_keyboardHook);
        _ready.Dispose();
    }
}
