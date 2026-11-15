using System.Diagnostics;
using System.Threading.Channels;
using VSDM18App.Networking;

namespace VSDM18App
{
    /// <summary>
    /// Talks to the deck through the Raspberry Pi relay: connection, key images, LEDs and key events.
    /// It knows nothing about presets or what a key should do. AppController decides that.
    /// </summary>
    public sealed class HIDDeviceManager : IDisposable
    {
        // ───────── protocol constants (unchanged from the original app) ─────────
        private static readonly byte[] CMD_CONNECT = { 0x43, 0x52, 0x54, 0x00, 0x00, 0x43, 0x4f, 0x4e, 0x4e, 0x45, 0x43, 0x54 };
        private static readonly byte[] CMD_LIG = { 0x43, 0x52, 0x54, 0x00, 0x00, 0x4c, 0x49, 0x47, 0x00, 0x00, 0x3c };
        private static readonly byte[] CMD_DIS = { 0x43, 0x52, 0x54, 0x00, 0x00, 0x44, 0x49, 0x53 };
        private static readonly byte[] CMD_QUCMD = { 0x43, 0x52, 0x54, 0x00, 0x00, 0x51, 0x55, 0x43, 0x4d, 0x44, 0x1f, 0x11, 0x00, 0x11, 0x00, 0x11 };
        private static readonly byte[] CMD_CLE_CD = { 0x43, 0x52, 0x54, 0x00, 0x00, 0x43, 0x4c, 0x45, 0x00, 0x00, 0x00, 0xff };
        private static readonly byte[] CMD_CLE = { 0x43, 0x52, 0x54, 0x00, 0x00, 0x43, 0x4c, 0x45, 0x00, 0x00, 0x00 };
        private static readonly byte[] CMD_STP = { 0x43, 0x52, 0x54, 0x00, 0x00, 0x53, 0x54, 0x50 };
        private static readonly byte[] CMD_LBLIG_3C = { 0x43, 0x52, 0x54, 0x00, 0x00, 0x4c, 0x42, 0x4c, 0x49, 0x47, 0x3c };
        private static readonly byte[] CMD_LBLIG_37 = { 0x43, 0x52, 0x54, 0x00, 0x00, 0x4c, 0x42, 0x4c, 0x49, 0x47, 0x37 };
        private static readonly byte[] CMD_SETLB_HEADER = { 0x43, 0x52, 0x54, 0x00, 0x00, 0x53, 0x45, 0x54, 0x4c, 0x42 };
        private static readonly byte[] CMD_BAT = { 0x43, 0x52, 0x54, 0x00, 0x00, 0x42, 0x41, 0x54, 0x00, 0x00 };
        private static readonly byte[] CMD_HAN = { 0x43, 0x52, 0x54, 0x00, 0x00, 0x48, 0x41, 0x4e };

        private const int ReportChunk = 512;

        // ───────── state ─────────
        private readonly object _stateLock = new();
        private readonly object _sendLock = new();
        private readonly object _pressedLock = new();

        private NetworkManager? _net;
        private CancellationTokenSource? _cts;
        private Channel<Action>? _work;

        private readonly HashSet<int> _pressed = new();
        private readonly Dictionary<byte, byte[]> _shown = new();   // what each key currently displays (worker thread only)
        private IReadOnlyDictionary<byte, byte[]?>? _pendingScreen;
        private LedFrame? _pendingLeds;
        private byte[]? _lastLeds;                                   // last LED packet sent (worker thread only)

        public bool IsConnected => _net?.IsConnected == true;
        public string? HostName => _net?.RemoteHostName;

        /// <summary>Fires (on a background thread) when a key goes down (true) or up (false). Ids are the device's input ids.</summary>
        public event Action<int, bool>? KeyStateChanged;

        /// <summary>Every raw report with a key id: (key, state). Useful for finding out what a button sends.</summary>
        public event Action<int, int>? RawReport;

        public event Action<bool>? ConnectionChanged;

        // ───────── connect / disconnect ─────────

        public async Task ConnectAsync(string? host)
        {
            if (IsConnected) return;
            TearDown(raise: false);

            var net = new NetworkManager();
            net.PacketReceived += OnPacketReceived;
            net.Disconnected += OnNetworkDisconnected;

            try
            {
                await net.StartAsync(host);
            }
            catch
            {
                net.PacketReceived -= OnPacketReceived;
                net.Disconnected -= OnNetworkDisconnected;
                net.Dispose();
                throw;
            }

            var cts = new CancellationTokenSource();
            var work = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });

            lock (_stateLock)
            {
                _net = net;
                _cts = cts;
                _work = work;
            }

            _shown.Clear();
            _lastLeds = null;
            lock (_pressedLock) _pressed.Clear();

