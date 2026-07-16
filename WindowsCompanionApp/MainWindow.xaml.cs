using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using VSDM18App.Networking;
using static VSDM18App.HIDDeviceManager;

namespace VSDM18App
{
    public partial class MainWindow : Window
    {
        private static readonly NetworkManager networkManager = new NetworkManager();

        private static readonly HIDDeviceManager hid =
            new HIDDeviceManager(networkManager);

        private static Grid? g;


        public MainWindow()
        {
            InitializeComponent();

            // Change MainGrid to your actual XAML Grid name
            g = grid;

            Loaded += MainWindow_Loaded;
        }


        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await hid.ConnectAsync();

                hid.InitializeDevice();

                hid.SetLEDColor(
                    255, 0, 0,
                    0, 255, 0,
                    0, 0, 255);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[MainWindow] Initialization failed: {ex.Message}");
            }
        }


        protected override void OnClosing(CancelEventArgs e)
        {
            hid.Shutdown();
            hid.Disconnect();

            base.OnClosing(e);
        }


        private void I_Drop(object sender, DragEventArgs e)
        {
            FrameworkElement el = sender as FrameworkElement;

            string n = el.Name;
            n = n.Substring(1, 2);

            byte name = Convert.ToByte(n, 16);

            Debug.WriteLine(name);


            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] filePaths =
                    (string[])e.Data.GetData(DataFormats.FileDrop);


                if (filePaths.Length > 0)
                {
                    string droppedFilePath = filePaths[0];

                    hid.SetImage(
                        hid.ImageToByteArray(
                            hid.ResizeTo64x64(droppedFilePath)),
                        name);
                }
            }
        }


        private void I_RightClick(object sender, MouseButtonEventArgs e)
        {
            FrameworkElement el = sender as FrameworkElement;

            string n = el.Name;

            n = n.Substring(1, 2);

            byte name = Convert.ToByte(n, 16);

            Debug.WriteLine(name);


            hid.ClearImage(name);

            SetImageSource(
                el.Name,
                File.ReadAllBytes("unset.png"));
        }



        public static void SetImageSource(
            string name,
            byte[]? imageData = null)
        {
            Debug.WriteLine(
                $"Loading image source for {name}");


            if (g == null)
            {
                Debug.WriteLine(
                    "Grid reference is null");

                return;
            }


            if (name.Length == 3)
            {
                name = "I" + name.Substring(1).ToLower();
            }


            Image? image =
                g.FindName(name) as Image;


            if (image == null)
            {
                Debug.WriteLine(
                    $"Image {name} not found");

                return;
            }


            byte[]? storedImage = null;


            if (File.Exists("saved_images.json"))
            {
                string json =
                    File.ReadAllText("saved_images.json");


                if (!string.IsNullOrWhiteSpace(json))
                {
                    List<SavedImage> images =
                        JsonSerializer.Deserialize<List<SavedImage>>(json)
                        ?? new List<SavedImage>();


                    byte button =
                        Convert.ToByte(
                            name.Substring(1),
                            16);


                    SavedImage? saved =
                        images.FirstOrDefault(
                            x => x.Button == button);


                    if (saved != null)
                    {
                        storedImage =
                            Convert.FromBase64String(
                                saved.ImageData);
                    }
                }
            }


            if (storedImage == null ||
                storedImage.Length == 0)
            {
                if (imageData == null ||
                    imageData.Length == 0)
                {
                    Debug.WriteLine(
                        $"No image data for {name}");

                    return;
                }

                storedImage = imageData;
            }


            using MemoryStream ms =
                new MemoryStream(storedImage);


            BitmapImage bitmap =
                new BitmapImage();


            bitmap.BeginInit();
            bitmap.StreamSource = ms;
            bitmap.CacheOption =
                BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();


            image.Source = bitmap;
        }
    }
}