using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;

namespace VSDM18App;

public enum LinkState { Disconnected, Connecting, Connected }

/// <summary>What a display key shows right now: the big text and an optional background colour.</summary>
public sealed record LiveView(string Text, Rgb? Background);

/// <summary>
/// The brain of the app. It owns the active preset and page, decides what each button press does,
/// and keeps the deck's screen in step with what the window shows.
/// </summary>
public sealed class AppController
{
    private sealed class Held
    {
        public readonly List<ushort> Keys = new();
        public MacroRun? Run;
        public long DownAt = Environment.TickCount64;
    }

    private const long MinHoldMs = 35; // very short taps are stretched so games polling once a frame still see them

    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly DispatcherTimer _pushTimer;
    private readonly Dictionary<int, Held> _held = new();
    private readonly Dictionary<Guid, MacroRun> _runs = new();
    private readonly List<Guid> _back = new();

    private bool _connecting, _userDisconnected, _exiting;

    public AppConfig Config { get; }
    public HIDDeviceManager Device { get; } = new();
    public SimHubClient SimHub { get; } = new();
    private readonly LedEngine _leds;
    private readonly DispatcherTimer _liveTimer;
    private Dictionary<byte, LiveView> _liveLast = new();
    public Profile ActiveProfile { get; private set; }
    public DeckPage ActivePage { get; private set; }
    public LinkState State { get; private set; } = LinkState.Disconnected;
    public string? LastError { get; private set; }

    /// <summary>The active preset or page changed (from the window or from the deck).</summary>
    public event Action? LayoutChanged;

    /// <summary>A key that shows live SimHub data (gear, RPM, speed) has a new value.</summary>
    public event Action? LiveKeysChanged;

    /// <summary>Connection state changed.</summary>
    public event Action? StatusChanged;

    /// <summary>A button on the deck was pressed: text for the status bar.</summary>
    public event Action<string>? InputSeen;

    public AppController(AppConfig config)
    {
        Config = config;
        ActiveProfile = Config.Profiles.FirstOrDefault(p => p.Id == Config.ActiveProfileId) ?? Config.Profiles[0];
        ActivePage = ActiveProfile.Pages[0];

        _leds = new LedEngine(this);
        _leds.Start();

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _liveTimer.Tick += (_, _) => TickLive();
        _liveTimer.Start();
        ApplySimHubSettings();

        _pushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _pushTimer.Tick += (_, _) => { _pushTimer.Stop(); PushToDevice(); };

        Device.KeyStateChanged += (id, down) =>
            _ui.BeginInvoke(() => OnKey(id, down), DispatcherPriority.Send);

        Device.RawReport += (key, state) =>
        {
            if (state == 1) _ui.BeginInvoke(() => InputSeen?.Invoke(DescribeInput(key)));
        };

        Device.ConnectionChanged += connected => _ui.BeginInvoke(() =>
        {
            if (!connected) ReleaseAllHeld();
            State = connected ? LinkState.Connected : LinkState.Disconnected;
            StatusChanged?.Invoke();
        });
    }

    // ───────────────────────── connection ─────────────────────────

    public async Task ConnectAsync()
    {
        if (_connecting || Device.IsConnected) return;

        _connecting = true;
        _userDisconnected = false;
        LastError = null;
        State = LinkState.Connecting;
        StatusChanged?.Invoke();

        try
        {
            await Device.ConnectAsync(string.IsNullOrWhiteSpace(Config.Host) ? null : Config.Host.Trim());
            State = LinkState.Connected;
            PushToDevice();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            State = LinkState.Disconnected;
        }
        finally
        {
            _connecting = false;
            StatusChanged?.Invoke();
        }
    }

    public void Disconnect()
    {
        _userDisconnected = true;
        ReleaseAllHeld();
        Device.Disconnect(graceful: true);
        State = LinkState.Disconnected;
        StatusChanged?.Invoke();
    }

    /// <summary>Keeps trying to connect while the app runs (until you press Disconnect).</summary>
    public async Task RunAutoConnectAsync()
    {
        while (!_exiting)
        {
            if (Config.AutoConnect && !_userDisconnected && !_connecting && !Device.IsConnected)
                await ConnectAsync();

            await Task.Delay(4000);
        }
    }

    /// <summary>Starts or stops the SimHub link to match the settings.</summary>
    public void ApplySimHubSettings()
    {
        if (Config.SimHubEnabled) SimHub.Start(Config.SimHubAddress);
        else SimHub.Stop();
    }

