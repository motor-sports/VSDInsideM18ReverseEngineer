using System.Windows.Threading;

namespace VSDM18App;

public sealed record LedFrame(Rgb[] Ring, Rgb Left, Rgb Right, int Brightness);

/// <summary>
/// Works out what the deck's 22 ring LEDs and 2 side lights should show about 30 times a second:
/// the preset's fixed colours, or (with SimHub) RPM or the current track flag.
/// Identical frames are not re-sent to the deck.
/// </summary>
public sealed class LedEngine
{
    private const int RingCount = 22;

    private readonly AppController _c;
    private readonly DispatcherTimer _timer;

    public LedEngine(AppController controller)
    {
        _c = controller;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    public void Tick()
    {
        if (!_c.Device.IsConnected) return;

        var p = _c.ActiveProfile;
        var ring = new Rgb[RingCount];
        Array.Fill(ring, Rgb.Parse(p.RingColor, new Rgb(255, 0, 0)));
        Rgb left = Rgb.Parse(p.LeftColor, new Rgb(0, 255, 0));
        Rgb right = Rgb.Parse(p.RightColor, new Rgb(0, 0, 255));

        var sim = _c.SimHub.Latest;
        long now = Environment.TickCount64;
        bool live = _c.Config.SimHubEnabled && sim != null && sim.Running && now - sim.ReceivedAt < 2000;

        if (live)
        {
            if (p.RingMode == LedSource.Rpm)
                FillRpm(ring, sim!, p, now);
            else if (p.RingMode == LedSource.Flag && FlagColor(sim!.Flags, p, now) is Rgb ringFlag)
                Array.Fill(ring, ringFlag);

            if (p.SideMode == LedSource.Flag && FlagColor(sim!.Flags, p, now) is Rgb sideFlag)
            {
                left = sideFlag;
                right = sideFlag;
            }
            else if (p.SideMode == LedSource.Rpm)
            {
                Rgb shift = ShiftColor(sim!, p, now);
                left = shift;
                right = shift;
            }
        }

        _c.Device.SetLedFrame(ring, left, right, p.Brightness);
    }

    // ───────────────────────── RPM ─────────────────────────

    /// <summary>
    /// Works out where the engine is between the RPM start point and the redline.
    /// Returns false while it is still below the start point (lights stay off).
    /// </summary>
    /// <param name="ratio">RPM as a fraction of max RPM.</param>
    /// <param name="level">0 at the start point, 1 at the redline.</param>
    /// <param name="redline">True once RPM reaches the preset's redline.</param>
    public static bool RpmLevel(SimData s, Profile p, out double ratio, out double level, out bool redline)
    {
        double max = s.MaxRpm > 100 ? s.MaxRpm : 8000;
        ratio = Math.Clamp(s.Rpm / max, 0, 1.2);
        double start = Math.Clamp(p.RpmStartPercent, 0, 95) / 100.0;
        double limit = Math.Max(p.RedlinePercent / 100.0, start + 0.02);

        level = Math.Clamp((ratio - start) / (limit - start), 0, 1);
        redline = ratio >= limit;
        return ratio > start;
    }

    /// <summary>The colour for a point (0 to 1) along the RPM range, using the preset's colours and shares.</summary>
    public static Rgb Zone(double level, Profile p)
    {
        double low = p.RpmLowPercent, mid = p.RpmMidPercent, high = p.RpmHighPercent;
        double total = low + mid + high;
        if (total <= 0) { low = 60; mid = 25; high = 15; total = 100; }

        double t1 = low / total, t2 = (low + mid) / total;
        return level < t1 ? Rgb.Parse(p.RpmLowColor, new Rgb(0, 255, 0))
             : level < t2 ? Rgb.Parse(p.RpmMidColor, new Rgb(255, 170, 0))
             : Rgb.Parse(p.RpmHighColor, new Rgb(255, 0, 0));
    }

    private static bool FlashOn(Profile p, long now) => (now / Math.Max(50, p.RedlineBlinkMs)) % 2 == 0;

    private static void FillRpm(Rgb[] ring, SimData s, Profile p, long now)
    {
        Array.Fill(ring, Rgb.Black);
        if (!RpmLevel(s, p, out _, out double level, out bool redline)) return;

        // At the redline the whole ring flashes in the top colour (if flashing is switched on).
        if (redline && p.RedlineFlash)
        {
            Rgb high = Rgb.Parse(p.RpmHighColor, new Rgb(255, 0, 0));
            Array.Fill(ring, FlashOn(p, now) ? high : Rgb.Black);
            return;
        }

        // Static: the whole ring is one colour that changes with RPM.
        if (p.RpmStyle == RpmStyle.Solid)
        {
            Array.Fill(ring, Zone(level, p));
            return;
        }

        // Bar: LEDs light up one after another.
        int lit = (int)Math.Ceiling(level * RingCount);
        for (int i = 0; i < lit && i < RingCount; i++)
            ring[Physical(i, p)] = Zone((i + 0.5) / RingCount, p);
    }

    // Side lights act as a shift light: off, low, mid, high colour, flashing at the redline.
    private static Rgb ShiftColor(SimData s, Profile p, long now)
    {
        if (!RpmLevel(s, p, out _, out double level, out bool redline)) return Rgb.Black;

        if (redline && p.RedlineFlash)
            return FlashOn(p, now) ? Rgb.Parse(p.RpmHighColor, new Rgb(255, 0, 0)) : Rgb.Black;

        return Zone(level, p);
    }

    // Which physical LED is bar position i, given the start offset and direction.
    private static int Physical(int i, Profile p)
    {
        int j = p.RingReverse ? RingCount - 1 - i : i;
        return ((j + p.RingOffset) % RingCount + RingCount) % RingCount;
    }

    // ───────────────────────── flags ─────────────────────────

    /// <summary>
    /// Finds the flag to show (the highest priority one that is out) and how it should look right now.
    /// Returns false if no flag is out. "on" is false during the dark half of a blink.
    /// </summary>
    public static bool FlagInfo(SimFlags flags, Profile p, long now, out SimFlags top, out Rgb color, out bool on)
    {
        top = SimFlags.None;
        color = Rgb.Black;
        on = true;

        foreach (var f in Profile.FlagOrder)
        {
            if (!flags.HasFlag(f)) continue;

            top = f;
            var setting = p.GetFlagSetting(f);
            color = Rgb.Parse(setting.Color, new Rgb(255, 255, 255));
            on = !setting.Blink || (now / Math.Max(50, p.FlagBlinkMs)) % 2 == 0;
            return true;
        }
        return false;
    }

    // For the lights: the flag colour, black during the dark half of a blink, or null if no flag is out.
    private static Rgb? FlagColor(SimFlags flags, Profile p, long now)
    {
        if (!FlagInfo(flags, p, now, out _, out Rgb color, out bool on)) return null;
        return on ? color : Rgb.Black;
    }
}
