using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using System.Windows;
using VSDM18App.Networking;

namespace VSDM18App
{
    public class HIDDeviceManager : IDisposable
    {
        // Device identifiers
        private const int VID = 0x5548;
        private const int PID = 0x1000;
        private const int REPORT_SIZE = 64;

        // Command bytes
        private static readonly byte[] CMD_CONNECT = new byte[] { 0x43, 0x52, 0x54, 0x00, 0x00, 0x43, 0x4f, 0x4e, 0x4e, 0x45, 0x43, 0x54 }; // CONNECT
        private static readonly byte[] CMD_DIS = new byte[] { 0x43, 0x52, 0x54, 0x00, 0x00, 0x44, 0x49, 0x53 }; // DIS
        private static readonly byte[] CMD_LIG = new byte[] { 0x43, 0x52, 0x54, 0x00, 0x00, 0x4c, 0x49, 0x47, 0x00, 0x00, 0x3c }; // LIG
        private static readonly byte[] CMD_QUCMD = new byte[] { 0x43, 0x52, 0x54, 0x00, 0x00, 0x51, 0x55, 0x43, 0x4d, 0x44, 0x1f, 0x11, 0x00, 0x11, 0x00, 0x11 }; // QUCMD
        private static readonly byte[] CMD_CLE_CD = new byte[] { 0x43, 0x52, 0x54, 0x00, 0x00, 0x43, 0x4c, 0x45, 0x00, 0x00, 0x00, 0xff }; // CLE_CD
        private static readonly byte[] CMD_CLE = new byte[] { 0x43, 0x52, 0x54, 0x00, 0x00, 0x43, 0x4c, 0x45, 0x00, 0x00, 0x00 }; // CLE
        private static readonly byte[] CMD_STP = new byte[] { 0x43, 0x52, 0x54, 0x00, 0x00, 0x53, 0x54, 0x50 }; // STP
        private static readonly byte[] CMD_LBLIG_3C = new byte[] { 0x43, 0x52, 0x54, 0x00, 0x00, 0x4c, 0x42, 0x4c, 0x49, 0x47, 0x3c }; // LBLIG
        private static readonly byte[] CMD_LBLIG_37 = new byte[] { 0x43, 0x52, 0x54, 0x00, 0x00, 0x4c, 0x42, 0x4c, 0x49, 0x47, 0x37 }; // LBLIG
        private static readonly byte[] CMD_SETLB_HEADER = new byte[] { 0x43, 0x52, 0x54, 0x00, 0x00, 0x53, 0x45, 0x54, 0x4c, 0x42 }; // SETLB
        private static readonly byte[] CMD_BAT = new byte[] { 0x43, 0x52, 0x54, 0x00, 0x00, 0x42, 0x41, 0x54, 0x00, 0x00 }; // BAT
        private static readonly byte[] CMD_HAN = new byte[] { 0x43, 0x52, 0x54, 0x00, 0x00, 0x48, 0x41, 0x4e }; // HAN

        private class ButtonReference
        {
            public ButtonReference(int i, byte o)
            {
                this.inputId = i;
                this.outputId = o;
            }

            public int inputId;
            public byte outputId;
        }

        private static readonly ButtonReference[][] ButtonMap = new ButtonReference[][]
        {
            new ButtonReference[] { new ButtonReference(1, 0x0b) , new ButtonReference(2, 0x0c) , new ButtonReference(3, 0x0d) , new ButtonReference(4, 0x0e) , new ButtonReference(5, 0x0f) },
            new ButtonReference[] { new ButtonReference(6, 0x06) , new ButtonReference(7, 0x07) , new ButtonReference(8, 0x08) , new ButtonReference(9, 0x09) , new ButtonReference(10, 0x0a) },
            new ButtonReference[] { new ButtonReference(11, 0x01) , new ButtonReference(12, 0x02) , new ButtonReference(13, 0x03) , new ButtonReference(14, 0x04) , new ButtonReference(15, 0x05) }
        };

        private static readonly int[] PhysicalButtonMap = new int[]
        {
            37, 48, 49
        };