    public void Shutdown()
    {
        _exiting = true;
        _leds.Stop();
        _liveTimer.Stop();
        SimHub.Dispose();
        ReleaseAllHeld();
        Device.Disconnect(graceful: true);
        InputInjector.ReleaseAll();
    }

    // ───────────────────────── admin ─────────────────────────

    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Games that run as administrator ignore input from normal programs, so restart elevated.</summary>
    public bool RestartAsAdmin()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe == null) return false;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch
        {
            return false; // user cancelled the UAC prompt
        }
    }

    // ───────────────────────── presets & pages ─────────────────────────

    public void SetActiveProfile(Profile profile)
    {
        if (!Config.Profiles.Contains(profile)) return;

        ActiveProfile = profile;
        Config.ActiveProfileId = profile.Id;
        ActivePage = profile.Pages.FirstOrDefault() ?? AddPageInternal(profile, "Page 1");
        _back.Clear();
        Changed(push: true);
    }

    public void SetActivePage(DeckPage page, bool recordHistory = false)
    {
        if (page == ActivePage || !ActiveProfile.Pages.Contains(page)) return;
        if (recordHistory) _back.Add(ActivePage.Id);
        ActivePage = page;
        Changed(push: true);
    }

    public void AddProfile(Profile profile)
    {
        Config.Profiles.Add(profile);
        SetActiveProfile(profile);
    }

    public void RemoveProfile(Profile profile)
    {
        if (Config.Profiles.Count <= 1) return;
        int index = Config.Profiles.IndexOf(profile);
        if (index < 0) return;

        Config.Profiles.RemoveAt(index);
        if (profile == ActiveProfile)
            SetActiveProfile(Config.Profiles[Math.Min(index, Config.Profiles.Count - 1)]);
    }

    public DeckPage AddPage(string name)
    {
        var page = AddPageInternal(ActiveProfile, name);
        SetActivePage(page);
        return page;
    }

    private static DeckPage AddPageInternal(Profile profile, string name)
    {
        var page = new DeckPage { Name = name };
        page.EnsureKeys();
        profile.Pages.Add(page);
        return page;
    }

    public void RemovePage(DeckPage page)
    {
        if (ActiveProfile.Pages.Count <= 1) return;
        int index = ActiveProfile.Pages.IndexOf(page);
        if (index < 0) return;

        ActiveProfile.Pages.RemoveAt(index);
        _back.RemoveAll(id => id == page.Id);
        if (page == ActivePage)
            SetActivePage(ActiveProfile.Pages[Math.Min(index, ActiveProfile.Pages.Count - 1)]);
        else
            Changed(push: false);
    }

    private void StepPage(int delta)
    {
        var pages = ActiveProfile.Pages;
        if (pages.Count < 2) return;
        int i = pages.IndexOf(ActivePage);
        SetActivePage(pages[(i + delta + pages.Count) % pages.Count]);
    }

    private void StepProfile(int delta)
    {
        var list = Config.Profiles;
        if (list.Count < 2) return;
        int i = list.IndexOf(ActiveProfile);
        SetActiveProfile(list[(i + delta + list.Count) % list.Count]);
    }

    private void GoBack()
    {
        while (_back.Count > 0)
        {
            Guid id = _back[^1];
            _back.RemoveAt(_back.Count - 1);
            var page = ActiveProfile.Pages.FirstOrDefault(p => p.Id == id);
            if (page != null && page != ActivePage)
            {
                SetActivePage(page);
                return;
            }
        }
    }

    private void Changed(bool push)
    {
        if (push) PushToDevice();
        LayoutChanged?.Invoke();
    }

    // ───────────────────────── deck output ─────────────────────────

    /// <summary>Waits a moment, then updates the deck. Use while typing so each letter doesn't re-send images.</summary>
    public void RequestPush()
    {
        _pushTimer.Stop();
        _pushTimer.Start();
    }

    public void PushToDevice()
    {
        _pushTimer.Stop();
        if (!Device.IsConnected) return;

        var screen = new Dictionary<byte, byte[]?>();
        foreach (byte slot in DeviceLayout.Slots)
        {
            var key = ActivePage.GetKey(slot);
            screen[slot] = KeyRenderer.IsBlank(key) ? null : RenderLive(key);
        }
        Device.ShowScreen(screen);

        _leds.Tick();
    }

    // ───────────────────────── live key displays ─────────────────────────

    private byte[] RenderLive(KeyConfig key)
    {
        var live = LiveFor(key);
        return KeyRenderer.RenderCached(key, live?.Text, live?.Background);
    }

    /// <summary>What a display key should show right now, or null if the key isn't a display.</summary>
    public LiveView? LiveFor(KeyConfig k)
    {
        if (k.Display == KeyDisplay.None) return null;

        var sim = SimHub.Latest;
        var p = ActiveProfile;
        long now = Environment.TickCount64;
        bool live = Config.SimHubEnabled && sim != null && sim.Running && now - sim.ReceivedAt < 2000;

        // Current flag: the key takes the flag's colour (and blinks if that flag is set to blink).
        if (k.Display == KeyDisplay.Flag)
        {
            if (live && LedEngine.FlagInfo(sim!.Flags, p, now, out var top, out Rgb flagColor, out bool lit))
                return new LiveView(FlagLabel(top), lit ? flagColor : null);
            return new LiveView("", null);
        }

        string text;
        switch (k.Display)
        {
            case KeyDisplay.Gear:
                text = live && !string.IsNullOrEmpty(sim!.Gear) ? sim.Gear : "-";
                break;
            case KeyDisplay.Rpm:
                // Rounded to the nearest 50 so the key isn't re-sent for every tiny change.
                text = live ? (Math.Round(sim!.Rpm / 50.0) * 50).ToString("0") : "----";
                break;
            default:
                text = live ? Math.Round(sim!.Speed).ToString("0") : "--";
                break;
        }

        Rgb? background = null;
        if (live && (k.RpmBackground || k.RedlineBlink))
        {
            bool above = LedEngine.RpmLevel(sim!, p, out _, out double level, out bool redline);

            if (k.RpmBackground && above)
                background = LedEngine.Zone(level, p);

            if (k.RedlineBlink && redline)
            {
                // Keys are re-sent for every change, so don't flash faster than about 3 times a second.
                bool on = (now / Math.Max(150, p.RedlineBlinkMs)) % 2 == 0;
                background = on
                    ? Rgb.Parse(p.RpmHighColor, new Rgb(255, 0, 0))
                    : (k.RpmBackground ? Rgb.Black : null);
            }
        }

        return new LiveView(text, background);
    }

    private static string FlagLabel(SimFlags f) => f switch
    {
        SimFlags.Checkered => "FINISH",
        SimFlags.Black => "BLACK",
        SimFlags.Red => "RED",
        SimFlags.Yellow => "YELLOW",
        SimFlags.Blue => "BLUE",
        SimFlags.Orange => "ORANGE",
        SimFlags.White => "WHITE",
        SimFlags.Green => "GREEN",
        _ => ""
    };

    private void TickLive()
    {
        var now = new Dictionary<byte, LiveView>();
        foreach (var key in ActivePage.Keys)
        {
            var view = LiveFor(key);
            if (view != null) now[(byte)key.Slot] = view;
        }

        bool changed = now.Count != _liveLast.Count ||
                       now.Any(kv => !_liveLast.TryGetValue(kv.Key, out var old) || old != kv.Value);
        if (!changed) return;

        _liveLast = now;
        LiveKeysChanged?.Invoke();
        PushToDevice();
    }

    // ───────────────────────── button presses ─────────────────────────

    private KeyConfig? Resolve(int inputId)
    {
        if (Array.IndexOf(DeviceLayout.SideInputs, inputId) >= 0)
            return ActiveProfile.GetSideButton(inputId);

        return DeviceLayout.InputToSlot.TryGetValue(inputId, out byte slot) ? ActivePage.GetKey(slot) : null;
    }

    private void OnKey(int id, bool down)
    {
        if (down)
        {
            if (_held.ContainsKey(id)) return;

            var held = new Held();
            _held[id] = held;

            var cfg = Resolve(id);
            if (cfg == null) return;

            try { Execute(cfg, held); }
            catch (Exception ex) { Debug.WriteLine($"[Controller] Action failed: {ex.Message}"); }
        }
        else if (_held.Remove(id, out var held))
        {
            Release(held);
        }
    }

    private void Execute(KeyConfig k, Held held)
    {
        switch (k.Action)
        {
            case ActionType.Hotkey:
                foreach (ushort vk in KeySpec.ParseVks(k.Hotkey))
                {
                    InputInjector.KeyDown(vk);
                    held.Keys.Add(vk);
                }
                break;

            case ActionType.Macro:
                var macro = Config.Macros.FirstOrDefault(m => m.Id == k.MacroId);
                if (macro != null && macro.Steps.Count > 0) StartMacro(macro, held);
                break;

            case ActionType.GoToPage:
                var target = ActiveProfile.Pages.FirstOrDefault(p => p.Id == k.TargetPageId);
                if (target != null) SetActivePage(target, recordHistory: true);
                break;

            case ActionType.NextPage: StepPage(+1); break;
            case ActionType.PreviousPage: StepPage(-1); break;
            case ActionType.Back: GoBack(); break;

            case ActionType.GoToProfile:
                var profile = Config.Profiles.FirstOrDefault(p => p.Id == k.TargetProfileId);
                if (profile != null) SetActiveProfile(profile);
                break;

            case ActionType.NextProfile: StepProfile(+1); break;
            case ActionType.PreviousProfile: StepProfile(-1); break;

            case ActionType.LaunchApp:
                if (!string.IsNullOrWhiteSpace(k.ProgramPath))
                    Process.Start(new ProcessStartInfo(k.ProgramPath) { Arguments = k.ProgramArgs, UseShellExecute = true });
                break;
        }
    }

    private void StartMacro(Macro macro, Held held)
    {
        _runs.TryGetValue(macro.Id, out var existing);
        bool running = existing?.IsRunning == true;

        switch (macro.Mode)
        {
            case MacroPlayMode.Once:
                if (!running) _runs[macro.Id] = MacroRun.Start(macro, loop: false);
                break;

            case MacroPlayMode.RepeatWhileHeld:
                if (!running)
                {
                    var run = MacroRun.Start(macro, loop: true);
                    _runs[macro.Id] = run;
                    held.Run = run;
                }
                break;

            case MacroPlayMode.ToggleRepeat:
                if (running) existing!.Stop();
                else _runs[macro.Id] = MacroRun.Start(macro, loop: true);
                break;
        }
    }

    private static void Release(Held held)
    {
        held.Run?.Stop();
        if (held.Keys.Count == 0) return;

        var keys = held.Keys.ToArray();
        long heldFor = Environment.TickCount64 - held.DownAt;

        void Up() { for (int i = keys.Length - 1; i >= 0; i--) InputInjector.KeyUp(keys[i]); }

        if (heldFor >= MinHoldMs) Up();
        else _ = Task.Delay((int)(MinHoldMs - heldFor)).ContinueWith(_ => Up());
    }

    public void ReleaseAllHeld()
    {
        foreach (var held in _held.Values) Release(held);
        _held.Clear();
        foreach (var run in _runs.Values) run.Stop();
    }

    // ───────────────────────── text for the UI ─────────────────────────

    public string Describe(KeyConfig k)
    {
        switch (k.Action)
        {
            case ActionType.Hotkey: return KeySpec.Pretty(k.Hotkey);
            case ActionType.Macro:
                return "Macro: " + (Config.Macros.FirstOrDefault(m => m.Id == k.MacroId)?.Name ?? "none chosen");
            case ActionType.GoToPage:
                return "Go to " + (ActiveProfile.Pages.FirstOrDefault(p => p.Id == k.TargetPageId)?.Name ?? "page");
            case ActionType.NextPage: return "Next page";
            case ActionType.PreviousPage: return "Previous page";
            case ActionType.Back: return "Back";
            case ActionType.GoToProfile:
                return "Go to " + (Config.Profiles.FirstOrDefault(p => p.Id == k.TargetProfileId)?.Name ?? "preset");
            case ActionType.NextProfile: return "Next preset";
            case ActionType.PreviousProfile: return "Previous preset";
            case ActionType.LaunchApp:
                return string.IsNullOrWhiteSpace(k.ProgramPath) ? "Open program" : System.IO.Path.GetFileName(k.ProgramPath);
            default: return "";
        }
    }

    private static string DescribeInput(int key)
    {
        if (DeviceLayout.InputToSlot.ContainsKey(key)) return $"Last press: key {key}";
        int side = Array.IndexOf(DeviceLayout.SideInputs, key);
        return side >= 0 ? $"Last press: side button {side + 1}" : $"Last press: unknown input {key}";
    }
}
