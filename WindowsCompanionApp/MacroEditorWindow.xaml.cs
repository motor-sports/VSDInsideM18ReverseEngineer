using System.Windows;
using System.Windows.Controls;

namespace VSDM18App
{
    public partial class MacroEditorWindow : Window
    {
        private static readonly List<Option<MacroPlayMode>> Modes = new()
        {
            new(MacroPlayMode.Once, "Once per press"),
            new(MacroPlayMode.RepeatWhileHeld, "Repeats while held"),
            new(MacroPlayMode.ToggleRepeat, "Repeats until pressed again"),
        };

        private static readonly List<Option<MacroStepType>> StepTypes = new()
        {
            new(MacroStepType.KeyPress, "Press a key"),
            new(MacroStepType.KeyDown, "Hold a key down"),
            new(MacroStepType.KeyUp, "Release a key"),
            new(MacroStepType.Delay, "Wait"),
            new(MacroStepType.TypeText, "Type text"),
            new(MacroStepType.MouseClick, "Click a mouse button"),
            new(MacroStepType.MouseDown, "Hold a mouse button"),
            new(MacroStepType.MouseUp, "Release a mouse button"),
        };

        private static readonly string[] MouseButtons = { "Left", "Right", "Middle", "X1", "X2" };

        private AppController C => App.Controller;

        private Macro? _macro;
        private MacroStep? _step;
        private MacroRecorder? _recorder;
        private bool _loading;

        public MacroEditorWindow()
        {
            InitializeComponent();

            MacroList.ItemsSource = C.Config.Macros;
            ModeBox.ItemsSource = Modes;
            StepTypeBox.ItemsSource = StepTypes;
            StepMouseBox.ItemsSource = MouseButtons;

            KeyCapture.Attach(StepKeyBox, combo: false, spec =>
            {
                if (!_loading && _step != null) _step.Key = spec;
            });

            if (C.Config.Macros.Count > 0) MacroList.SelectedIndex = 0;
            else LoadMacro();
        }

        protected override void OnClosed(EventArgs e)
        {
            _recorder?.Dispose();
            _recorder = null;
            base.OnClosed(e);
        }

        private void Quiet(Action action)
        {
            bool previous = _loading;
            _loading = true;
            try { action(); }
            finally { _loading = previous; }
        }

        // ───────────────────────── macro list ─────────────────────────

        private void MacroList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _macro = MacroList.SelectedItem as Macro;
            LoadMacro();
        }

        private void LoadMacro()
        {
            Quiet(() =>
            {
                RightPane.IsEnabled = _macro != null;
                NameBox.Text = _macro?.Name ?? "";
                ModeBox.SelectedValue = _macro?.Mode;
                GapBox.Text = (_macro?.RepeatDelayMs ?? 20).ToString();
                StepList.ItemsSource = _macro?.Steps;
                UpdateModeHint();
            });
            LoadStep();
        }

        private void UpdateModeHint()
        {
            ModeHint.Text = (_macro?.Mode ?? MacroPlayMode.Once) switch
            {
                MacroPlayMode.RepeatWhileHeld => "Runs over and over while you hold the button, and stops when you let go.",
                MacroPlayMode.ToggleRepeat => "Starts on one press and keeps repeating until you press the button again.",
                _ => "Runs the steps once each time you press the button."
            };
            GapBox.IsEnabled = _macro != null && _macro.Mode != MacroPlayMode.Once;
        }

        private void NewMacro_Click(object sender, RoutedEventArgs e)
        {
            var m = new Macro { Name = $"Macro {C.Config.Macros.Count + 1}" };
            C.Config.Macros.Add(m);
            MacroList.SelectedItem = m;
        }

