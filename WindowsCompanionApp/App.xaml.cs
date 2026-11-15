using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace VSDM18App
{
    public partial class App : Application
    {
        public static AppController Controller { get; private set; } = null!;

        // Makes Task.Delay accurate to about 1 ms instead of ~15 ms, which matters for macro timing.
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += (_, args) =>
            {
                Debug.WriteLine(args.Exception);
                MessageBox.Show(args.Exception.Message, "Something went wrong", MessageBoxButton.OK, MessageBoxImage.Warning);
                args.Handled = true;
            };

            timeBeginPeriod(1);

            var config = ConfigStore.Load();
            InputInjector.Mode = config.InputMode;
            Controller = new AppController(config);

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            ConfigStore.Flush();
            timeEndPeriod(1);
            base.OnExit(e);
        }
    }
}
