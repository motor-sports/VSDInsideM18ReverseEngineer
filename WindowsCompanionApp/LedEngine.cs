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
    public const double LimiterRatio = 0.97;   // at this fraction of max RPM the lights flash

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
            else if (p.RingMode == LedSource.Flag && FlagColor(sim!.Flags, now) is Rgb ringFlag)
                Array.Fill(ring, ringFlag);

            if (p.SideMode == LedSource.Flag && FlagColor(sim!.Flags, now) is Rgb sideFlag)
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

    // Returns false while the engine is below the start point (lights stay off).
    public static bool RpmLevel(SimData s, Profile p, out double ratio, out double level)
    {
        double max = s.MaxRpm > 100 ? s.MaxRpm : 8000;
        ratio = Math.Clamp(s.Rpm / max, 0, 1.2);
        double start = Math.Clamp(p.RpmStartPercent, 0, 95) / 100.0;

        level = Math.Clamp((ratio - start) / (LimiterRatio - start), 0, 1);
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

    private static void FillRpm(Rgb[] ring, SimData s, Profile p, long now)
    {
        Array.Fill(ring, Rgb.Black);
        if (!RpmLevel(s, p, out double ratio, out double level)) return;

        // Near the limiter the whole ring flashes in the top colour.
        if (ratio >= LimiterRatio)
        {
            Rgb high = Rgb.Parse(p.RpmHighColor, new Rgb(255, 0, 0));
            Array.Fill(ring, (now / 90) % 2 == 0 ? high : Rgb.Black);
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

    // Side lights act as a shift light: off, low, mid, high colour, flashing at the limiter.
    private static Rgb ShiftColor(SimData s, Profile p, long now)
    {
        if (!RpmLevel(s, p, out double ratio, out double level)) return Rgb.Black;

        if (ratio >= LimiterRatio)
            return (now / 90) % 2 == 0 ? Rgb.Parse(p.RpmHighColor, new Rgb(255, 0, 0)) : Rgb.Black;

        return Zone(level, p);
    }

    // Which physical LED is bar position i, given the start offset and direction.
    private static int Physical(int i, Profile p)
    {
        int j = p.RingReverse ? RingCount - 1 - i : i;
        return ((j + p.RingOffset) % RingCount + RingCount) % RingCount;
    }

    // ───────────────────────── flags ─────────────────────────

    private static Rgb? FlagColor(SimFlags f, long now)
    {
        bool blink = (now / 250) % 2 == 0;

        if (f.HasFlag(SimFlags.Checkered)) return blink ? new Rgb(255, 255, 255) : Rgb.Black;
        if (f.HasFlag(SimFlags.Black)) return blink ? new Rgb(255, 255, 255) : Rgb.Black;
        if (f.HasFlag(SimFlags.Red)) return new Rgb(255, 0, 0);
        if (f.HasFlag(SimFlags.Yellow)) return new Rgb(255, 190, 0);
        if (f.HasFlag(SimFlags.Blue)) return new Rgb(0, 70, 255);
        if (f.HasFlag(SimFlags.Orange)) return new Rgb(255, 100, 0);
        if (f.HasFlag(SimFlags.White)) return new Rgb(255, 255, 255);
        if (f.HasFlag(SimFlags.Green)) return new Rgb(0, 255, 0);
        return null;
    }
}