        private void CopyMacro_Click(object sender, RoutedEventArgs e)
        {
            if (_macro == null) return;
            var copy = new Macro { Name = _macro.Name + " copy", Mode = _macro.Mode, RepeatDelayMs = _macro.RepeatDelayMs };
            foreach (var s in _macro.Steps)
                copy.Steps.Add(new MacroStep { Type = s.Type, Key = s.Key, Ms = s.Ms, Text = s.Text });
            C.Config.Macros.Add(copy);
            MacroList.SelectedItem = copy;
        }

        private void DeleteMacro_Click(object sender, RoutedEventArgs e)
        {
            if (_macro == null) return;
            var answer = MessageBox.Show(this, $"Delete the macro '{_macro.Name}'? Keys that use it will stop doing anything.",
                "Delete macro", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            int index = MacroList.SelectedIndex;
            C.Config.Macros.Remove(_macro);
            if (C.Config.Macros.Count > 0)
                MacroList.SelectedIndex = Math.Min(index, C.Config.Macros.Count - 1);
        }

        private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_loading && _macro != null) _macro.Name = NameBox.Text;
        }

        private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _macro == null || ModeBox.SelectedValue is not MacroPlayMode mode) return;
            _macro.Mode = mode;
            UpdateModeHint();
        }

        private void GapBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_macro == null) return;
            if (int.TryParse(GapBox.Text, out int ms)) _macro.RepeatDelayMs = ms;
            GapBox.Text = _macro.RepeatDelayMs.ToString();
        }

        // ───────────────────────── steps ─────────────────────────

        private void StepList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _step = StepList.SelectedItem as MacroStep;
            LoadStep();
        }

        private void LoadStep()
        {
            Quiet(() =>
            {
                StepEditor.IsEnabled = _step != null;
                if (_step == null) { UpdateStepRows(MacroStepType.KeyPress); return; }

                StepTypeBox.SelectedValue = _step.Type;
                if (MacroStep.IsMouse(_step.Type))
                    StepMouseBox.SelectedItem = MouseButtons.Contains(_step.Key) ? _step.Key : "Left";
                else
                {
                    StepKeyBox.Tag = _step.Key;
                    StepKeyBox.Text = KeySpec.Pretty(_step.Key);
                }
                StepMsBox.Text = _step.Ms.ToString();
                StepTextBox.Text = _step.Text;
                UpdateStepRows(_step.Type);
            });
        }

        private void UpdateStepRows(MacroStepType t)
        {
            bool mouse = MacroStep.IsMouse(t);
            bool key = t is MacroStepType.KeyPress or MacroStepType.KeyDown or MacroStepType.KeyUp;

            KeyRow.Visibility = key ? Visibility.Visible : Visibility.Collapsed;
            MouseRow.Visibility = mouse ? Visibility.Visible : Visibility.Collapsed;
            TextRow.Visibility = t == MacroStepType.TypeText ? Visibility.Visible : Visibility.Collapsed;

            bool ms = t is MacroStepType.KeyPress or MacroStepType.Delay or MacroStepType.MouseClick;
            MsRow.Visibility = ms ? Visibility.Visible : Visibility.Collapsed;
            MsLabel.Text = t == MacroStepType.Delay ? "Wait (ms)" : "Hold (ms)";
        }

        private void StepTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _step == null || StepTypeBox.SelectedValue is not MacroStepType type) return;

            bool wasMouse = MacroStep.IsMouse(_step.Type);
            bool isMouse = MacroStep.IsMouse(type);
            if (wasMouse != isMouse) _step.Key = isMouse ? "Left" : "";
            _step.Type = type;
            LoadStep();
        }

        private void PickStepKey_Click(object sender, RoutedEventArgs e)
        {
            if (_step == null) return;
            string? spec = KeyboardPickerWindow.Pick(this, _step.Key, combo: false);
            if (spec == null) return;
            _step.Key = spec;
            StepKeyBox.Tag = spec;
            StepKeyBox.Text = KeySpec.Pretty(spec);
        }

        private void StepMouseBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _step == null || StepMouseBox.SelectedItem is not string button) return;
            _step.Key = button;
        }

        private void StepMsBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading || _step == null) return;
            if (int.TryParse(StepMsBox.Text, out int ms)) _step.Ms = ms;
        }

        private void StepTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_loading && _step != null) _step.Text = StepTextBox.Text;
        }

        private void AddStep_Click(object sender, RoutedEventArgs e)
        {
            if (_macro == null) return;
            var step = new MacroStep();
            int index = StepList.SelectedIndex;
            if (index >= 0 && index < _macro.Steps.Count) _macro.Steps.Insert(index + 1, step);
            else _macro.Steps.Add(step);
            StepList.SelectedItem = step;
        }

        private void RemoveStep_Click(object sender, RoutedEventArgs e)
        {
            if (_macro == null || _step == null) return;
            int index = StepList.SelectedIndex;
            _macro.Steps.Remove(_step);
            if (_macro.Steps.Count > 0) StepList.SelectedIndex = Math.Min(index, _macro.Steps.Count - 1);
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
        private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(+1);

        private void Move(int delta)
        {
            if (_macro == null) return;
            int i = StepList.SelectedIndex;
            int j = i + delta;
            if (i < 0 || j < 0 || j >= _macro.Steps.Count) return;
            _macro.Steps.Move(i, j);
            StepList.SelectedIndex = j;
        }

        private static bool SameKey(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private void Tidy_Click(object sender, RoutedEventArgs e)
        {
            if (_macro == null) return;
            var list = _macro.Steps.ToList();
            var result = new List<MacroStep>();

            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s.Type == MacroStepType.KeyDown)
                {
                    if (i + 1 < list.Count && list[i + 1].Type == MacroStepType.KeyUp && SameKey(list[i + 1].Key, s.Key))
                    {
                        result.Add(new MacroStep { Type = MacroStepType.KeyPress, Key = s.Key, Ms = 40 });
                        i += 1;
                        continue;
                    }
                    if (i + 2 < list.Count && list[i + 1].Type == MacroStepType.Delay &&
                        list[i + 2].Type == MacroStepType.KeyUp && SameKey(list[i + 2].Key, s.Key))
                    {
                        result.Add(new MacroStep { Type = MacroStepType.KeyPress, Key = s.Key, Ms = Math.Max(10, list[i + 1].Ms) });
                        i += 2;
                        continue;
                    }
                }
                result.Add(s);
            }

            _macro.Steps.Clear();
            foreach (var s in result) _macro.Steps.Add(s);
        }

        // ───────────────────────── record & test ─────────────────────────

        private void Record_Click(object sender, RoutedEventArgs e)
        {
            if (_recorder != null) { StopRecording(); return; }
            if (_macro == null) return;

            try
            {
                _recorder = new MacroRecorder();
                _recorder.StepRecorded += step =>
                {
                    _macro?.Steps.Add(step);
                    StepList.ScrollIntoView(step);
                };
                _recorder.Start();
            }
            catch (Exception ex)
            {
                _recorder = null;
                MessageBox.Show(this, ex.Message, "Can't record", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RecordButton.Content = "Stop recording";
            RecordHint.Visibility = Visibility.Visible;
            MacroList.IsEnabled = false;
        }

        private void StopRecording()
        {
            _recorder?.Dispose();
            _recorder = null;
            RecordButton.Content = "Record keys";
            RecordHint.Visibility = Visibility.Collapsed;
            MacroList.IsEnabled = true;
        }

        private async void Test_Click(object sender, RoutedEventArgs e)
        {
            if (_macro == null || _macro.Steps.Count == 0) return;
            var macro = _macro;

            TestButton.IsEnabled = false;
            try
            {
                for (int s = 3; s > 0; s--)
                {
                    TestButton.Content = $"Starting in {s}...";
                    await Task.Delay(1000);
                }
                MacroRun.Start(macro, loop: false);
            }
            finally
            {
                TestButton.Content = "Test in 3 seconds";
                TestButton.IsEnabled = true;
            }
        }
    }
}
