using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VSDM18App
{
    public partial class SettingsWindow : Window
    {
        private static readonly List<Option<InputMode>> InputModes = new()
        {
            new(InputMode.Scancode, "Hardware scan codes (best for games)"),
            new(InputMode.VirtualKey, "Virtual keys (for apps that ignore scan codes)"),
        };

        private AppController C => App.Controller;
        private readonly Action<string> _simStatusHandler;
        private bool _loading;

        public SettingsWindow()
        {
            InitializeComponent();

            Quiet(() =>
            {
                HostBox.Text = C.Config.Host;
                InputModeBox.ItemsSource = InputModes;
                InputModeBox.SelectedValue = C.Config.InputMode;
                AutoConnectCheck.IsChecked = C.Config.AutoConnect;
                SimHubCheck.IsChecked = C.Config.SimHubEnabled;
                SimHubAddressBox.Text = C.Config.SimHubAddress;
            });

            SimHubStatusText.Text = C.SimHub.Status;
            _simStatusHandler = status => Dispatcher.BeginInvoke(() => SimHubStatusText.Text = status);
            C.SimHub.StatusChanged += _simStatusHandler;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // LostFocus doesn't fire when the window is closed, so save anything still being typed.
            C.Config.Host = HostBox.Text.Trim();
            ApplySimHubAddress();
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            C.SimHub.StatusChanged -= _simStatusHandler;
            base.OnClosed(e);
        }

        private void Quiet(Action action)
        {
            bool previous = _loading;
            _loading = true;
            try { action(); }
            finally { _loading = previous; }
        }

        // ───────────────────────── refresh ─────────────────────────


        private void SimHubCheck_Click(object sender, RoutedEventArgs e)
        {
            C.Config.SimHubEnabled = SimHubCheck.IsChecked == true;
            C.ApplySimHubSettings();
        }

        private void SimHubAddressBox_LostFocus(object sender, RoutedEventArgs e) => ApplySimHubAddress();

        private void ApplySimHubAddress()
        {
            string address = SimHubAddressBox.Text.Trim();
            if (address == C.Config.SimHubAddress) return;
            C.Config.SimHubAddress = address;
            C.ApplySimHubSettings();
        }

        private void HostBox_LostFocus(object sender, RoutedEventArgs e) => C.Config.Host = HostBox.Text.Trim();

        private void InputModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || InputModeBox.SelectedValue is not InputMode mode) return;
            C.Config.InputMode = mode;
            InputInjector.Mode = mode;
        }

        private void AutoConnect_Click(object sender, RoutedEventArgs e) =>
            C.Config.AutoConnect = AutoConnectCheck.IsChecked == true;
    }
}
