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
    private uint _pendingWinVk; // 삼킨 Win-down의 키(0 = 없음). 셸에는 균형 잡힌 down/up 쌍만 보인다.
    private bool _hadSession; // 보류 중인 Win 누름 동안 이동 세션이 있었는지
    private Exception? _startError;

    public bool Enabled
    {
        get => _settings.Enabled;
        set
        {
            _settings.Enabled = value;
            if (!value)
            {
                // 사용 중지 시 진행 중 이동을 취소하고 보류 중인 Win 누름을 버린다.
                // (버려진 down에 대응하는 up은 단독 up이라 셸이 무시하므로 고착 없음)
                EndSession();
                _pendingWinVk = 0;
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
        if (_settings.Modifier == ModifierKey.Win)
        {
            // Win-down은 후크에서 삼키므로 OS 비동기 상태가 갱신되지 않을 수 있다.
            // 후크가 직접 관찰한 보류 상태를 우선 사용한다.
            if (_pendingWinVk != 0)
                return true;
            return IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN);
        }
        return IsKeyDown(VK_MENU);
    }

    private IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Enabled)
        {
            int msg = wParam.ToInt32();
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            if (!_moving)
            {
                if (msg == WM_LBUTTONDOWN && IsModifierDown())
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
                if (!IsModifierDown() || msg == WM_LBUTTONUP)
                {
                    EndSession();
                    return (IntPtr)1;
                }
                if (msg == WM_MOUSEMOVE)
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

        // 합성 입력(자체 재생 포함)은 상태를 건드리지 않고 그대로 전달한다.
        if ((kb.flags & LLKHF_INJECTED) != 0)
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        int msg = wParam.ToInt32();

        if (_settings.Modifier == ModifierKey.Win)
        {
            bool isWin = kb.vkCode == VK_LWIN || kb.vkCode == VK_RWIN;

            // Win-down은 항상 삼키고 보류한다. 셸이 down을 못 봤으므로
            // 이후 up을 삼켜도 셸 상태가 고착되지 않는다.
            if (isWin && (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN))
            {
                if (_pendingWinVk == 0)
                {
                    _pendingWinVk = kb.vkCode;
                    // 버튼을 누른 채 Win을 다시 누른 경우 세션 유지로 보고 억제를 잇는다.
                    if (!_moving)
                        _hadSession = false;
                }
                return (IntPtr)1;
            }

            if (isWin && (msg == WM_KEYUP || msg == WM_SYSKEYUP))
            {
                if (_pendingWinVk != 0)
                {
                    uint vk = _pendingWinVk;
                    _pendingWinVk = 0;
                    if (_hadSession)
                    {
                        // 이동했으면 up도 삼킨다. 셸은 down/up 모두 못 봤으므로 시작 메뉴 없음.
                        _hadSession = false;
                        EndSession();
                        return (IntPtr)1;
                    }
                    // 이동 없으면 삼킨 down을 재생한 뒤 실제 up을 통과시켜 시작 메뉴를 보존한다.
                    ReplayWinDown(vk);
                }
                return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            }

            // 보류 중 다른 키가 오면(Win+R 등) down을 먼저 재생해 조합이 동작하게 한다.
            if (_pendingWinVk != 0
                && (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN || msg == WM_KEYUP || msg == WM_SYSKEYUP))
            {
                ReplayWinDown(_pendingWinVk);
                _pendingWinVk = 0;
            }
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

    private static void ReplayWinDown(uint vkCode)
    {
        try
        {
            var input = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION { ki = new KEYBDINPUT { wVk = (ushort)vkCode } },
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }
        catch
        {
            // 재생 실패 시 셸은 up만 보게 되어 시작 메뉴가 안 뜰 수 있으나, 상태 고착은 없다.
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
