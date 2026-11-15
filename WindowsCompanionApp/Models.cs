using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace VSDM18App;

// ───────────────────────── Enums ─────────────────────────

public enum ActionType
{
    None, Hotkey, Macro, GoToPage, NextPage, PreviousPage, Back,
    GoToProfile, NextProfile, PreviousProfile, LaunchApp
}

public enum MacroPlayMode { Once, RepeatWhileHeld, ToggleRepeat }

public enum MacroStepType { KeyPress, KeyDown, KeyUp, Delay, TypeText, MouseClick, MouseDown, MouseUp }

public enum InputMode { Scancode, VirtualKey }

/// <summary>Live SimHub value drawn on an LCD key.</summary>
public enum KeyDisplay { None, Gear, Rpm, Speed }

/// <summary>How the ring shows RPM: a bar that fills around it, or the whole ring in one colour.</summary>
public enum RpmStyle { Bar, Solid }

/// <summary>What drives a group of LEDs.</summary>
public enum LedSource { Static, Rpm, Flag }

public record Option<T>(T Value, string Text);

// ───────────────────────── Base class ─────────────────────────

public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <summary>Raised whenever any saved setting changes (drives auto-save).</summary>
    public static event Action? Modified;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        Modified?.Invoke();
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Black => new(0, 0, 0);

    public static bool TryParse(string? hex, out Rgb color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        string s = hex.Trim().TrimStart('#');
        if (s.Length != 6) return false;
        if (!byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) ||
            !byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) ||
            !byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            return false;
        color = new Rgb(r, g, b);
        return true;
    }

    public static Rgb Parse(string? hex, Rgb fallback) => TryParse(hex, out var c) ? c : fallback;

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
}

// ───────────────────────── Macros ─────────────────────────

public class MacroStep : ObservableObject
{
    private MacroStepType _type = MacroStepType.KeyPress;
    private string _key = "";
    private int _ms = 40;
    private string _text = "";

    public MacroStepType Type { get => _type; set { if (Set(ref _type, value)) Raise(nameof(Summary)); } }

    /// <summary>A WPF Key name ("A", "LeftShift", "F13") or a mouse button ("Left", "Right", "Middle", "X1", "X2").</summary>
    public string Key { get => _key; set { if (Set(ref _key, value ?? "")) Raise(nameof(Summary)); } }

    /// <summary>Wait time for Delay steps, hold time for KeyPress / MouseClick.</summary>
    public int Ms { get => _ms; set { if (Set(ref _ms, Math.Clamp(value, 0, 600000))) Raise(nameof(Summary)); } }

    public string Text { get => _text; set { if (Set(ref _text, value ?? "")) Raise(nameof(Summary)); } }

    [JsonIgnore]
    public string Summary
    {
        get
        {
            string k = string.IsNullOrEmpty(Key) ? "(no key)" : KeySpec.Pretty(Key);
            return Type switch
            {
                MacroStepType.KeyPress => $"Press {k}, hold {Ms} ms",
                MacroStepType.KeyDown => $"Hold down {k}",
                MacroStepType.KeyUp => $"Release {k}",
                MacroStepType.Delay => $"Wait {Ms} ms",
                MacroStepType.TypeText => $"Type \"{Text}\"",
                MacroStepType.MouseClick => $"Click {Key} mouse button, hold {Ms} ms",
                MacroStepType.MouseDown => $"Hold down {Key} mouse button",
                MacroStepType.MouseUp => $"Release {Key} mouse button",
                _ => ""
            };
        }
    }

    public static bool IsMouse(MacroStepType t) =>
        t is MacroStepType.MouseClick or MacroStepType.MouseDown or MacroStepType.MouseUp;
}

public class Macro : ObservableObject
{
    private string _name = "New macro";
    private MacroPlayMode _mode = MacroPlayMode.Once;
    private int _repeatDelayMs = 20;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => Set(ref _name, value ?? ""); }
    public MacroPlayMode Mode { get => _mode; set => Set(ref _mode, value); }
    public int RepeatDelayMs { get => _repeatDelayMs; set => Set(ref _repeatDelayMs, Math.Clamp(value, 1, 60000)); }
    public ObservableCollection<MacroStep> Steps { get; set; } = new();
}

// ───────────────────────── Key / page / preset ─────────────────────────

/// <summary>
/// One button's setup. For the 15 LCD keys, Slot is the display slot id (0x01-0x0F).
/// For the side buttons, Slot is the device's input id (37, 48, 49).
/// </summary>
public class KeyConfig : ObservableObject
{
    private string _label = "";
    private string? _imageFile;
    private string _background = "#000000";
    private ActionType _action;
    private string _hotkey = "";
    private Guid? _macroId, _targetPageId, _targetProfileId;
    private string _programPath = "";
    private string _programArgs = "";
    private KeyDisplay _display;
    private bool _rpmBackground, _redlineBlink;

