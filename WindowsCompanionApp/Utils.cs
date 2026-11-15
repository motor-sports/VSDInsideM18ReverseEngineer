using System.Net;
using System.Net.Sockets;

public static class Utils
{
    /// <summary>Looks for the Raspberry Pi by its usual hostnames, all at once. Returns its IPv4 address, or null.</summary>
    public static async Task<string?> FindRaspberryPiAsync()
    {
        string[] hostnames = { "raspberrypi.local", "pi.local", "raspberrypi" };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var pending = hostnames.Select(h => ResolveAsync(h, cts.Token)).ToList();

        try
        {
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending);
                pending.Remove(done);
                string? ip = await done;
                if (ip != null) return ip;
            }
            return null;
        }
        finally
        {
            cts.Cancel();
        }
    }

    private static async Task<string?> ResolveAsync(string name, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(name, ct);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
        }
        catch
        {
            return null;
        }
    }
}
