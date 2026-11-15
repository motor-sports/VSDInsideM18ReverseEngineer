using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VSDM18App
{
    public partial class LightingWindow : Window
    {
        private static readonly List<Option<LedSource>> RingModes = new()
        {
            new(LedSource.Static, "A fixed colour"),
            new(LedSource.Rpm, "Engine RPM (SimHub)"),
            new(LedSource.Flag, "Track flag (SimHub)"),
        };

        private static readonly List<Option<LedSource>> SideModes = new()
        {
            new(LedSource.Static, "A fixed colour"),
            new(LedSource.Rpm, "Engine RPM (SimHub)"),
            new(LedSource.Flag, "Track flag (SimHub)"),
        };

        private static readonly List<Option<RpmStyle>> RpmStyles = new()
        {
            new(RpmStyle.Bar, "Bar that fills around the ring"),
            new(RpmStyle.Solid, "Whole ring in one colour (static)"),
        };

        private AppController C => App.Controller;

        // The preset that was active when the window opened; all edits go to it.
        private readonly Profile _p = App.Controller.ActiveProfile;
        private readonly Action<string> _simStatusHandler;
        private bool _loading;

        public LightingWindow()
        {
            InitializeComponent();

            RingModeBox.ItemsSource = RingModes;
            SideModeBox.ItemsSource = SideModes;
            RpmStyleBox.ItemsSource = RpmStyles;

            RingField.HexChanged += hex => _p.RingColor = hex;
            LeftField.HexChanged += hex => _p.LeftColor = hex;
            RightField.HexChanged += hex => _p.RightColor = hex;
            LowField.HexChanged += hex => _p.RpmLowColor = hex;
            MidField.HexChanged += hex => _p.RpmMidColor = hex;
            HighField.HexChanged += hex => _p.RpmHighColor = hex;

            _simStatusHandler = _ => Dispatcher.BeginInvoke(UpdateSimStatus);
            C.SimHub.StatusChanged += _simStatusHandler;

            Load();
            UpdateSimStatus();
        }

        protected override void OnClosed(EventArgs e)
        {
            C.SimHub.StatusChanged -= _simStatusHandler;
            base.OnClosed(e);
        }

        private void Load()
        {
            _loading = true;
            try
            {
                TitleText.Text = $"Lighting for {_p.Name}";

                RingField.Hex = _p.RingColor;
                LeftField.Hex = _p.LeftColor;
                RightField.Hex = _p.RightColor;
                BrightnessSlider.Value = _p.Brightness;

                RingModeBox.SelectedValue = _p.RingMode;
                SideModeBox.SelectedValue = _p.SideMode;

                RpmStyleBox.SelectedValue = _p.RpmStyle;
                RpmStartSlider.Value = _p.RpmStartPercent;
                RpmStartText.Text = $"{_p.RpmStartPercent}%";
                RingOffsetSlider.Value = _p.RingOffset;
                RingOffsetText.Text = _p.RingOffset.ToString();
                RingReverseCheck.IsChecked = _p.RingReverse;

                ShowShares();

                LowField.Hex = _p.RpmLowColor;
                MidField.Hex = _p.RpmMidColor;
                HighField.Hex = _p.RpmHighColor;
            }
            finally
            {
                _loading = false;
            }
            UpdateRpmHint();
        }

        private void ShowShares()
        {
            LowShareSlider.Value = _p.RpmLowPercent;
            MidShareSlider.Value = _p.RpmMidPercent;
            HighShareSlider.Value = _p.RpmHighPercent;
            LowShareText.Text = $"{_p.RpmLowPercent}%";
            MidShareText.Text = $"{_p.RpmMidPercent}%";
            HighShareText.Text = $"{_p.RpmHighPercent}%";
        }

        private void UpdateSimStatus()
        {
            SimStatusText.Text = C.Config.SimHubEnabled
                ? "SimHub: " + C.SimHub.Status
                : "SimHub is switched off. Turn on 'Use SimHub data' in the main window for RPM and flags.";
        }

        private void UpdateRpmHint()
        {
            bool bar = _p.RpmStyle == RpmStyle.Bar;
            BarOptions.IsEnabled = bar;
            BarOptions.Opacity = bar ? 1.0 : 0.4;

            RpmHint.Text = bar
                ? "The bar fills one LED at a time. The low, middle and top colours cover the shares of the bar set below. Near the limiter the whole ring flashes in the top colour."
                : "The whole ring shows one colour that changes with RPM: low, middle, then top colour, flashing in the top colour near the limiter. Left and right lights use the same colours when set to RPM. The three shares always add up to 100%; moving one adjusts the other two.";
        }

        // ───────────────────────── handlers ─────────────────────────

        private void Brightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading || !IsLoaded) return;
            _p.Brightness = (int)BrightnessSlider.Value;
        }

        private void RingMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || RingModeBox.SelectedValue is not LedSource mode) return;
            _p.RingMode = mode;
        }

        private void SideMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || SideModeBox.SelectedValue is not LedSource mode) return;
            _p.SideMode = mode;
        }

        private void RpmStyle_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || RpmStyleBox.SelectedValue is not RpmStyle style) return;
            _p.RpmStyle = style;
            UpdateRpmHint();
        }

        private void RpmStart_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading || !IsLoaded) return;
            int value = (int)RpmStartSlider.Value;
            _p.RpmStartPercent = value;
            RpmStartText.Text = $"{value}%";
        }

        private void RingOffset_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading || !IsLoaded) return;
            int value = (int)RingOffsetSlider.Value;
            _p.RingOffset = value;
            RingOffsetText.Text = value.ToString();
        }

        // The three shares always add up to 100: moving one shares the rest between the other two.
        private void Share_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading || !IsLoaded) return;

            int changed = sender == LowShareSlider ? 0 : sender == MidShareSlider ? 1 : 2;
            int value = (int)((Slider)sender).Value;

            int[] v = { _p.RpmLowPercent, _p.RpmMidPercent, _p.RpmHighPercent };
            int a = (changed + 1) % 3, b = (changed + 2) % 3;
            int rest = 100 - value;
            int others = v[a] + v[b];

            int newA = others == 0 ? rest / 2 : (int)Math.Round(rest * (double)v[a] / others);
            v[changed] = value;
            v[a] = newA;
            v[b] = rest - newA;

            _loading = true;
            try
            {
                _p.RpmLowPercent = v[0];
                _p.RpmMidPercent = v[1];
                _p.RpmHighPercent = v[2];
                ShowShares();
            }
            finally
            {
                _loading = false;
            }
        }

        private void RingReverse_Click(object sender, RoutedEventArgs e) =>
            _p.RingReverse = RingReverseCheck.IsChecked == true;
    }
}