        // NetworkManager reference
        private Thread _keyInputThread;
        private Thread _keepAliveThread;
        private volatile bool _running = false;

        private readonly NetworkManager _networkManager;

        public HIDDeviceManager(NetworkManager networkManager)
        {
            _networkManager = networkManager;
        }

        // ============ CONNECTION ============

        public async Task ConnectAsync()
        {
            try
            {
                Debug.WriteLine("[HIDDeviceManager] Connecting...");
                await _networkManager.StartAsync();

                _running = true;
                Debug.WriteLine("[HIDDeviceManager] ✓ Connected to relay");

                // Subscribe to packet received events for key input handling
                _networkManager.PacketReceived += OnPacketReceived;

                _keyInputThread = new Thread(KeyInputLoop)
                {
                    IsBackground = true,
                    Name = "HID Key-Input"
                };
                _keyInputThread.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HIDDeviceManager] ✗ Failed to connect: {ex.Message}");
                throw new Exception($"Failed to connect to device: {ex.Message}", ex);
            }
        }

        // Backward compatible wrapper for non-async callers
        public void Connect()
        {
            ConnectAsync().Wait();
        }

        public void Disconnect()
        {
            _running = false;

            if (_keyInputThread?.IsAlive == true)
            {
                _keyInputThread.Join(1000);
            }

            if (_keepAliveThread?.IsAlive == true)
            {
                _keepAliveThread.Join(1000);
            }

            if (_networkManager != null)
            {
                _networkManager.PacketReceived -= OnPacketReceived;
                _networkManager.Dispose();
            }
        }

        // ============ INITIALIZATION ============

