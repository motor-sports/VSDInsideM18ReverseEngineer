using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VSDM18App;

/// <summary>One button in the on-screen deck (an LCD key or a side button).</summary>
public sealed class KeyTile : ObservableObject
{
    private ImageSource? _preview;
    private string _caption = "";
    private bool _isSelected;

    public KeyTile(KeyConfig config, string title, bool isSide)
    {
        Config = config;
        Title = title;
        IsSide = isSide;
    }

    public KeyConfig Config { get; }
    public string Title { get; }
    public bool IsSide { get; }

    public ImageSource? Preview { get => _preview; set { _preview = value; Raise(); } }
    public string Caption { get => _caption; set { _caption = value; Raise(); } }
    public bool IsSelected { get => _isSelected; set { _isSelected = value; Raise(); } }
}

/// <summary>Colour entry: a swatch, a hex box and a few quick colours.</summary>
public sealed class ColorField : UserControl
{
    private static readonly string[] PresetHex =
        { "#000000", "#FFFFFF", "#E5484D", "#F5A524", "#46A758", "#3E63DD", "#8E4EC6" };

    private readonly Border _swatch;
    private readonly TextBox _box;
    private readonly StackPanel _presets;
    private string _hex = "#000000";

    /// <summary>Raised when the user picks or types a colour (not when Hex is set from code).</summary>
    public event Action<string>? HexChanged;

    public ColorField()
    {
        var line = (Brush?)Application.Current.TryFindResource("Line") ?? Brushes.Gray;

        _swatch = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1), BorderBrush = line, Margin = new Thickness(0, 0, 8, 0)
        };
        _box = new TextBox { Width = 78, MaxLength = 7, VerticalContentAlignment = VerticalAlignment.Center };
        _presets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };

        foreach (string hex in PresetHex)
        {
            var c = Rgb.Parse(hex, Rgb.Black);
            var dot = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 5, 0),
                Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)),
                BorderBrush = line, BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
                ToolTip = hex
            };
            dot.MouseLeftButtonUp += (_, _) => Apply(hex);
            _presets.Children.Add(dot);
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_swatch);
        row.Children.Add(_box);
        row.Children.Add(_presets);
        Content = row;

        _box.LostFocus += (_, _) => Apply(_box.Text);
        _box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(_box.Text); };
        UpdateUi();
    }

    public bool ShowPresets
    {
        get => _presets.Visibility == Visibility.Visible;
        set => _presets.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public string Hex
    {
        get => _hex;
        set
        {
            if (!Rgb.TryParse(value, out var c)) return;
            _hex = c.ToHex();
            UpdateUi();
        }
    }

    private void Apply(string text)
    {
        if (Rgb.TryParse(text, out var c))
        {
            string hex = c.ToHex();
            bool changed = hex != _hex;
            _hex = hex;
            UpdateUi();
            if (changed) HexChanged?.Invoke(_hex);
        }
        else
        {
            UpdateUi(); // put the last good colour back
        }
    }

    private void UpdateUi()
    {
        var c = Rgb.Parse(_hex, Rgb.Black);
        _box.Text = _hex;
        _swatch.Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
    }
}

/// <summary>Turns a read-only TextBox into a "press the key you want" box.</summary>
public static class KeyCapture
{
    private static readonly Key[] Modifiers =
        { Key.LeftCtrl, Key.RightCtrl, Key.LeftShift, Key.RightShift, Key.LeftAlt, Key.RightAlt, Key.LWin, Key.RWin };

    /// <param name="combo">True = capture held modifiers too (Ctrl+Shift+F5). False = a single key.</param>
    /// <param name="changed">Receives the key spec. It is also stored in the box's Tag.</param>
    public static void Attach(TextBox box, bool combo, Action<string> changed)
    {
        box.IsReadOnly = true;
        box.IsReadOnlyCaretVisible = false;
        box.Cursor = Cursors.Hand;

        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;

            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.ImeProcessed) key = e.ImeProcessedKey;
            if (key == Key.DeadCharProcessed) key = e.DeadCharProcessedKey;
            if (key == Key.None || key == Key.DeadCharProcessed) return;

            string spec;
            if (!combo)
            {
                spec = key.ToString();
            }
            else
            {
                var parts = new List<string>();
                foreach (var m in Modifiers)
                    if (m != key && Keyboard.IsKeyDown(m)) parts.Add(m.ToString());
                parts.Add(key.ToString());
                spec = string.Join("+", parts);
            }

            box.Tag = spec;
            box.Text = KeySpec.Pretty(spec);
            changed(spec);
        };
    }
}

/// <summary>A small "type a name" dialog.</summary>
public sealed class PromptWindow : Window
{
    private readonly TextBox _box;

    private string? Result { get; set; }

    private PromptWindow(string title, string label, string initial)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("Base");
        Foreground = (Brush)Application.Current.FindResource("Text");
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        _box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 16) };
        var ok = new Button
        {
            Content = "OK", IsDefault = true, MinWidth = 84,
            Style = (Style)Application.Current.FindResource("AccentButton")
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 84 };
        ok.Click += (_, _) => { Result = _box.Text.Trim(); DialogResult = true; };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = label });
        root.Children.Add(_box);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    /// <summary>Returns the entered text, or null if cancelled or left empty.</summary>
    public static string? Ask(Window owner, string title, string label, string initial = "")
    {
        var w = new PromptWindow(title, label, initial) { Owner = owner };
        return w.ShowDialog() == true && !string.IsNullOrWhiteSpace(w.Result) ? w.Result : null;
    }
}
