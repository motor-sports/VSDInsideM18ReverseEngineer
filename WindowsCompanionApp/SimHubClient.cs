using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace VSDM18App;

[Flags]
public enum SimFlags
{
    None = 0, Yellow = 1, Blue = 2, Black = 4, White = 8,
    Checkered = 16, Green = 32, Orange = 64, Red = 128
}

/// <summary>One reading from SimHub. ReceivedAt is Environment.TickCount64 when it arrived.</summary>
public sealed record SimData(bool Running, double Rpm, double MaxRpm, SimFlags Flags, long ReceivedAt,
                             string Gear = "", double Speed = 0);

/// <summary>
/// Polls SimHub's built-in web server (http://host:8888/api/GetGamedata, about 30 times a second)
/// and keeps the latest RPM and flag state. Turn SimHub's web server on in its settings if it isn't.
/// </summary>
public sealed class SimHubClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _cts;
    private string _url = "";
    private volatile SimData? _latest;

    public SimData? Latest => _latest;
    public string Status { get; private set; } = "Off";

    /// <summary>Raised on a background thread when Status changes.</summary>
    public event Action<string>? StatusChanged;

    /// <param name="address">"127.0.0.1:8888", "192.168.1.20" (port 8888 assumed) or a full http:// address.</param>
    public void Start(string? address)
    {
        string url = BuildUrl(address);
        if (_cts != null && url == _url) return;

        Stop();
        _url = url;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetStatus("Connecting to SimHub...");
        _ = Task.Run(() => Loop(url, ct));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _url = "";
        _latest = null;
        SetStatus("Off");
    }

    private static string BuildUrl(string? address)
    {
        string a = (address ?? "").Trim();
        if (a.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) a = a.Substring(7);
        a = a.TrimEnd('/');
        if (a.Length == 0) a = "127.0.0.1";
        if (!a.Contains(':')) a += ":8888";
        return $"http://{a}/api/GetGamedata";
    }

    private async Task Loop(string url, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var data = Parse(doc.RootElement);
                _latest = data;
                SetStatus(data.Running ? "Receiving game data" : "Connected, waiting for a game to start");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                _latest = null;
                SetStatus("Can't reach SimHub. Check the address and that SimHub's web server is on.");
                try { await Task.Delay(1500, ct); } catch (OperationCanceledException) { break; }
            }

            try { await Task.Delay(33, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private static SimData Parse(JsonElement root)
    {
        long now = Environment.TickCount64;

        if (!root.TryGetProperty("NewData", out var nd) || nd.ValueKind != JsonValueKind.Object)
            return new SimData(false, 0, 0, SimFlags.None, now);

        bool running = !root.TryGetProperty("GameRunning", out var gr) || gr.ValueKind != JsonValueKind.False;

        var flags = SimFlags.None;
        if (Num(nd, "Flag_Yellow") != 0) flags |= SimFlags.Yellow;
        if (Num(nd, "Flag_Blue") != 0) flags |= SimFlags.Blue;
        if (Num(nd, "Flag_Black") != 0) flags |= SimFlags.Black;
        if (Num(nd, "Flag_White") != 0) flags |= SimFlags.White;
        if (Num(nd, "Flag_Checkered") != 0) flags |= SimFlags.Checkered;
        if (Num(nd, "Flag_Green") != 0) flags |= SimFlags.Green;
        if (Num(nd, "Flag_Orange") != 0) flags |= SimFlags.Orange;
        if (Num(nd, "Flag_Red") != 0) flags |= SimFlags.Red;

        return new SimData(running, Num(nd, "Rpms"), Num(nd, "MaxRpm"), flags, now,
                           Text(nd, "Gear"), Num(nd, "SpeedKmh"));
    }

    private static string Text(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => ""
        };
    }

    private static double Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        switch (v.ValueKind)
        {
            case JsonValueKind.Number: return v.GetDouble();
            case JsonValueKind.True: return 1;
            case JsonValueKind.String:
                return double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;
            default: return 0;
        }
    }

    private void SetStatus(string status)
    {
        if (Status == status) return;
        Status = status;
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}
