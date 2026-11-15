using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Channels;

namespace VSDM18App.Networking
{
    public sealed class NetworkManager : IDisposable
    {
        public const int PacketSize = 1024;
        private const int TcpPort = 5000;

        private TcpClient? _client;
        private NetworkStream? _stream;

        private readonly CancellationTokenSource _cts = new();
        private readonly Channel<byte[]> _sendQueue =
            Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

        private int _pending;
        private int _disposed;
        private volatile bool _connected;

        public string RemoteHostName { get; private set; } = "";
        public bool IsConnected => _connected;
        public int PendingPackets => Volatile.Read(ref _pending);

        public event Action<byte[]>? PacketReceived;

        /// <summary>Raised once if the connection drops by itself (not when you call Dispose).</summary>
        public event Action? Disconnected;

        /// <param name="hostOverride">An IP or hostname. Blank = find the Pi automatically.</param>
        public async Task StartAsync(string? hostOverride = null)
        {
            string? host = string.IsNullOrWhiteSpace(hostOverride)
                ? await Utils.FindRaspberryPiAsync()
                : hostOverride.Trim();

            if (host == null)
                throw new InvalidOperationException(
                    "Couldn't find the Raspberry Pi on the network. Enter its IP address in the settings.");

            RemoteHostName = host;
            Debug.WriteLine($"[NetworkManager] Connecting to {host}:{TcpPort}");

            var client = new TcpClient { NoDelay = true };
            try
            {
                var connectTask = client.ConnectAsync(host, TcpPort);
                if (await Task.WhenAny(connectTask, Task.Delay(10000)) != connectTask)
                    throw new TimeoutException($"Connecting to {host} timed out.");
                await connectTask;
            }
            catch
            {
                client.Dispose();
                throw;
            }

            _client = client;
            _stream = client.GetStream();
            _connected = true;

            _ = Task.Run(SenderLoop);
            _ = Task.Run(ReceiveLoop);
        }

        private async Task ReceiveLoop()
        {
            var buf = new byte[4096];
            var stream = _stream!;

            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    int read = await stream.ReadAsync(buf.AsMemory(), _cts.Token);

                    if (read == 0)
                    {
                        Debug.WriteLine("[NetworkManager] Remote closed connection");
                        break;
                    }

                    // Forward exactly what arrived; HID input reports aren't a fixed size.
                    byte[] packet = new byte[read];
                    Buffer.BlockCopy(buf, 0, packet, 0, read);
                    PacketReceived?.Invoke(packet);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NetworkManager] RECEIVE FAILED: {ex.Message}");
            }

            if (!_cts.IsCancellationRequested) DisconnectInternal();
        }

        private async Task SenderLoop()
        {
            var stream = _stream!;
            try
            {
                await foreach (var packet in _sendQueue.Reader.ReadAllAsync(_cts.Token))
                {
                    await stream.WriteAsync(packet.AsMemory(0, PacketSize), _cts.Token);
                    Interlocked.Decrement(ref _pending);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NetworkManager] SEND FAILED: {ex.Message}");
                if (!_cts.IsCancellationRequested) DisconnectInternal();
            }
        }

        public void SendPacket(byte[] packet)
        {
            if (packet.Length != PacketSize)
                throw new ArgumentException("Packet must be exactly 1024 bytes.");

            if (!_connected)
                throw new InvalidOperationException("Not connected to device.");

            Interlocked.Increment(ref _pending);
            if (!_sendQueue.Writer.TryWrite(packet))
                Interlocked.Decrement(ref _pending);
        }

        /// <summary>Blocks until everything queued has been written (or the timeout passes).</summary>
        public void WaitForFlush(TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (PendingPackets > 0 && _connected && sw.Elapsed < timeout)
                Thread.Sleep(5);
            Thread.Sleep(20);
        }

        private void DisconnectInternal()
        {
            if (!_connected) return;
            _connected = false;

            Debug.WriteLine("[NetworkManager] Connection lost");

            try { _stream?.Dispose(); } catch { }
            try { _client?.Dispose(); } catch { }

            Disconnected?.Invoke();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            _connected = false;
            _cts.Cancel();
            _sendQueue.Writer.TryComplete();

            try { _stream?.Dispose(); } catch { }
            try { _client?.Dispose(); } catch { }
        }
    }
}
