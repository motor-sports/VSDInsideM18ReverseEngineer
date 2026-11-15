using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace VSDM18App;

/// <summary>
/// Gets the icon of a program (.exe) or shortcut (.lnk) as a PNG, so "Open a program" keys can use it as their image.
/// Icons are cached in %AppData%\VSDM18App\icons and refreshed if the file changes.
/// </summary>
public static class ProgramIcon
{
    private static readonly string Dir = Path.Combine(ConfigStore.Dir, "icons");
    private static readonly Dictionary<string, string?> Known = new();
    private static readonly object Gate = new();

    /// <summary>Path of a PNG of the program's icon, or null if the file doesn't exist or has no icon.</summary>
    public static string? GetIconFile(string? programPath)
    {
        if (string.IsNullOrWhiteSpace(programPath)) return null;

        string path = Environment.ExpandEnvironmentVariables(programPath.Trim().Trim('"'));
        if (!File.Exists(path)) return null;

        string stamp = path.ToLowerInvariant() + "|" + File.GetLastWriteTimeUtc(path).Ticks;
        lock (Gate)
        {
            if (Known.TryGetValue(stamp, out var cached)) return cached;
        }

        string? result = null;
        try { result = Extract(path, stamp); }
        catch (Exception ex) { Debug.WriteLine($"[ProgramIcon] {ex.Message}"); }

        lock (Gate) Known[stamp] = result;
        return result;
    }

    private static string? Extract(string path, string stamp)
    {
        Directory.CreateDirectory(Dir);
        string file = Path.Combine(Dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(stamp)))[..16] + ".png");
        if (File.Exists(file)) return file;

        var (source, index) = ResolveSource(path);

        using Bitmap? bmp = FromFile(source, index) ?? FromAssociated(path);
        if (bmp == null) return null;

        bmp.Save(file, ImageFormat.Png);
        return file;
    }

    // For a shortcut, use the icon it names, or its target's icon. Anything else is used as it is.
    private static (string path, int index) ResolveSource(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return (path, 0);

        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return (path, 0);

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(path);

            string target = (string?)link.TargetPath ?? "";
            string location = (string?)link.IconLocation ?? "";

            int comma = location.LastIndexOf(',');
            string iconPath = comma >= 0 ? location.Substring(0, comma) : location;
            int index = 0;
            if (comma >= 0) int.TryParse(location.Substring(comma + 1), out index);

            iconPath = Environment.ExpandEnvironmentVariables(iconPath.Trim().Trim('"'));
            if (iconPath.Length > 0 && File.Exists(iconPath)) return (iconPath, index);
            if (target.Length > 0 && File.Exists(target)) return (target, 0);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProgramIcon] Couldn't read shortcut: {ex.Message}");
        }

        return (path, 0);
    }

    // Asks Windows for the icon at 128 px, so big icons stay sharp when shrunk to the 64 px key.
    private static Bitmap? FromFile(string source, int index)
    {
        var icons = new IntPtr[1];
        var ids = new uint[1];

        uint count = PrivateExtractIconsW(source, index, 128, 128, icons, ids, 1, 0);
        if (count == 0 || count == 0xFFFFFFFF || icons[0] == IntPtr.Zero) return null;

        try
        {
            using var icon = Icon.FromHandle(icons[0]);
            return icon.ToBitmap();
        }
        finally
        {
            DestroyIcon(icons[0]);
        }
    }

    // Fallback for files Windows can still give an icon for (like .bat files).
    private static Bitmap? FromAssociated(string path)
    {
        using var icon = Icon.ExtractAssociatedIcon(path);
        return icon?.ToBitmap();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIconsW(string fileName, int iconIndex, int cx, int cy,
        IntPtr[] icons, uint[] iconIds, uint count, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
