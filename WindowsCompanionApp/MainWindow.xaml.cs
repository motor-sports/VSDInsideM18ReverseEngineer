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
    public partial class MainWindow : Window
    {
        private static readonly List<Option<ActionType>> ActionOptions = new()
        {
            new(ActionType.None, "Do nothing"),
            new(ActionType.Hotkey, "Press keys"),
            new(ActionType.Macro, "Run a macro"),
            new(ActionType.GoToPage, "Go to a page"),
            new(ActionType.NextPage, "Next page"),
            new(ActionType.PreviousPage, "Previous page"),
            new(ActionType.Back, "Back to the last page"),
            new(ActionType.GoToProfile, "Switch to a preset"),
            new(ActionType.NextProfile, "Next preset"),
            new(ActionType.PreviousProfile, "Previous preset"),
            new(ActionType.LaunchApp, "Open a program"),
        };

        private static readonly List<Option<InputMode>> InputModes = new()
        {
            new(InputMode.Scancode, "Hardware scan codes (best for games)"),
            new(InputMode.VirtualKey, "Virtual keys (for apps that ignore scan codes)"),
        };

        private static readonly List<Option<KeyDisplay>> DisplayOptions = new()
        {
            new(KeyDisplay.None, "Nothing (image and label only)"),
            new(KeyDisplay.Gear, "Current gear"),
            new(KeyDisplay.Rpm, "Engine RPM"),
            new(KeyDisplay.Speed, "Speed (km/h)"),
            new(KeyDisplay.Flag, "Current flag"),
        };

        private static readonly BitmapImage Unset = LoadUnset();

        private AppController C => App.Controller;

        private List<KeyTile> _tiles = new();
        private List<KeyTile> _sideTiles = new();
        private KeyTile? _selected;
        private bool _loading;

        public MainWindow()
        {
            InitializeComponent();

            ActionBox.ItemsSource = ActionOptions;
            DisplayBox.ItemsSource = DisplayOptions;
            ProfileList.ItemsSource = C.Config.Profiles;
            MacroBox.ItemsSource = C.Config.Macros;
            ProfileTargetBox.ItemsSource = C.Config.Profiles;
            KeyCapture.Attach(HotkeyBox, combo: true, _ => Commit());

            bool elevated = AppController.IsElevated;
            AdminButton.Visibility = elevated ? Visibility.Collapsed : Visibility.Visible;
            AdminBadge.Visibility = elevated ? Visibility.Visible : Visibility.Collapsed;

            BgField.HexChanged += _ => Commit();

            C.LiveKeysChanged += () =>
            {
                foreach (var t in _tiles)
                    if (t.Config.Display != KeyDisplay.None) UpdateTile(t);
            };

            C.LayoutChanged += RefreshAll;
            C.StatusChanged += RefreshStatus;
            C.InputSeen += text => InputLine.Text = text;

            Loaded += (_, _) =>
            {
                RefreshAll();
                _ = C.RunAutoConnectAsync();
            };
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            C.Shutdown();
            base.OnClosing(e);
        }

        private static BitmapImage LoadUnset()
        {
            var bi = new BitmapImage(new Uri("pack://application:,,,/unset.png"));
            bi.Freeze();
            return bi;
        }

        private void Quiet(Action action)
        {
            bool previous = _loading;
            _loading = true;
            try { action(); }
            finally { _loading = previous; }
        }

        // ───────────────────────── refresh ─────────────────────────

        private void RefreshAll()
        {
            Quiet(() =>
            {
                ProfileList.SelectedItem = C.ActiveProfile;

                if (!ReferenceEquals(PageList.ItemsSource, C.ActiveProfile.Pages))
                {
                    PageList.ItemsSource = C.ActiveProfile.Pages;
                    PageTargetBox.ItemsSource = C.ActiveProfile.Pages;
                }
                PageList.SelectedItem = C.ActivePage;

            });

            RebuildTiles();
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            switch (C.State)
            {
                case LinkState.Connected:
                    StatusDot.Fill = (Brush)FindResource("Good");
                    StatusText.Text = $"Connected to {C.Device.HostName}";
                    break;
                case LinkState.Connecting:
                    StatusDot.Fill = (Brush)FindResource("Accent");
                    StatusText.Text = "Looking for the deck...";
                    break;
                default:
                    StatusDot.Fill = (Brush)FindResource("Muted");
                    StatusText.Text = string.IsNullOrEmpty(C.LastError) ? "Not connected" : "Not connected: " + C.LastError;
                    break;
            }

            ConnectButton.Content = C.State == LinkState.Connected ? "Disconnect" : "Connect";
            ConnectButton.IsEnabled = C.State != LinkState.Connecting;
            DeckLine.Text = $"Showing {C.ActiveProfile.Name}, {C.ActivePage.Name}";
        }

        private void RebuildTiles()
        {
            int? selectedSlot = _selected?.Config.Slot;
            bool selectedWasSide = _selected?.IsSide ?? false;

            _tiles = new List<KeyTile>();
            int number = 1;
            foreach (var row in DeviceLayout.Rows)
                foreach (byte slot in row)
                    _tiles.Add(new KeyTile(C.ActivePage.GetKey(slot), $"Key {number++}", false));

            _sideTiles = DeviceLayout.SideInputs
                .Select((id, i) => new KeyTile(C.ActiveProfile.GetSideButton(id), $"Side button {i + 1}", true))
                .ToList();

            foreach (var t in _tiles.Concat(_sideTiles)) UpdateTile(t);

            KeyGrid.ItemsSource = _tiles;
            SideGrid.ItemsSource = _sideTiles;

            _selected = null;
            if (selectedSlot != null)
            {
                var again = (selectedWasSide ? _sideTiles : _tiles).FirstOrDefault(t => t.Config.Slot == selectedSlot);
                if (again != null) { Select(again); return; }
            }
            LoadEditor();
        }

        private void UpdateTile(KeyTile t)
        {
            var live = C.LiveFor(t.Config);
            t.Preview = KeyRenderer.IsBlank(t.Config)
                ? Unset
                : KeyRenderer.ToBitmapSource(KeyRenderer.RenderCached(t.Config, live?.Text, live?.Background));
            t.Caption = C.Describe(t.Config);
        }

        // ───────────────────────── selecting & editing keys ─────────────────────────

        private void Select(KeyTile? tile)
        {
            foreach (var t in _tiles.Concat(_sideTiles)) t.IsSelected = false;
            _selected = tile;
            if (tile != null) tile.IsSelected = true;
            LoadEditor();
        }

        private static KeyTile? FindTile(object? source)
        {
            var d = source as DependencyObject;
            while (d != null)
            {
                if (d is FrameworkElement fe && fe.DataContext is KeyTile t) return t;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        private void KeyGrid_Click(object sender, MouseButtonEventArgs e) => Select(FindTile(e.OriginalSource));
        private void SideGrid_Click(object sender, MouseButtonEventArgs e) => Select(FindTile(e.OriginalSource));

        private void KeyGrid_RightClick(object sender, MouseButtonEventArgs e)
        {
            var tile = FindTile(e.OriginalSource);
            if (tile == null) return;
            Select(tile);

            var menu = new ContextMenu();
            var removeImage = new MenuItem { Header = "Remove image" };
            removeImage.Click += (_, _) => RemoveImage_Click(this, new RoutedEventArgs());
            var clear = new MenuItem { Header = "Clear this key" };
            clear.Click += (_, _) => ClearKey_Click(this, new RoutedEventArgs());
            menu.Items.Add(removeImage);
            menu.Items.Add(clear);
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void KeyGrid_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && FindTile(e.OriginalSource) != null
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void KeyGrid_Drop(object sender, DragEventArgs e)
        {
            var tile = FindTile(e.OriginalSource);
            if (tile == null) return;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                Select(tile);
                SetImage(tile, files[0]);
            }
        }

        private void LoadEditor()
        {
            Quiet(() =>
            {
                var t = _selected;
                EditorEmpty.Visibility = t == null ? Visibility.Visible : Visibility.Collapsed;
                EditorBody.Visibility = t == null ? Visibility.Collapsed : Visibility.Visible;
                if (t == null) return;

                var k = t.Config;
                EditorTitle.Text = t.IsSide ? t.Title : $"{t.Title} on {C.ActivePage.Name}";
                LookSection.Visibility = t.IsSide ? Visibility.Collapsed : Visibility.Visible;

                LabelBox.Text = k.Label;
                BgField.Hex = k.Background;
                DisplayBox.SelectedValue = k.Display;
                RpmBgCheck.IsChecked = k.RpmBackground;
                RedlineBlinkCheck.IsChecked = k.RedlineBlink;
                LiveOptions.Visibility = ShowsRpmOptions(k.Display) ? Visibility.Visible : Visibility.Collapsed;
                ActionBox.SelectedValue = k.Action;
                HotkeyBox.Tag = k.Hotkey;
                HotkeyBox.Text = KeySpec.Pretty(k.Hotkey);
                MacroBox.SelectedValue = k.MacroId;
                PageTargetBox.SelectedValue = k.TargetPageId;
                ProfileTargetBox.SelectedValue = k.TargetProfileId;
                PathBox.Text = k.ProgramPath;
                ArgsBox.Text = k.ProgramArgs;
                UseIconCheck.IsChecked = k.UseProgramIcon;
                UpdateActionPanels(k.Action);
            });
        }

        // The RPM background / redline flash options only make sense for gear, RPM and speed keys.
        private static bool ShowsRpmOptions(KeyDisplay d) =>
            d is KeyDisplay.Gear or KeyDisplay.Rpm or KeyDisplay.Speed;

        private void UpdateActionPanels(ActionType a)
        {
            HotkeyPanel.Visibility = a == ActionType.Hotkey ? Visibility.Visible : Visibility.Collapsed;
            MacroPanel.Visibility = a == ActionType.Macro ? Visibility.Visible : Visibility.Collapsed;
            PagePanel.Visibility = a == ActionType.GoToPage ? Visibility.Visible : Visibility.Collapsed;
            ProfilePanel.Visibility = a == ActionType.GoToProfile ? Visibility.Visible : Visibility.Collapsed;
            AppPanel.Visibility = a == ActionType.LaunchApp ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Editor_Changed(object sender, EventArgs e) => Commit();

        private void ActionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            UpdateActionPanels((ActionType)(ActionBox.SelectedValue ?? ActionType.None));
            Commit();
        }

        private void Commit()
        {
            if (_loading || _selected == null) return;
            var k = _selected.Config;

            if (!_selected.IsSide)
            {
                k.Label = LabelBox.Text;
                k.Background = BgField.Hex;
                k.Display = (KeyDisplay)(DisplayBox.SelectedValue ?? KeyDisplay.None);
                k.RpmBackground = RpmBgCheck.IsChecked == true;
                k.RedlineBlink = RedlineBlinkCheck.IsChecked == true;
                LiveOptions.Visibility = ShowsRpmOptions(k.Display) ? Visibility.Visible : Visibility.Collapsed;
            }

            k.Action = (ActionType)(ActionBox.SelectedValue ?? ActionType.None);
            k.Hotkey = HotkeyBox.Tag as string ?? "";
            k.MacroId = MacroBox.SelectedValue as Guid?;
            k.TargetPageId = PageTargetBox.SelectedValue as Guid?;
            k.TargetProfileId = ProfileTargetBox.SelectedValue as Guid?;
            k.ProgramPath = PathBox.Text;
            k.ProgramArgs = ArgsBox.Text;
            k.UseProgramIcon = UseIconCheck.IsChecked == true;

            UpdateTile(_selected);
            C.RequestPush();
        }

        private void PickHotkey_Click(object sender, RoutedEventArgs e)
        {
            string? spec = KeyboardPickerWindow.Pick(this, HotkeyBox.Tag as string, combo: true);
            if (spec == null) return;
            HotkeyBox.Tag = spec;
            HotkeyBox.Text = KeySpec.Pretty(spec);
            Commit();
        }

        private void ClearHotkey_Click(object sender, RoutedEventArgs e)
        {
            HotkeyBox.Tag = "";
            HotkeyBox.Text = "";
            Commit();
        }

        private void BrowseProgram_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Programs and shortcuts|*.exe;*.lnk;*.bat;*.cmd|All files|*.*" };
            if (dlg.ShowDialog(this) == true) PathBox.Text = dlg.FileName;
        }

        private void ChooseImage_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null || _selected.IsSide) return;
            var dlg = new OpenFileDialog { Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*" };
            if (dlg.ShowDialog(this) == true) SetImage(_selected, dlg.FileName);
        }

        private void SetImage(KeyTile tile, string path)
        {
            if (tile.IsSide) return;
            try
            {
                tile.Config.ImageFile = ConfigStore.ImportImage(path);
                UpdateTile(tile);
                C.RequestPush();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"That file couldn't be used as a picture: {ex.Message}", "Image not set",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void RemoveImage_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null || _selected.IsSide) return;
            _selected.Config.ImageFile = null;
            UpdateTile(_selected);
            C.RequestPush();
        }

        private void ClearKey_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            _selected.Config.Reset();
            UpdateTile(_selected);
            LoadEditor();
            C.RequestPush();
        }

        // ───────────────────────── presets ─────────────────────────

        private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || ProfileList.SelectedItem is not Profile p) return;
            C.SetActiveProfile(p);
        }

        private void NewProfile_Click(object sender, RoutedEventArgs e)
        {
            string? name = PromptWindow.Ask(this, "New preset", "Name for the new preset:", "New preset");
            if (name != null) C.AddProfile(ConfigStore.NewDefaultProfile(name));
        }

        private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
        {
            var pkg = ConfigStore.Pack(C.ActiveProfile, C.Config, includeMacros: false, includeImages: false);
            var copy = ConfigStore.Unpack(pkg, C.Config);
            copy.Name = C.ActiveProfile.Name + " copy";
            C.AddProfile(copy);
        }

        private void RenameProfile_Click(object sender, RoutedEventArgs e)
        {
            string? name = PromptWindow.Ask(this, "Rename preset", "New name:", C.ActiveProfile.Name);
            if (name == null) return;
            C.ActiveProfile.Name = name;
            RefreshStatus();
        }

        private void DeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            if (C.Config.Profiles.Count <= 1)
            {
                MessageBox.Show(this, "You need at least one preset.", "Can't delete", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var answer = MessageBox.Show(this, $"Delete the preset '{C.ActiveProfile.Name}'? This can't be undone.",
                "Delete preset", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes) C.RemoveProfile(C.ActiveProfile);
        }

        private void ExportProfile_Click(object sender, RoutedEventArgs e)
        {
            string safe = string.Concat(C.ActiveProfile.Name.Split(Path.GetInvalidFileNameChars()));
            var dlg = new SaveFileDialog
            {
                FileName = string.IsNullOrWhiteSpace(safe) ? "preset" : safe,
                DefaultExt = ".vsdm18preset",
                Filter = "VSDM18 preset|*.vsdm18preset"
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                var pkg = ConfigStore.Pack(C.ActiveProfile, C.Config, includeMacros: true, includeImages: true);
                File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(pkg, ConfigStore.JsonOptions));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't save the file: {ex.Message}", "Export failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ImportProfile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "VSDM18 preset|*.vsdm18preset|All files|*.*" };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                var pkg = JsonSerializer.Deserialize<ConfigStore.ProfilePackage>(File.ReadAllText(dlg.FileName), ConfigStore.JsonOptions)
                          ?? throw new InvalidDataException("The file is empty.");
                C.AddProfile(ConfigStore.Unpack(pkg, C.Config));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"That file isn't a valid preset: {ex.Message}", "Import failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ───────────────────────── pages ─────────────────────────

        private void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || PageList.SelectedItem is not DeckPage page) return;
            C.SetActivePage(page);
        }

        private void AddPage_Click(object sender, RoutedEventArgs e)
        {
            string? name = PromptWindow.Ask(this, "Add page", "Name for the new page:", $"Page {C.ActiveProfile.Pages.Count + 1}");
            if (name != null) C.AddPage(name);
        }

        private void RenamePage_Click(object sender, RoutedEventArgs e)
        {
            string? name = PromptWindow.Ask(this, "Rename page", "New name:", C.ActivePage.Name);
            if (name == null) return;
            C.ActivePage.Name = name;
            RefreshStatus();
        }

        private void DeletePage_Click(object sender, RoutedEventArgs e)
        {
            if (C.ActiveProfile.Pages.Count <= 1)
            {
                MessageBox.Show(this, "A preset needs at least one page.", "Can't delete", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var answer = MessageBox.Show(this, $"Delete the page '{C.ActivePage.Name}'? This can't be undone.",
                "Delete page", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes) C.RemovePage(C.ActivePage);
        }

        // ───────────────────────── header & settings ─────────────────────────

        private async void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (C.State == LinkState.Connected) C.Disconnect();
            else await C.ConnectAsync();
        }

        private void Admin_Click(object sender, RoutedEventArgs e)
        {
            if (C.RestartAsAdmin()) Application.Current.Shutdown();
        }

        private void Lighting_Click(object sender, RoutedEventArgs e)
        {
            new LightingWindow { Owner = this }.ShowDialog();
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            new SettingsWindow { Owner = this }.ShowDialog();
        }

        private void Macros_Click(object sender, RoutedEventArgs e)
        {
            new MacroEditorWindow { Owner = this }.ShowDialog();
            foreach (var t in _tiles.Concat(_sideTiles)) UpdateTile(t);
        }
    }
}