            _ = Task.Run(() => WorkerLoop(work, cts.Token));

            try
            {
                InitializeDevice();
            }
            catch
            {
                TearDown(raise: false);
                throw;
            }

            _ = Task.Run(() => KeepAliveLoop(cts.Token));
            ConnectionChanged?.Invoke(true);
        }

        /// <param name="graceful">Clear the keys and say goodbye to the deck first.</param>
        public void Disconnect(bool graceful = true)
        {
            var net = _net;
            if (net == null) return;

            if (graceful && net.IsConnected)
            {
                try
                {
                    SendPacket(CMD_CLE_CD);
                    SendPacket(CMD_HAN);
                    net.WaitForFlush(TimeSpan.FromMilliseconds(500));
                }
                catch (Exception ex) { Debug.WriteLine($"[HID] Shutdown failed: {ex.Message}"); }
            }

            TearDown(raise: true);
        }

        private void OnNetworkDisconnected() => TearDown(raise: true);

        private void TearDown(bool raise)
        {
            NetworkManager? net;
            lock (_stateLock)
            {
                net = _net;
                _net = null;
                _cts?.Cancel();
                _cts = null;
                _work?.Writer.TryComplete();
                _work = null;
            }

            if (net != null)
            {
                net.PacketReceived -= OnPacketReceived;
                net.Disconnected -= OnNetworkDisconnected;
                net.Dispose();
            }

            // Anything still held down gets released, so keys can never get stuck.
            int[] stuck;
            lock (_pressedLock)
            {
                stuck = _pressed.ToArray();
                _pressed.Clear();
            }
            foreach (int id in stuck) KeyStateChanged?.Invoke(id, false);

            if (raise && net != null) ConnectionChanged?.Invoke(false);
        }

        private void InitializeDevice()
        {
            SendPacket(CMD_DIS);
            SendPacket(CMD_LIG);
            SendPacket(CMD_QUCMD);
            SendPacket(CMD_LIG);
            SendPacket(CMD_CLE_CD);
            SendPacket(CMD_CLE_CD);
            SendPacket(CMD_STP);
            SendPacket(CMD_LBLIG_3C);
            SendPacket(CMD_LBLIG_37);
            SendPacket(CMD_SETLB_HEADER);
            SendPacket(CMD_QUCMD);
            SendPacket(CMD_QUCMD);
            SendPacket(CMD_CLE_CD);
            SendPacket(CMD_CLE_CD);
            SendPacket(CMD_STP);
            SendPacket(CMD_CONNECT);
        }

        // ───────── background work (images, LEDs) ─────────

        private void Enqueue(Action action) => _work?.Writer.TryWrite(action);

        private static async Task WorkerLoop(Channel<Action> channel, CancellationToken ct)
        {
            try
            {
                await foreach (var action in channel.Reader.ReadAllAsync(ct))
                {
                    try { action(); }
                    catch (Exception ex) { Debug.WriteLine($"[HID] Work item failed: {ex.Message}"); }
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task KeepAliveLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(10000, ct);
                    if (IsConnected) SendPacket(CMD_CONNECT);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Debug.WriteLine($"[HID] Keep-alive stopped: {ex.Message}"); }
        }

        // ───────── screen ─────────

        /// <summary>
        /// Shows these images on the keys (null = blank key). Only keys that actually changed are re-sent,
        /// and if you call this faster than the deck can keep up, older requests are skipped.
        /// </summary>
        public void ShowScreen(IReadOnlyDictionary<byte, byte[]?> slots)
        {
            Interlocked.Exchange(ref _pendingScreen, slots);
            Enqueue(ApplyPendingScreen);
        }

        private void ApplyPendingScreen()
        {
            var screen = Interlocked.Exchange(ref _pendingScreen, null);
            if (screen == null) return;

            foreach (byte slot in DeviceLayout.Slots)
            {
                screen.TryGetValue(slot, out var wanted);
                _shown.TryGetValue(slot, out var current);

                if (wanted == null && current == null) continue;
                if (wanted != null && current != null && wanted.AsSpan().SequenceEqual(current)) continue;

                if (wanted == null)
                {
                    SendClear(slot);
                    _shown.Remove(slot);
                }
                else
                {
                    SendImage(slot, wanted);
                    _shown[slot] = wanted;
                }
            }
        }

        private void SendImage(byte slot, byte[] jpeg)
        {
            if (jpeg.Length > ushort.MaxValue)
                throw new ArgumentException("Image is too large for the deck.");

            lock (_sendLock)
            {
                var header = new byte[NetworkManager.PacketSize];
                Array.Copy(CMD_BAT, header, CMD_BAT.Length);
                header[CMD_BAT.Length] = (byte)(jpeg.Length >> 8);
                header[CMD_BAT.Length + 1] = (byte)(jpeg.Length & 0xFF);
                header[CMD_BAT.Length + 2] = slot;
                SendPacket(header);

                for (int offset = 0; offset < jpeg.Length; offset += NetworkManager.PacketSize)
                {
                    var chunk = new byte[NetworkManager.PacketSize];
                    Array.Copy(jpeg, offset, chunk, 0, Math.Min(NetworkManager.PacketSize, jpeg.Length - offset));
                    SendPacket(chunk);
                }

                SendPacket(CMD_STP);
            }
        }

        private void SendClear(byte slot)
        {
            lock (_sendLock)
            {
                var packet = new byte[NetworkManager.PacketSize];
                Array.Copy(CMD_CLE, packet, CMD_CLE.Length);
                packet[CMD_CLE.Length] = slot;
                SendPacket(packet);
            }
        }

        // ───────── LEDs ─────────

        /// <summary>
        /// Sets all 22 ring LEDs individually plus the left and right lights.
        /// Safe to call many times a second: only the newest frame is sent, and only if it changed.
        /// </summary>
        public void SetLedFrame(Rgb[] ring, Rgb left, Rgb right, int brightnessPercent)
        {
            Interlocked.Exchange(ref _pendingLeds, new LedFrame(ring, left, right, brightnessPercent));
            Enqueue(ApplyPendingLeds);
        }

        private void ApplyPendingLeds()
        {
            var frame = Interlocked.Exchange(ref _pendingLeds, null);
            if (frame == null) return;

            double factor = Math.Clamp(frame.Brightness, 0, 100) / 100.0;

            var packet = new byte[NetworkManager.PacketSize];
            Array.Copy(CMD_SETLB_HEADER, packet, CMD_SETLB_HEADER.Length);
            int o = CMD_SETLB_HEADER.Length;

            for (int i = 0; i < 22; i++)
                o = Put(packet, o, i < frame.Ring.Length ? frame.Ring[i] : Rgb.Black, factor);   // 22 ring LEDs
            o = Put(packet, o, frame.Left, factor);                                              // LED 23
            Put(packet, o, frame.Right, factor);                                                 // LED 24

            if (_lastLeds != null && packet.AsSpan().SequenceEqual(_lastLeds)) return;
            _lastLeds = packet;
            SendPacket(packet);
        }

        private static int Put(byte[] packet, int offset, Rgb c, double factor)
        {
            packet[offset] = Scale(c.R, factor);
            packet[offset + 1] = Scale(c.G, factor);
            packet[offset + 2] = Scale(c.B, factor);
            return offset + 3;
        }

        // The deck's LEDs top out at 0x78.
        private static byte Scale(byte v, double factor) => (byte)Math.Round(v / 255.0 * 0x78 * factor);

        // ───────── key input ─────────

        private void OnPacketReceived(byte[] packet)
        {
            // The relay can hand over two 512-byte reports in one read.
            for (int offset = 0; offset < packet.Length; offset += ReportChunk)
            {
                int len = Math.Min(ReportChunk, packet.Length - offset);
                ProcessReport(packet.AsSpan(offset, len));
            }
        }

        private void ProcessReport(ReadOnlySpan<byte> p)
        {
            if (p.Length < 12) return;

            int key = p[9];
            int state = p[10];

            RawReport?.Invoke(key, state);

            bool valid = (key >= 1 && key <= 15) || Array.IndexOf(DeviceLayout.SideInputs, key) >= 0;
            var released = new List<int>();
            bool pressedNow = false;

            lock (_pressedLock)
            {
                if (state == 1)
                {
                    if (valid && _pressed.Add(key)) pressedNow = true;
                }
                else if (state == 0)
                {
                    if (valid && _pressed.Remove(key))
                    {
                        released.Add(key);
                    }
                    else if (_pressed.Count > 0)
                    {
                        // Some reports don't say which key was let go: release everything held.
                        released.AddRange(_pressed);
                        _pressed.Clear();
                    }
                }
            }

            if (pressedNow) KeyStateChanged?.Invoke(key, true);
            foreach (int id in released) KeyStateChanged?.Invoke(id, false);
        }

        // ───────── sending ─────────

        private void SendPacket(byte[] data)
        {
            var net = _net;
            if (net == null || !net.IsConnected)
                throw new InvalidOperationException("Not connected to device.");

            if (data.Length > NetworkManager.PacketSize)
                throw new ArgumentException("Packet exceeds 1024 bytes.");

            byte[] packet = data;
            if (data.Length != NetworkManager.PacketSize)
            {
                packet = new byte[NetworkManager.PacketSize];
                Array.Copy(data, packet, data.Length);
            }

            net.SendPacket(packet);
        }

        public void Dispose() => Disconnect(graceful: false);
    }
}