    public int Slot { get; set; }
    public string Label { get => _label; set => Set(ref _label, value ?? ""); }
    public string? ImageFile { get => _imageFile; set => Set(ref _imageFile, value); }
    public string Background { get => _background; set => Set(ref _background, value ?? "#000000"); }
    public ActionType Action { get => _action; set => Set(ref _action, value); }
    public string Hotkey { get => _hotkey; set => Set(ref _hotkey, value ?? ""); }
    public Guid? MacroId { get => _macroId; set => Set(ref _macroId, value); }
    public Guid? TargetPageId { get => _targetPageId; set => Set(ref _targetPageId, value); }
    public Guid? TargetProfileId { get => _targetProfileId; set => Set(ref _targetProfileId, value); }
    public string ProgramPath { get => _programPath; set => Set(ref _programPath, value ?? ""); }
    public string ProgramArgs { get => _programArgs; set => Set(ref _programArgs, value ?? ""); }
    public KeyDisplay Display { get => _display; set => Set(ref _display, value); }
    /// <summary>Display keys only: the key's background takes the current RPM colour.</summary>
    public bool RpmBackground { get => _rpmBackground; set => Set(ref _rpmBackground, value); }
    /// <summary>Display keys only: the background flashes in the top RPM colour at the redline.</summary>
    public bool RedlineBlink { get => _redlineBlink; set => Set(ref _redlineBlink, value); }

    public void Reset()
    {
        Label = ""; ImageFile = null; Background = "#000000"; Action = ActionType.None;
        Hotkey = ""; MacroId = null; TargetPageId = null; TargetProfileId = null;
        ProgramPath = ""; ProgramArgs = ""; Display = KeyDisplay.None;
        RpmBackground = false; RedlineBlink = false;
    }
}

/// <summary>A "screen" of 15 keys. Called DeckPage to avoid clashing with System.Windows.Controls.Page.</summary>
public class DeckPage : ObservableObject
{
    private string _name = "Page";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => Set(ref _name, value ?? ""); }
    public List<KeyConfig> Keys { get; set; } = new();

    public KeyConfig GetKey(int slot)
    {
        var k = Keys.FirstOrDefault(x => x.Slot == slot);
        if (k == null) { k = new KeyConfig { Slot = slot }; Keys.Add(k); }
        return k;
    }

    public void EnsureKeys() { foreach (var s in DeviceLayout.Slots) GetKey(s); }
}

/// <summary>A preset: a set of pages, side-button bindings and LED colours.</summary>
public class Profile : ObservableObject
{
    private string _name = "Preset";
    private string _ring = "#FF0000", _left = "#00FF00", _right = "#0000FF";
    private int _brightness = 100;
    private LedSource _ringMode = LedSource.Static, _sideMode = LedSource.Static;
    private int _ringOffset, _rpmStart = 60;
    private bool _ringReverse;
    private RpmStyle _rpmStyle = RpmStyle.Bar;
    private string _rpmLow = "#00FF00", _rpmMid = "#FFAA00", _rpmHigh = "#FF0000";
    private int _lowPct = 60, _midPct = 25, _highPct = 15;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => Set(ref _name, value ?? ""); }
    public string RingColor { get => _ring; set => Set(ref _ring, value ?? "#FF0000"); }
    public string LeftColor { get => _left; set => Set(ref _left, value ?? "#00FF00"); }
    public string RightColor { get => _right; set => Set(ref _right, value ?? "#0000FF"); }
    public int Brightness { get => _brightness; set => Set(ref _brightness, Math.Clamp(value, 0, 100)); }
    public LedSource RingMode { get => _ringMode; set => Set(ref _ringMode, value); }
    public LedSource SideMode { get => _sideMode; set => Set(ref _sideMode, value); }
    /// <summary>Which of the 22 ring LEDs (0-21) is the first one of the RPM bar.</summary>
    public int RingOffset { get => _ringOffset; set => Set(ref _ringOffset, Math.Clamp(value, 0, 21)); }
    public bool RingReverse { get => _ringReverse; set => Set(ref _ringReverse, value); }
    public RpmStyle RpmStyle { get => _rpmStyle; set => Set(ref _rpmStyle, value); }
    /// <summary>RPM colours: low and middle range, and the top range (which also flashes at the limiter).</summary>
    public string RpmLowColor { get => _rpmLow; set => Set(ref _rpmLow, value ?? "#00FF00"); }
    public string RpmMidColor { get => _rpmMid; set => Set(ref _rpmMid, value ?? "#FFAA00"); }
    public string RpmHighColor { get => _rpmHigh; set => Set(ref _rpmHigh, value ?? "#FF0000"); }
    /// <summary>How the RPM range is split between the three colours (meant to add up to 100).</summary>
    public int RpmLowPercent { get => _lowPct; set => Set(ref _lowPct, Math.Clamp(value, 0, 100)); }
    public int RpmMidPercent { get => _midPct; set => Set(ref _midPct, Math.Clamp(value, 0, 100)); }
    public int RpmHighPercent { get => _highPct; set => Set(ref _highPct, Math.Clamp(value, 0, 100)); }
    /// <summary>The RPM bar stays dark until the engine passes this percentage of max RPM.</summary>
    public int RpmStartPercent { get => _rpmStart; set => Set(ref _rpmStart, Math.Clamp(value, 0, 95)); }
    public ObservableCollection<DeckPage> Pages { get; set; } = new();
    public List<KeyConfig> SideButtons { get; set; } = new();

    public KeyConfig GetSideButton(int inputId)
    {
        var k = SideButtons.FirstOrDefault(x => x.Slot == inputId);
        if (k == null) { k = new KeyConfig { Slot = inputId }; SideButtons.Add(k); }
        return k;
    }

    public IEnumerable<KeyConfig> AllKeys() =>
        Pages.SelectMany(p => p.Keys).Concat(SideButtons);
}

