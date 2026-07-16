using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;

namespace VSDM18App.Networking
{
    public class NetworkManager : IDisposable
    {
        public const int PacketSize = 1024;

        private string RemoteHost = "0.0.0.0";
        private const int TcpPort = 5000;

        private TcpClient? _client;
        private NetworkStream? _stream;

        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentQueue<byte[]> _sendQueue = new();

        private volatile bool _connected;

        public bool IsConnected => _connected;

        public event Action<byte[]>? PacketReceived;


        public async Task StartAsync()
        {
            RemoteHost = await Utils.FindRaspberryPiAsync();

            Debug.WriteLine(RemoteHost);

            try
            {
                Debug.WriteLine(
                    $"[NetworkManager] START ENTERED Instance={GetHashCode()}");


                _client = new TcpClient();

                Debug.WriteLine(
                    "[NetworkManager] Connecting...");


                var connectTask = _client.ConnectAsync(
                    RemoteHost,
                    TcpPort);


                var completedTask = await Task.WhenAny(
                    connectTask,
                    Task.Delay(10000));


                if (completedTask != connectTask)
                {
                    throw new TimeoutException(
                        "Connection timed out.");
                }


                await connectTask;


                _stream = _client.GetStream();


                _connected = true;


                Debug.WriteLine(
                    $"[NetworkManager] CONNECTED TRUE Instance={GetHashCode()}");


                _ = Task.Run(SenderLoop);
                _ = Task.Run(ReceiveLoop);


                Debug.WriteLine(
                    "[NetworkManager] START EXIT");

                Debug.WriteLine(
    $"[NetworkManager] CONNECTED TRUE Instance={GetHashCode()}");
            }
            catch (Exception ex)
            {
                _connected = false;

                Debug.WriteLine(
                    $"[NetworkManager] START FAILED: {ex}");

                throw;
            }
        }



        private async Task ReceiveLoop()
        {
            var buf = new byte[4096];

            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    int read = await _stream!.ReadAsync(
                        buf,
                        0,
                        buf.Length,
                        _cts.Token);

                    if (read == 0)
                    {
                        Debug.WriteLine(
                            "[NetworkManager] Remote closed connection");
                        break;
                    }

                    Debug.WriteLine(
                        $"[NetworkManager] Received {read} bytes");


                    // Forward exactly what arrived.
                    // HID input reports are not guaranteed to be 1024 bytes.
                    byte[] packet = new byte[read];

                    Buffer.BlockCopy(
                        buf,
                        0,
                        packet,
                        0,
                        read);


                    PacketReceived?.Invoke(packet);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[NetworkManager] RECEIVE FAILED: {ex.Message}");

                DisconnectInternal();
            }
        }



        private async Task SenderLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    if (_stream != null &&
                        _connected &&
                        _sendQueue.TryDequeue(out byte[]? packet))
                    {
                        await _stream.WriteAsync(
                            packet,
                            0,
                            PacketSize,
                            _cts.Token);
                    }
                    else
                    {
                        await Task.Delay(2);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[NetworkManager] SEND FAILED: {ex.Message}");

                DisconnectInternal();
            }
        }



        public void SendPacket(byte[] packet)
        {
            if (packet.Length != PacketSize)
            {
                throw new ArgumentException(
                    "Packet must be exactly 1024 bytes.");
            }


            if (!_connected)
            {
                Debug.WriteLine(
                    "[NetworkManager] Send blocked - not connected.");

                throw new InvalidOperationException(
                    "Not connected to device.");
            }


            _sendQueue.Enqueue(packet);
        }



        private void DisconnectInternal()
        {
            if (!_connected)
                return;


            Debug.WriteLine(
                $"[NetworkManager] DISCONNECT Instance={GetHashCode()}");


            _connected = false;


            try
            {
                _stream?.Dispose();
            }
            catch
            {
            }


            try
            {
                _client?.Close();
            }
            catch
            {
            }


            _stream = null;
            _client = null;
        }



        public void Dispose()
        {
            _connected = false;

            _cts.Cancel();

            DisconnectInternal();
        }
    }
}