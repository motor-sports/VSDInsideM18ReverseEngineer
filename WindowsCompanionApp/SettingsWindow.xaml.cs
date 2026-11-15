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

            bool elevated = AppController.IsElevated;

            SimHubStatusText.Text = C.SimHub.Status;
            C.SimHub.StatusChanged += status => Dispatcher.BeginInvoke(() => SimHubStatusText.Text = status);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            C.Shutdown();
            base.OnClosing(e);
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

        private void SimHubAddressBox_LostFocus(object sender, RoutedEventArgs e)
        {
            C.Config.SimHubAddress = SimHubAddressBox.Text.Trim();
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