public class AppConfig : ObservableObject
{
    private string _host = "";
    private InputMode _inputMode = InputMode.Scancode;
    private bool _autoConnect = true;
    private bool _simHubEnabled;
    private string _simHubAddress = "127.0.0.1:8888";

    public int Version { get; set; } = 2;
    public Guid? ActiveProfileId { get; set; }

    /// <summary>Blank = find raspberrypi.local automatically.</summary>
    public string Host { get => _host; set => Set(ref _host, value ?? ""); }
    public InputMode InputMode { get => _inputMode; set => Set(ref _inputMode, value); }
    public bool AutoConnect { get => _autoConnect; set => Set(ref _autoConnect, value); }
    public bool SimHubEnabled { get => _simHubEnabled; set => Set(ref _simHubEnabled, value); }
    public string SimHubAddress { get => _simHubAddress; set => Set(ref _simHubAddress, value ?? ""); }

    public ObservableCollection<Profile> Profiles { get; set; } = new();
    public ObservableCollection<Macro> Macros { get; set; } = new();
}

// ───────────────────────── Hardware layout ─────────────────────────

public static class DeviceLayout
{
    // Display slot ids, top row first. Matches the original ButtonMap.
    public static readonly byte[][] Rows =
    {
        new byte[] { 0x0b, 0x0c, 0x0d, 0x0e, 0x0f },
        new byte[] { 0x06, 0x07, 0x08, 0x09, 0x0a },
        new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 },
    };

    public static readonly int[] SideInputs = { 37, 48, 49 };

    /// <summary>Input id reported by the device (1-15) to display slot id.</summary>
    public static readonly IReadOnlyDictionary<int, byte> InputToSlot = Build();

    public static IEnumerable<byte> Slots => Rows.SelectMany(r => r);

    private static Dictionary<int, byte> Build()
    {
        var d = new Dictionary<int, byte>();
        int input = 1;
        foreach (var row in Rows)
            foreach (var slot in row)
                d[input++] = slot;
        return d;
    }
}

// ───────────────────────── Key names ─────────────────────────

/// <summary>Hotkeys are stored as WPF Key names joined with '+', e.g. "LeftCtrl+LeftShift+F5".</summary>
public static class KeySpec
{
    public static ushort Vk(string? keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName)) return 0;
        if (!Enum.TryParse<Key>(keyName.Trim(), true, out var k) || k == Key.None) return 0;
        return (ushort)KeyInterop.VirtualKeyFromKey(k);
    }

    public static List<ushort> ParseVks(string? spec)
    {
        var list = new List<ushort>();
        if (string.IsNullOrWhiteSpace(spec)) return list;
        foreach (var part in spec.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            ushort vk = Vk(part);
            if (vk != 0) list.Add(vk);
        }
        return list;
    }

    public static string Pretty(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return "";
        return string.Join(" + ", spec.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(PrettyKey));
    }

    private static string PrettyKey(string k) => k switch
    {
        "LeftCtrl" => "Ctrl",
        "RightCtrl" => "Right Ctrl",
        "LeftShift" => "Shift",
        "RightShift" => "Right Shift",
        "LeftAlt" => "Alt",
        "RightAlt" => "Right Alt",
        "LWin" => "Win",
        "RWin" => "Right Win",
        "Return" => "Enter",
        "Back" => "Backspace",
        "Prior" => "Page Up",
        "Next" => "Page Down",
        "Capital" => "Caps Lock",
        _ when k.Length == 2 && k[0] == 'D' && char.IsDigit(k[1]) => k[1].ToString(),
        _ when k.StartsWith("NumPad", StringComparison.Ordinal) => "Num " + k.Substring(6),
        _ => k
    };
}