        public void InitializeDevice()
        {
            // Wait a bit for connection to fully establish
            int retries = 0;
            while (!_networkManager.IsConnected && retries < 10)
            {
                Debug.WriteLine("[HIDDeviceManager] Waiting for connection...");
                Thread.Sleep(500);
                retries++;
            }

            if (!_networkManager.IsConnected)
                throw new Exception("Device not connected - connection failed or timed out");

            Debug.WriteLine("[HIDDeviceManager] Initializing device...");

            SendPacket(CMD_DIS);
            SendPacket(CMD_LIG);
            SendPacket(CMD_QUCMD);
            SendPacket(CMD_LIG);
            SendPacket(CMD_CLE_CD);
            SendPacket(CMD_CLE_CD);
            SendPacket(CMD_STP);
            SendPacket(CMD_LBLIG_3C);
            SendPacket(CMD_LBLIG_37);
            SendPacket(CMD_SETLB_HEADER); // Prepare for LED data
            SendPacket(CMD_QUCMD);
            SendPacket(CMD_QUCMD);
            SendPacket(CMD_CLE_CD);
            SendPacket(CMD_CLE_CD);
            SendPacket(CMD_STP);
            SendPacket(CMD_CONNECT);

            _keepAliveThread = new Thread(KeepAliveLoop)
            {
                IsBackground = true,
                Name = "HID Keep-Alive"
            };
            _keepAliveThread.Start();

            // Note: Device initialization should be handled by the USB device firmware
            // This relay simply forwards commands - no initialization needed

            // Optional: Send initialization commands if needed by your device
            // SendPacket(CMD_CONNECT);

            // Restore any saved images
            try
            {
                RestoreImages();
                Debug.WriteLine("[HIDDeviceManager] ✓ Device initialized");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HIDDeviceManager] Warning: Error restoring images: {ex.Message}");
                // Don't fail initialization if image restore fails
            }
        }

        public void Shutdown()
        {
            if (!_networkManager.IsConnected)
                return;

            SendPacket(CMD_CLE_CD);
            SendPacket(CMD_HAN);
        }

        // ============ LED CONTROL ============

        public void SetLEDColor(int ring_r, int ring_b, int ring_g, int r, int g, int b, int r2, int g2, int b2)
        {
            if (!_networkManager.IsConnected)
                throw new Exception("Device not connected");

            // Scale colors from 0-255 to 0-120 (0x78)
            byte scaledRR = ScaleColor((byte)ring_r);
            byte scaledRG = ScaleColor((byte)ring_g);
            byte scaledRB = ScaleColor((byte)ring_b);

            byte scaledR = ScaleColor((byte)r);
            byte scaledG = ScaleColor((byte)g);
            byte scaledB = ScaleColor((byte)b);

            byte scaledR2 = ScaleColor((byte)r2);
            byte scaledG2 = ScaleColor((byte)g2);
            byte scaledB2 = ScaleColor((byte)b2);

            // Build LED packet
            byte[] packet = new byte[NetworkManager.PacketSize];

            // Copy header
            Array.Copy(CMD_SETLB_HEADER, packet, CMD_SETLB_HEADER.Length);

            // Fill with 22 black LEDs (0,0,0)
            int offset = CMD_SETLB_HEADER.Length;
            for (int i = 0; i < 22; i++)
            {
                packet[offset] = scaledRR;
                packet[offset + 1] = scaledRG;
                packet[offset + 2] = scaledRB;
                offset += 3;
            }

            // LED 23 (left)
            packet[offset] = scaledR;
            packet[offset + 1] = scaledG;
            packet[offset + 2] = scaledB;
            offset += 3;

            // LED 24 (right)
            packet[offset] = scaledR2;
            packet[offset + 1] = scaledG2;
            packet[offset + 2] = scaledB2;

            SendPacket(packet);
        }

        // ============ IMAGE CONTROL ============
        public System.Drawing.Image ResizeTo64x64(string imagePath)
        {
            using (System.Drawing.Image originalImage = System.Drawing.Image.FromFile(imagePath))
            {
                Bitmap resizedImage = new Bitmap(64, 64);

                using (Graphics graphics = Graphics.FromImage(resizedImage))
                {
                    graphics.InterpolationMode =
                        System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

                    graphics.DrawImage(originalImage, 0, 0, 64, 64);
                }

                return resizedImage;
            }
        }


        public byte[] ImageToByteArray(System.Drawing.Image imageIn)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                imageIn.Save(ms, ImageFormat.Jpeg);
                return ms.ToArray();
            }
        }


        public class SavedImage
        {
            public byte Button { get; set; }
            public string ImageData { get; set; }
        }


        private const int MAX_IMAGES = 15;
        private const string IMAGE_JSON = "saved_images.json";


        public void SaveImageToJson(byte button, byte[] imageBytes)
        {
            if (button < 1 || button > MAX_IMAGES)
                throw new ArgumentOutOfRangeException(nameof(button),
                    "Button must be between 1 and 15");


            List<SavedImage> images = new List<SavedImage>();


            // Load existing JSON
            if (File.Exists(IMAGE_JSON))
            {
                string json = File.ReadAllText(IMAGE_JSON);

                if (!string.IsNullOrWhiteSpace(json))
                {
                    images = JsonSerializer.Deserialize<List<SavedImage>>(json)
                             ?? new List<SavedImage>();
                }
            }


            // Remove existing image assigned to this button
            images.RemoveAll(x => x.Button == button);


            // Add replacement
            images.Add(new SavedImage
            {
                Button = button,
                ImageData = Convert.ToBase64String(imageBytes)
            });


            // Ensure max of 15 stored images
            if (images.Count > MAX_IMAGES)
            {
                images = images
                    .OrderBy(x => x.Button)
                    .Take(MAX_IMAGES)
                    .ToList();
            }


            // Keep entries ordered 1-15
            images = images
                .OrderBy(x => x.Button)
                .ToList();


            string output = JsonSerializer.Serialize(
                images,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });


            File.WriteAllText(IMAGE_JSON, output);
        }


        public void SetImage(byte[] imageData, byte dest_button, bool saveToJson = true)
        {
            List<byte> imageBytes = imageData.ToList();


            // Optional JSON save
            if (saveToJson)
            {
                SaveImageToJson(
                    dest_button,
                    imageBytes.ToArray()
                );
            }


            MainWindow.SetImageSource(
                $"I{dest_button:x2}",
                imageBytes.ToArray()
            );


            if (!_networkManager.IsConnected)
                throw new Exception("Device not connected");


            byte[] packet = new byte[NetworkManager.PacketSize];


            byte[] bytes = BitConverter.GetBytes(imageBytes.Count);

            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }


            Array.Copy(
                CMD_BAT,
                packet,
                CMD_BAT.Length
            );


            Array.Copy(
                bytes,
                2,
                packet,
                CMD_BAT.Length,
                2
            );


            packet[CMD_BAT.Length + 2] = dest_button;


            SendPacket(packet);


            while (imageBytes.Count > NetworkManager.PacketSize)
            {
                packet = imageBytes
                    .GetRange(0, NetworkManager.PacketSize)
                    .ToArray();


                imageBytes.RemoveRange(0, NetworkManager.PacketSize);


                SendPacket(packet);
            }


            // Pad final packet to 1024 bytes
            byte[] finalPacket = new byte[NetworkManager.PacketSize];
            Array.Copy(imageBytes.ToArray(), finalPacket, imageBytes.Count);
            SendPacket(finalPacket);


            SendPacket(CMD_STP);
        }

        public void RestoreImages()
        {
            if (!File.Exists(IMAGE_JSON))
                return;


            string json = File.ReadAllText(IMAGE_JSON);

            if (string.IsNullOrWhiteSpace(json))
                return;


            List<SavedImage> images =
                JsonSerializer.Deserialize<List<SavedImage>>(json)
                ?? new List<SavedImage>();


            for (byte button = 0x01; button <= 0x0F; button++)
            {
                SavedImage saved = images
                    .FirstOrDefault(x => x.Button == button);


                if (saved == null)
                    continue;


                byte[] imageBytes =
                    Convert.FromBase64String(saved.ImageData);


                SetImage(
                    imageBytes,
                    button,
                    false
                );
            }
        }

        public void ClearImage(byte dest_button)
        {
            Debug.WriteLine(
    $"[HIDDeviceManager] NetworkManager Instance={_networkManager.GetHashCode()} Connected={_networkManager.IsConnected}");

            if (!_networkManager.IsConnected)
                throw new Exception("Device not connected");


            // Send clear command to device
            byte[] packet = new byte[NetworkManager.PacketSize];

            Array.Copy(CMD_CLE, packet, CMD_CLE.Length);

            packet[CMD_CLE.Length] = dest_button;

            SendPacket(packet);


            // Remove image from JSON
            RemoveImageFromJson(dest_button);
        }


        private void RemoveImageFromJson(byte dest_button)
        {
            if (dest_button < 1 || dest_button > MAX_IMAGES)
                throw new ArgumentOutOfRangeException(nameof(dest_button),
                    "Button must be between 1 and 15");


            if (!File.Exists(IMAGE_JSON))
                return;


            string json = File.ReadAllText(IMAGE_JSON);

            if (string.IsNullOrWhiteSpace(json))
                return;


            List<SavedImage> images =
                JsonSerializer.Deserialize<List<SavedImage>>(json)
                ?? new List<SavedImage>();


            // Remove only this button's image
            images.RemoveAll(x => x.Button == dest_button);


            // Keep JSON ordered
            images = images
                .OrderBy(x => x.Button)
                .ToList();


            string output = JsonSerializer.Serialize(
                images,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });


            File.WriteAllText(IMAGE_JSON, output);
        }

        // ============ KEY INPUT HANDLING ============

        // ============ KEY INPUT HANDLING ============

        private int _lastKey = 0;


        private void OnPacketReceived(byte[] packet)
        {
            if (packet.Length > 512)
            {
                byte[] part1 = new byte[512];
                Buffer.BlockCopy(packet, 0, part1, 0, 512);
                byte[] part2 = new byte[512];
                Buffer.BlockCopy(packet, 512, part2, 0, 512);

                OnPacketReceived(part1);
                OnPacketReceived(part2);
                return;
            }

            if (packet == null || packet.Length < 12)
                return;


            int key = packet[9];
            int state = packet[10];


            Debug.WriteLine(
                $"KEY={key} STATE={state}"
            );


            Application.Current?.Dispatcher?.BeginInvoke(() =>
            {
                HandleKeyPacket(key, state);
            });
        }



        private void HandleKeyPacket(int key, int state)
        {
            // Key pressed
            if (state == 1)
            {
                if (key >= 1 && key <= 15)
                {
                    _lastKey = key;

                    EmulateKey(
                        key,
                        1
                    );
                }

                return;
            }


            // Key released
            if (state == 0)
            {
                if (_lastKey >= 1 && _lastKey <= 15)
                {
                    EmulateKey(
                        _lastKey,
                        0
                    );

                    _lastKey = 0;
                }
            }
        }

        private void KeyInputLoop()
        {
            // PacketReceived event handles the actual key input processing
            while (_running && _networkManager.IsConnected)
            {
                Thread.Sleep(50);
            }
        }

        private void KeepAliveLoop()
        {
            while (_running && _networkManager.IsConnected)
            {
                SendPacket(CMD_CONNECT);
                Thread.Sleep(10000);
            }
        }

        private byte GetVirtualKeyCode(int key)
        {
            switch (key)
            {
                case 1: return 0x7C; // F13
                case 2: return 0x7D; // F14
                case 3: return 0x7E; // F15
                case 4: return 0x7F; // F16
                case 5: return 0x80; // F17
                case 6: return 0x81; // F18
                case 7: return 0x82; // F19
                case 8: return 0x83; // F20
                case 9: return 0x84; // F21
                case 10: return 0x85; // F22
                case 11: return 0x86; // F23
                case 12: return 0x87; // F24

                case 13: return 0x60; // Numpad 0
                case 14: return 0x61; // Numpad 1
                case 15: return 0x62; // Numpad 2

                default:
                    throw new ArgumentOutOfRangeException(nameof(key));
            }
        }

        private void EmulateKey(int key, int state)
        {
            byte vk = GetVirtualKeyCode(key);


            if (state == 1)
            {
                Debug.WriteLine($"DOWN {key}");
                Keyboard.KeyDown(vk);
            }
            else
            {
                Debug.WriteLine($"UP {key}");
                Keyboard.KeyUp(vk);
            }
        }

        // ============ HELPERS ============

        private void SendPacket(byte[] data)
        {
            if (!_networkManager.IsConnected)
                throw new Exception("Not connected to device");

            try
            {
                // NetworkManager expects exactly 1024 bytes
                byte[] packet = new byte[Networking.NetworkManager.PacketSize];

                if (data.Length > Networking.NetworkManager.PacketSize)
                    throw new Exception("Packet exceeds 1024 bytes.");

                Array.Copy(data, packet, data.Length);

                _networkManager.SendPacket(packet);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to send packet: {ex.Message}", ex);
            }
        }

        private byte ScaleColor(byte value)
        {
            // Scale from 0-255 to 0-120 (0x78)
            return (byte)Math.Round((value / 255.0) * 0x78);
        }

        public void Dispose()
        {
            Disconnect();
            _networkManager?.Dispose();
        }
    }

    // Keep the Keyboard static class unchanged
    internal static class Keyboard
    {
        private const uint KEYEVENTF_KEYUP = 0x0002;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(
            byte bVk,
            byte bScan,
            uint dwFlags,
            System.UIntPtr dwExtraInfo);

        public static void KeyDown(byte vk)
        {
            byte scan = (byte)MapVirtualKey(vk, 0);
            keybd_event(vk, scan, 0, System.UIntPtr.Zero);
        }

        public static void KeyUp(byte vk)
        {
            byte scan = (byte)MapVirtualKey(vk, 0);
            keybd_event(vk, scan, KEYEVENTF_KEYUP, System.UIntPtr.Zero);
        }

        public static void PressKey(byte vk)
        {
            KeyDown(vk);
            KeyUp(vk);
        }
    }
}