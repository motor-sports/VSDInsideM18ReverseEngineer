using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace VSDM18App;

/// <summary>
/// Everything is stored in %AppData%\VSDM18App\config.json (presets, pages, keys, macros, settings).
/// Key images are kept as normal files in %AppData%\VSDM18App\images.
/// Changes are saved automatically a moment after you stop editing.
/// </summary>
public static class ConfigStore
{
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VSDM18App");
    public static readonly string ImagesDir = Path.Combine(Dir, "images");
    private static readonly string FilePath = Path.Combine(Dir, "config.json");

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static AppConfig? _config;
    private static DispatcherTimer? _timer;

    // ───────── load / save ─────────

    public static AppConfig Load()
    {
        Directory.CreateDirectory(ImagesDir);

        AppConfig? cfg = null;
        try
        {
            if (File.Exists(FilePath))
                cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOptions);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ConfigStore] config.json unreadable: {ex.Message}");
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch { }
        }

        cfg ??= CreateFirstRun();
        Normalize(cfg);
        _config = cfg;
        PruneImages(cfg);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _timer.Tick += (_, _) => { _timer!.Stop(); Save(); };
        ObservableObject.Modified += MarkDirty;

        Save();
        return cfg;
    }

    public static void MarkDirty()
    {
        if (_timer == null) return;
        _timer.Stop();
        _timer.Start();
    }

    public static void Flush()
    {
        _timer?.Stop();
        Save();
    }

    private static void Save()
    {
        if (_config == null) return;
        try
        {
            Directory.CreateDirectory(Dir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_config, JsonOptions));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ConfigStore] Save failed: {ex.Message}");
        }
    }

    private static void Normalize(AppConfig cfg)
    {
        if (cfg.Profiles.Count == 0) cfg.Profiles.Add(NewDefaultProfile("Default"));

        foreach (var p in cfg.Profiles)
        {
            if (p.Pages.Count == 0) p.Pages.Add(new DeckPage { Name = "Page 1" });
            foreach (var page in p.Pages) page.EnsureKeys();
            foreach (int id in DeviceLayout.SideInputs) p.GetSideButton(id);
        }

        if (cfg.ActiveProfileId == null || cfg.Profiles.All(p => p.Id != cfg.ActiveProfileId))
            cfg.ActiveProfileId = cfg.Profiles[0].Id;
    }

    // ───────── first run / defaults ─────────

    private static AppConfig CreateFirstRun()
    {
        var cfg = new AppConfig();
        var profile = NewDefaultProfile("Default");
        TryImportLegacyImages(profile);
        cfg.Profiles.Add(profile);
        cfg.ActiveProfileId = profile.Id;
        return cfg;
    }

    /// <summary>
    /// New presets start with the same key bindings the old app used
    /// (F13-F24, then Numpad 0-2), so nothing changes for existing games.
    /// Side buttons: previous page, next page, next preset.
    /// </summary>
    public static Profile NewDefaultProfile(string name)
    {
        var p = new Profile { Name = name };
        var page = new DeckPage { Name = "Page 1" };
        page.EnsureKeys();

        for (int input = 1; input <= 15; input++)
        {
            var key = page.GetKey(DeviceLayout.InputToSlot[input]);
            key.Action = ActionType.Hotkey;
            key.Hotkey = input <= 12 ? $"F{12 + input}" : $"NumPad{input - 13}";
        }
        p.Pages.Add(page);

        p.GetSideButton(37).Action = ActionType.PreviousPage;
        p.GetSideButton(48).Action = ActionType.NextPage;
        p.GetSideButton(49).Action = ActionType.NextProfile;
        return p;
    }

    private sealed class LegacyImage
    {
        public byte Button { get; set; }
        public string ImageData { get; set; } = "";
    }

    /// <summary>Brings images over from the old saved_images.json, if there is one.</summary>
    private static void TryImportLegacyImages(Profile profile)
    {
        try
        {
            foreach (string dir in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            {
                string path = Path.Combine(dir, "saved_images.json");
                if (!File.Exists(path)) continue;

                var list = JsonSerializer.Deserialize<List<LegacyImage>>(File.ReadAllText(path)) ?? new();
                var page = profile.Pages[0];
                foreach (var item in list)
                {
                    if (item.Button < 1 || item.Button > 15 || string.IsNullOrEmpty(item.ImageData)) continue;
                    string name = $"legacy_{item.Button:x2}.jpg";
                    File.WriteAllBytes(Path.Combine(ImagesDir, name), Convert.FromBase64String(item.ImageData));
                    page.GetKey(item.Button).ImageFile = name;
                }
                return;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ConfigStore] Legacy import failed: {ex.Message}");
        }
    }

    // ───────── images ─────────

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    /// <summary>Copies an image into the app's folder and returns its stored file name.</summary>
    public static string ImportImage(string sourcePath)
    {
        // Throws if the file isn't a readable image.
        using (System.Drawing.Image.FromFile(sourcePath)) { }

        string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!ImageExtensions.Contains(ext)) ext = ".png";
        string name = Guid.NewGuid().ToString("N") + ext;
        Directory.CreateDirectory(ImagesDir);
        File.Copy(sourcePath, Path.Combine(ImagesDir, name), true);
        return name;
    }

    private static void PruneImages(AppConfig cfg)
    {
        try
        {
            var used = cfg.Profiles.SelectMany(p => p.AllKeys())
                .Select(k => k.ImageFile).Where(f => !string.IsNullOrEmpty(f)).ToHashSet();
            foreach (string file in Directory.GetFiles(ImagesDir))
                if (!used.Contains(Path.GetFileName(file))) File.Delete(file);
        }
        catch (Exception ex) { Debug.WriteLine($"[ConfigStore] Prune failed: {ex.Message}"); }
    }

    // ───────── export / import / duplicate ─────────

    public class ProfilePackage
    {
        public int Format { get; set; } = 1;
        public Profile Profile { get; set; } = new();
        public List<Macro> Macros { get; set; } = new();
        public Dictionary<string, string> Images { get; set; } = new();
    }

    private static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;

    public static ProfilePackage Pack(Profile source, AppConfig cfg, bool includeMacros, bool includeImages)
    {
        var pkg = new ProfilePackage { Profile = Clone(source) };
        var keys = pkg.Profile.AllKeys().ToList();

        if (includeMacros)
        {
            foreach (var id in keys.Where(k => k.MacroId != null).Select(k => k.MacroId!.Value).Distinct())
            {
                var m = cfg.Macros.FirstOrDefault(x => x.Id == id);
                if (m != null) pkg.Macros.Add(Clone(m));
            }
        }

        if (includeImages)
        {
            foreach (string f in keys.Select(k => k.ImageFile).Where(f => !string.IsNullOrEmpty(f)).Distinct()!)
            {
                string path = Path.Combine(ImagesDir, f);
                if (File.Exists(path)) pkg.Images[f] = Convert.ToBase64String(File.ReadAllBytes(path));
            }
        }
        return pkg;
    }

    /// <summary>Gives the package fresh ids and returns the profile (not yet added to cfg.Profiles).</summary>
    public static Profile Unpack(ProfilePackage pkg, AppConfig cfg)
    {
        var p = pkg.Profile;
        Guid oldId = p.Id;
        p.Id = Guid.NewGuid();

        var pageMap = new Dictionary<Guid, Guid>();
        foreach (var page in p.Pages)
        {
            var n = Guid.NewGuid();
            pageMap[page.Id] = n;
            page.Id = n;
        }

        var macroMap = new Dictionary<Guid, Guid>();
        foreach (var m in pkg.Macros)
        {
            var n = Guid.NewGuid();
            macroMap[m.Id] = n;
            m.Id = n;
            cfg.Macros.Add(m);
        }

        var imageMap = new Dictionary<string, string>();
        foreach (var (file, b64) in pkg.Images)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (!ImageExtensions.Contains(ext)) ext = ".png";
            string name = Guid.NewGuid().ToString("N") + ext;
            File.WriteAllBytes(Path.Combine(ImagesDir, name), Convert.FromBase64String(b64));
            imageMap[file] = name;
        }

        foreach (var k in p.AllKeys())
        {
            if (k.ImageFile != null && imageMap.TryGetValue(k.ImageFile, out var img)) k.ImageFile = img;
            if (k.MacroId is Guid mid && macroMap.TryGetValue(mid, out var nm)) k.MacroId = nm;
            if (k.TargetPageId is Guid pid) k.TargetPageId = pageMap.TryGetValue(pid, out var np) ? np : null;
            if (k.TargetProfileId is Guid tid)
                k.TargetProfileId = tid == oldId ? p.Id : (cfg.Profiles.Any(x => x.Id == tid) ? tid : null);
        }

        foreach (var page in p.Pages) page.EnsureKeys();
        foreach (int id in DeviceLayout.SideInputs) p.GetSideButton(id);
        return p;
    }
}
