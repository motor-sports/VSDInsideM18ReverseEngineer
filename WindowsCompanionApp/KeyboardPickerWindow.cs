using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VSDM18App;

/// <summary>
/// A pop-out on-screen keyboard. Click keys to choose them instead of pressing them.
/// combo = true lets you pick several keys (Ctrl + Shift + F5); false picks exactly one.
/// </summary>
public sealed class KeyboardPickerWindow : Window
{
    private const double U = 44, Gap = 4;   // key size and spacing, in pixels

    private static readonly Key[] ModifierOrder =
        { Key.LeftCtrl, Key.RightCtrl, Key.LeftShift, Key.RightShift, Key.LeftAlt, Key.RightAlt, Key.LWin, Key.RWin };

    private readonly bool _combo;
    private readonly List<Key> _selected = new();
    private readonly Dictionary<Key, Border> _caps = new();
    private readonly Canvas _canvas = new() { Width = 23 * U, Height = 7.5 * U };
    private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center };
    private string? _result;

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
    private static Key K(string name) => Enum.Parse<Key>(name);

    private KeyboardPickerWindow(bool combo, string? current)
    {
        _combo = combo;

        Title = combo ? "Pick keys" : "Pick a key";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Res("Base");
        Foreground = Res("Text");
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;

        BuildKeys();

        foreach (string part in (current ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries))
            if (Enum.TryParse<Key>(part, out var k) && _caps.ContainsKey(k) && (combo || _selected.Count == 0))
                _selected.Add(k);

        var clear = new Button { Content = "Clear" };
        clear.Click += (_, _) => { _selected.Clear(); Refresh(); };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var ok = new Button
        {
            Content = combo ? "Use these keys" : "Use this key", IsDefault = true, MinWidth = 120,
            Style = (Style)Application.Current.FindResource("AccentButton")
        };
        ok.Click += (_, _) => { _result = string.Join("+", Ordered()); DialogResult = true; };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(clear);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var bottom = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_summary);

        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock
        {
            Text = combo
                ? "Click keys to add or remove them. Modifier keys are pressed first automatically."
                : "Click the key you want.",
            Foreground = Res("Muted"),
            Margin = new Thickness(0, 0, 0, 14)
        });
        root.Children.Add(_canvas);
        root.Children.Add(bottom);
        Content = root;

        Refresh();
    }

    /// <summary>Returns the chosen key spec ("" = none), or null if cancelled.</summary>
    public static string? Pick(Window owner, string? current, bool combo)
    {
        var w = new KeyboardPickerWindow(combo, current) { Owner = owner };
        return w.ShowDialog() == true ? w._result : null;
    }

    // ───────────────────────── selection ─────────────────────────

    private void Toggle(Key key)
    {
        if (!_combo) { _selected.Clear(); _selected.Add(key); }
        else if (!_selected.Remove(key)) _selected.Add(key);
        Refresh();
    }

    private List<Key> Ordered() =>
        _selected.Where(k => Array.IndexOf(ModifierOrder, k) >= 0)
                 .OrderBy(k => Array.IndexOf(ModifierOrder, k))
                 .Concat(_selected.Where(k => Array.IndexOf(ModifierOrder, k) < 0))
                 .ToList();

    private void Refresh()
    {
        foreach (var (key, cap) in _caps)
        {
            bool on = _selected.Contains(key);
            cap.Background = on ? Res("Accent") : Res("Raised");
            cap.BorderBrush = on ? Res("Accent") : Res("Line");
            if (cap.Child is TextBlock t) t.Foreground = on ? Res("AccentText") : Res("Text");
        }

        var ordered = Ordered();
        _summary.Text = ordered.Count == 0
            ? "Nothing selected"
            : "Selected: " + KeySpec.Pretty(string.Join("+", ordered));
    }

    // ───────────────────────── keyboard drawing ─────────────────────────

    private void AddKey(string label, Key key, double x, double y, double w = 1, double h = 1)
    {
        var cap = new Border
        {
            Width = w * U - Gap, Height = h * U - Gap,
            CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            BorderBrush = Res("Line"), Background = Res("Raised"),
            Cursor = Cursors.Hand, ToolTip = key.ToString(),
            Child = new TextBlock
            {
                Text = label, FontSize = label.Length > 3 ? 10.5 : 12.5,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        cap.MouseLeftButtonUp += (_, _) => Toggle(key);
        Canvas.SetLeft(cap, x * U);
        Canvas.SetTop(cap, y * U);
        _canvas.Children.Add(cap);
        _caps[key] = cap;
    }

    private void AddRow(double y, params (string label, string key, double w)[] keys)
    {
        double x = 0;
        foreach (var (label, key, w) in keys)
        {
            AddKey(label, K(key), x, y, w);
            x += w;
        }
    }

    private static (string, string, double)[] Letters(string letters) =>
        letters.Select(c => (c.ToString(), c.ToString(), 1.0)).ToArray();

    private void BuildKeys()
    {
        // Function rows (F13-F24 included, since many setups bind to them)
        AddKey("Esc", Key.Escape, 0, 0);
        string[] f = { "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12" };
        double[] fx = { 2, 3, 4, 5, 6.5, 7.5, 8.5, 9.5, 11, 12, 13, 14 };
        for (int i = 0; i < 12; i++) AddKey(f[i], K(f[i]), fx[i], 0);
        for (int i = 0; i < 12; i++) AddKey($"F{13 + i}", K($"F{13 + i}"), 2 + i, 1.1);

        double y = 2.5;

        // Number row
        var row1 = new List<(string, string, double)> { ("`", "Oem3", 1) };
        for (int d = 1; d <= 10; d++) row1.Add(((d % 10).ToString(), $"D{d % 10}", 1));
        row1.Add(("-", "OemMinus", 1));
        row1.Add(("=", "OemPlus", 1));
        row1.Add(("Backspace", "Back", 2));
        AddRow(y, row1.ToArray());

        // QWERTY
        var row2 = new List<(string, string, double)> { ("Tab", "Tab", 1.5) };
        row2.AddRange(Letters("QWERTYUIOP"));
        row2.Add(("[", "Oem4", 1));
        row2.Add(("]", "Oem6", 1));
        row2.Add(("\\", "Oem5", 1.5));
        AddRow(y + 1, row2.ToArray());

        // ASDF
        var row3 = new List<(string, string, double)> { ("Caps Lock", "Capital", 1.75) };
        row3.AddRange(Letters("ASDFGHJKL"));
        row3.Add((";", "Oem1", 1));
        row3.Add(("'", "OemQuotes", 1));
        row3.Add(("Enter", "Return", 2.25));
        AddRow(y + 2, row3.ToArray());

        // ZXCV
        var row4 = new List<(string, string, double)> { ("Shift", "LeftShift", 2.25) };
        row4.AddRange(Letters("ZXCVBNM"));
        row4.Add((",", "OemComma", 1));
        row4.Add((".", "OemPeriod", 1));
        row4.Add(("/", "OemQuestion", 1));
        row4.Add(("Shift", "RightShift", 2.75));
        AddRow(y + 3, row4.ToArray());

        // Bottom row
        AddRow(y + 4,
            ("Ctrl", "LeftCtrl", 1.25), ("Win", "LWin", 1.25), ("Alt", "LeftAlt", 1.25),
            ("Space", "Space", 6.25),
            ("Alt", "RightAlt", 1.25), ("Win", "RWin", 1.25), ("Menu", "Apps", 1.25), ("Ctrl", "RightCtrl", 1.25));

        // Navigation block
        AddKey("PrtSc", Key.Snapshot, 15.5, 0);
        AddKey("ScrLk", Key.Scroll, 16.5, 0);
        AddKey("Pause", Key.Pause, 17.5, 0);
        AddKey("Ins", Key.Insert, 15.5, y);
        AddKey("Home", Key.Home, 16.5, y);
        AddKey("PgUp", Key.Prior, 17.5, y);
        AddKey("Del", Key.Delete, 15.5, y + 1);
        AddKey("End", Key.End, 16.5, y + 1);
        AddKey("PgDn", Key.Next, 17.5, y + 1);
        AddKey("Up", Key.Up, 16.5, y + 3);
        AddKey("Left", Key.Left, 15.5, y + 4);
        AddKey("Down", Key.Down, 16.5, y + 4);
        AddKey("Right", Key.Right, 17.5, y + 4);

        // Number pad
        AddKey("Num", Key.NumLock, 19, y);
        AddKey("/", Key.Divide, 20, y);
        AddKey("*", Key.Multiply, 21, y);
        AddKey("-", Key.Subtract, 22, y);
        AddKey("7", Key.NumPad7, 19, y + 1);
        AddKey("8", Key.NumPad8, 20, y + 1);
        AddKey("9", Key.NumPad9, 21, y + 1);
        AddKey("+", Key.Add, 22, y + 1, 1, 2);
        AddKey("4", Key.NumPad4, 19, y + 2);
        AddKey("5", Key.NumPad5, 20, y + 2);
        AddKey("6", Key.NumPad6, 21, y + 2);
        AddKey("1", Key.NumPad1, 19, y + 3);
        AddKey("2", Key.NumPad2, 20, y + 3);
        AddKey("3", Key.NumPad3, 21, y + 3);
        AddKey("0", Key.NumPad0, 19, y + 4, 2);
        AddKey(".", Key.Decimal, 21, y + 4);
    }
}
