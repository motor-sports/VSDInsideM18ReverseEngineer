using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

public static class Utils
{
    public static async Task<string?> FindRaspberryPiAsync()
    {
        string[] hostnames =
        {
            "raspberrypi.local",
            "pi.local",
            "raspberrypi"
        };

        foreach (var name in hostnames)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(name);
                var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

                if (ipv4 != null)
                    return ipv4.ToString();
            }
            catch
            {
            }
        }

        return null;
    }
}