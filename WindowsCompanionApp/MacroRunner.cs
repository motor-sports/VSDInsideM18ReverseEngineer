using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace VSDM18App;

/// <summary>Plays a macro on a background task. Anything it pressed is released when it stops.</summary>
public sealed class MacroRun
{
    private sealed record StepData(MacroStepType Type, string Key, int Ms, string Text);

    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<ushort> _keys = new();
    private readonly HashSet<string> _buttons = new(StringComparer.OrdinalIgnoreCase);

    public Task Completion { get; private set; } = Task.CompletedTask;
    public bool IsRunning => !Completion.IsCompleted;

    public void Stop() => _cts.Cancel();

    /// <summary>Call on the UI thread (it snapshots the steps first).</summary>
    public static MacroRun Start(Macro macro, bool loop)
    {
        var run = new MacroRun();
        var steps = macro.Steps.Select(s => new StepData(s.Type, s.Key, s.Ms, s.Text)).ToList();
        int gap = macro.RepeatDelayMs;
        run.Completion = Task.Run(() => run.RunAsync(steps, gap, loop));
        return run;
    }

    private async Task RunAsync(List<StepData> steps, int gapMs, bool loop)
    {
        var ct = _cts.Token;
        try
        {
            do
            {
                foreach (var s in steps)
                {
                    ct.ThrowIfCancellationRequested();
                    await ExecuteAsync(s, ct);
                }
                if (loop) await Task.Delay(Math.Max(1, gapMs), ct);
            }
            while (loop);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Debug.WriteLine($"[Macro] {ex}"); }
        finally { ReleaseHeld(); }
    }

    private async Task ExecuteAsync(StepData s, CancellationToken ct)
    {
        switch (s.Type)
        {
            case MacroStepType.KeyDown:
            {
                ushort vk = KeySpec.Vk(s.Key);
                if (vk != 0 && _keys.Add(vk)) InputInjector.KeyDown(vk);
                break;
            }
            case MacroStepType.KeyUp:
            {
                ushort vk = KeySpec.Vk(s.Key);
                if (vk != 0 && _keys.Remove(vk)) InputInjector.KeyUp(vk);
                break;
            }
            case MacroStepType.KeyPress:
            {
                ushort vk = KeySpec.Vk(s.Key);
                if (vk == 0) break;
                bool added = _keys.Add(vk);
                if (added) InputInjector.KeyDown(vk);
                try { await Task.Delay(Math.Max(1, s.Ms), ct); }
                finally { if (_keys.Remove(vk)) InputInjector.KeyUp(vk); }
                break;
            }
            case MacroStepType.Delay:
                if (s.Ms > 0) await Task.Delay(s.Ms, ct);
                break;
            case MacroStepType.TypeText:
                if (!string.IsNullOrEmpty(s.Text)) InputInjector.TypeText(s.Text);
                break;
            case MacroStepType.MouseDown:
                if (_buttons.Add(s.Key)) InputInjector.MouseButton(s.Key, true);
                break;
            case MacroStepType.MouseUp:
                if (_buttons.Remove(s.Key)) InputInjector.MouseButton(s.Key, false);
                break;
            case MacroStepType.MouseClick:
            {
                bool added = _buttons.Add(s.Key);
                if (added) InputInjector.MouseButton(s.Key, true);
                try { await Task.Delay(Math.Max(10, s.Ms), ct); }
                finally { if (_buttons.Remove(s.Key)) InputInjector.MouseButton(s.Key, false); }
                break;
            }
        }
    }

    private void ReleaseHeld()
    {
        foreach (var vk in _keys.ToArray()) InputInjector.KeyUp(vk);
        _keys.Clear();
        foreach (var b in _buttons.ToArray()) InputInjector.MouseButton(b, false);
        _buttons.Clear();
    }
}

/// <summary>
/// Records the keyboard with a low-level hook. Create and Start it on the UI thread.
/// Input injected by this app (or any other program) is ignored.
/// </summary>
public sealed class MacroRecorder : IDisposable
{
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public UIntPtr dwExtraInfo;
    }

    private const int WH_KEYBOARD_LL = 13;

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private readonly HookProc _proc;
    private readonly Stopwatch _clock = new();
    private readonly HashSet<uint> _down = new();
    private IntPtr _hook = IntPtr.Zero;
    private long _last;
    private bool _started;

    public event Action<MacroStep>? StepRecorded;

    public MacroRecorder() { _proc = Callback; }

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _clock.Restart();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException("Couldn't start recording (keyboard hook failed).");
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool injected = (info.flags & 0x10) != 0;
            if (!injected)
            {
                int msg = wParam.ToInt32();
                bool down = msg == 0x0100 || msg == 0x0104;
                bool up = msg == 0x0101 || msg == 0x0105;
                if (down || up) Record(info.vkCode, down);
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void Record(uint vk, bool down)
    {
        var key = KeyInterop.KeyFromVirtualKey((int)vk);
        if (key == Key.None) return;

        if (down) { if (!_down.Add(vk)) return; } // ignore auto-repeat
        else _down.Remove(vk);

        long now = _clock.ElapsedMilliseconds;
        if (_started)
        {
            long gap = now - _last;
            if (gap >= 5) StepRecorded?.Invoke(new MacroStep { Type = MacroStepType.Delay, Ms = (int)gap });
        }
        _started = true;
        _last = now;

        StepRecorded?.Invoke(new MacroStep
        {
            Type = down ? MacroStepType.KeyDown : MacroStepType.KeyUp,
            Key = key.ToString()
        });
    }

    public void Dispose() => Stop();
}
