namespace LiteMixerSetup;

internal enum Screen { Welcome, Working, Done, Failed, ConfirmRemove, Removing, Removed }

/// <summary>All installer screens: state, animation and drawing. Hosted by Window, or rendered offscreen for checks.</summary>
internal sealed class Ui
{
    public const int Width = 780;
    public const int Height = 480;

    const string Display = "Segoe UI Variable Display";
    const string DisplayBold = "Segoe UI Variable Display Semib";
    const string Body = "Segoe UI Variable Text";
    const string BodyStrong = "Segoe UI Semibold";

    const uint Background = 0xFF0E1014;
    const uint White = 0xFFF5F6F8;
    const uint Soft = 0xFFA4ACB8;
    const uint Dim = 0xFF656D79;
    const uint Track = 0xFF1C2027;
    const uint Blue = 0xFF5B6CF5;
    const uint Violet = 0xFF9D5CF5;
    const uint Teal = 0xFF1FCB9C;
    const uint Danger = 0xFFE5484D;

    static readonly (string Name, uint Color)[] Channels =
    [
        ("MASTER", 0xFF5B6CF5),
        ("GAME", 0xFF1FCB9C),
        ("CHAT", 0xFF3BA4F0),
        ("VIDEO", 0xFFF2468E),
        ("MUSIC", 0xFF9D5CF5),
        ("MIC", 0xFFF5A623),
    ];

    // Each fader drifts at its own pace so the idle motion never looks like a loop.
    static readonly double[] Speeds = [0.53, 0.71, 0.43, 0.89, 0.61, 0.37];
    static readonly double[] Phases = [0.4, 2.1, 4.0, 1.2, 3.1, 5.2];
    static readonly double[] Rest = [0.72, 0.50, 0.42, 0.34, 0.58, 0.64];

    public enum Hit { None, Primary, Secondary, Toggle, Close, Minimize, License, Location }

    readonly List<(Hit Id, float X, float Y, float W, float H)> _hits = new();
    readonly Dictionary<Hit, double> _hover = new();
    readonly double[] _levels = new double[6];
    readonly bool _reducedMotion;

    double _time;
    double _screenAge;
    Screen _screen;

    public Ui(Screen screen, bool toggle, bool updating, bool reducedMotion)
    {
        _screen = screen;
        ToggleOn = toggle;
        Updating = updating;
        _reducedMotion = reducedMotion;
        for (int i = 0; i < 6; i++) _levels[i] = screen is Screen.Done or Screen.Removing ? 1 : Rest[i];
    }

    public Screen Screen
    {
        get => _screen;
        set
        {
            if (_screen == value) return;
            _screen = value;
            _screenAge = 0;
        }
    }

    public bool ToggleOn { get; set; }
    public bool Updating { get; }
    public string ErrorText { get; set; } = "";
    public string LocationText { get; set; } = "";
    public string LocationProblem { get; set; } = "";
    public double Progress { get; set; }
    public Hit Hot { get; set; }
    public Hit Pressed { get; set; }
    public Hit Focused { get; set; }

    public bool CanClose => _screen is not (Screen.Working or Screen.Removing);

    public Hit HitTest(float x, float y)
    {
        foreach (var item in _hits)
            if (x >= item.X && x < item.X + item.W && y >= item.Y && y < item.Y + item.H) return item.Id;
        return Hit.None;
    }

    /// <summary>The controls on this screen in Tab order.</summary>
    public IEnumerable<Hit> Focusable()
    {
        foreach (var item in _hits)
            if (item.Id is Hit.Primary or Hit.Toggle or Hit.Secondary or Hit.Location) yield return item.Id;
    }

    public void Advance(double dt)
    {
        _time += dt;
        _screenAge += dt;
        foreach (var id in new[] { Hit.Primary, Hit.Secondary, Hit.Toggle, Hit.Close, Hit.Minimize, Hit.License, Hit.Location })
        {
            double target = Hot == id ? 1 : 0;
            double now = _hover.GetValueOrDefault(id);
            _hover[id] = now + (target - now) * Math.Min(1, dt * 16);
        }

        double follow = _screen is Screen.Working or Screen.Removing ? 7 : 3;
        for (int i = 0; i < 6; i++)
            _levels[i] += (TargetLevel(i) - _levels[i]) * Math.Min(1, dt * follow);
    }

    /// <summary>Settles every animation, for offscreen rendering.</summary>
    public void Settle(double time)
    {
        _time = time;
        _screenAge = 10;
        for (int i = 0; i < 6; i++) _levels[i] = TargetLevel(i);
    }

    double TargetLevel(int i)
    {
        switch (_screen)
        {
            case Screen.Working:
                // The faders fill left to right, so progress reads at a glance.
                return Math.Clamp(Progress * 6 - i, 0, 1);
            case Screen.Removing:
                return Math.Clamp((1 - Progress) * 6 - i, 0, 1);
            case Screen.Done:
                return 1;
            case Screen.Removed:
                return 0;
            case Screen.Failed:
                return Rest[i] * 0.4;
            default:
                if (_reducedMotion) return Rest[i];
                return Math.Clamp(Rest[i] + 0.15 * Math.Sin(_time * Speeds[i] + Phases[i])
                                          + 0.05 * Math.Sin(_time * Speeds[i] * 2.7 + Phases[i] * 2), 0.06, 0.96);
        }
    }

    public void Draw(Gfx g)
    {
        _hits.Clear();
        g.Clear(Background);
        DrawBackdrop(g);
        DrawFaders(g);

        // Each screen fades and rises in; reduced-motion just shows it.
        double t = _reducedMotion ? 1 : Math.Clamp(_screenAge / 0.32, 0, 1);
        double eased = 1 - Math.Pow(1 - t, 3);
        float rise = (float)((1 - eased) * 12);

        DrawMark(g, 48, 44);
        switch (_screen)
        {
            case Screen.Welcome: DrawWelcome(g, rise, eased); break;
            case Screen.Working: DrawProgress(g, Updating ? "Updating" : "Installing", rise, eased); break;
            case Screen.Done: DrawDone(g, rise, eased); break;
            case Screen.Failed: DrawFailed(g, rise, eased); break;
            case Screen.ConfirmRemove: DrawConfirmRemove(g, rise, eased); break;
            case Screen.Removing: DrawProgress(g, "Removing", rise, eased); break;
            case Screen.Removed: DrawRemoved(g, rise, eased); break;
        }
        DrawWindowButtons(g);
    }

    void DrawBackdrop(Gfx g)
    {
        // Slow-moving colored light behind the faders.
        double drift = _reducedMotion ? 0 : _time * 0.12;
        g.Glow(590 + (float)(Math.Sin(drift) * 40), 150 + (float)(Math.Cos(drift * 1.3) * 30), 340, 260, 0x305B6CF5);
        g.Glow(690 + (float)(Math.Cos(drift * 0.8) * 30), 390 + (float)(Math.Sin(drift) * 24), 300, 220, 0x24F2468E);
        g.Glow(470 + (float)(Math.Sin(drift * 1.2) * 30), 420, 260, 190, 0x201FCB9C);
    }

    void DrawFaders(Gfx g)
    {
        const float top = 74, bottom = 396, knobH = 20, knobW = 30;
        float span = bottom - top - knobH;
        double dim = _screen == Screen.Failed ? 0.45 : 1;

        for (int i = 0; i < 6; i++)
        {
            float x = 448 + i * 54;
            var (name, color) = Channels[i];
            float level = (float)_levels[i];
            float knobY = top + (1 - level) * span;

            g.Glow(x, knobY + knobH / 2, 42, 70, Gfx.Fade(color, 0.28 * dim * Math.Max(level, 0.25)));
            g.RoundRect(x - 2.5f, top, 5, bottom - top, 2.5f, Track);

            float fillTop = knobY + knobH / 2;
            if (bottom - fillTop > 1)
                g.RoundRectGradient(x - 2.5f, fillTop, 5, bottom - fillTop, 2.5f,
                    Gfx.Fade(color, dim), Gfx.Fade(color, 0.25 * dim), vertical: true);

            g.RoundRect(x - knobW / 2, knobY + 1.5f, knobW, knobH, 5, 0x60000000);
            g.RoundRect(x - knobW / 2, knobY, knobW, knobH, 5, Gfx.Fade(0xFFF3F5F8, _screen == Screen.Failed ? 0.6 : 1));
            g.RoundRect(x - 7, knobY + knobH / 2 - 1, 14, 2, 1, color);

            g.Text(name, x - 27, bottom + 14, 54, 14, BodyStrong, 9.5f, Gfx.Fade(color, 0.85 * dim), Gfx.Align.Center);
        }
    }

    static void DrawMark(Gfx g, float x, float y)
    {
        g.RoundRect(x, y, 34, 34, 9, 0xFF1C2027);
        uint[] colors = [0xFF1FCB9C, 0xFF3BA4F0, 0xFFF2468E];
        float[] knobs = [0.40f, 0.68f, 0.50f];
        for (int i = 0; i < 3; i++)
        {
            float fx = x + 10 + i * 7;
            g.RoundRect(fx - 1, y + 8, 2, 18, 1, colors[i]);
            g.RoundRect(fx - 2.6f, y + 8 + (1 - knobs[i]) * 14, 5.2f, 4, 1.2f, 0xFFF3F5F8);
        }
        g.Text("LiteMixer", x + 44, y + 7, 200, 22, BodyStrong, 15, White);
    }

    void DrawWelcome(Gfx g, float rise, double o)
    {
        Headline(g, Updating ? "Update\nLiteMixer." : "Just the\nmixer.", rise, o);
        Paragraph(g, Updating
            ? "Installs the latest version. Your channels, profiles and hotkeys stay exactly as you left them."
            : "Channels, per-app routing, game and chat balance, and hotkeys. No drivers, no account.", 256 + rise, o);

        PrimaryButton(g, Updating ? "Update now" : "Install", 48, 332 + rise, 172, o, Blue, Violet);
        Toggle(g, "Desktop shortcut", 240, 346 + rise, o);
        // An update stays where the app already is.
        if (!Updating) LocationRow(g, 48, 302 + rise, o);
        LicenseLine(g, 48, 404 + rise, o);
        Footer(g, $"Version {Installer.Version}  ·  {Installer.Publisher}", o);
    }

    void DrawProgress(Gfx g, string verb, float rise, double o)
    {
        g.Text(verb, 48, 128 + rise, 340, 26, BodyStrong, 16, Gfx.Fade(Soft, o));
        g.Text($"{Math.Round(Progress * 100):0}%", 45, 148 + rise, 340, 124, DisplayBold, 86, Gfx.Fade(White, o));

        float y = 272 + rise;
        g.RoundRect(48, y, 300, 4, 2, Gfx.Fade(Track, o));
        if (Progress > 0.005)
            g.RoundRectGradient(48, y, 300 * (float)Progress, 4, 2, Gfx.Fade(Blue, o), Gfx.Fade(Violet, o), vertical: false);
        Paragraph(g, "This only takes a moment. Your audio keeps playing.", 292 + rise, o);
    }

    void DrawDone(Gfx g, float rise, double o)
    {
        Check(g, 48, 122 + rise, o);
        Headline(g, "You're all set.", rise + 72, o, single: true);
        Paragraph(g, "LiteMixer starts with Windows and sits in your tray. Turn that off anytime from its menu.", 256 + rise, o);
        PrimaryButton(g, "Open LiteMixer", 48, 332 + rise, 196, o, Blue, Violet);
        SecondaryButton(g, "Close", 262, 332 + rise, 90, o);
    }

    void DrawFailed(Gfx g, float rise, double o)
    {
        Headline(g, "That didn't\nwork.", rise, o);
        Paragraph(g, ErrorText, 256 + rise, o);
        PrimaryButton(g, "Try again", 48, 348 + rise, 172, o, Blue, Violet);
        SecondaryButton(g, "Close", 238, 348 + rise, 90, o);
    }

    void DrawConfirmRemove(Gfx g, float rise, double o)
    {
        Headline(g, "Remove\nLiteMixer?", rise, o);
        Paragraph(g, "Takes the app, its shortcuts and its startup entry off this PC.", 256 + rise, o);
        Toggle(g, "Also delete my settings and profiles", 48, 310 + rise, o);
        PrimaryButton(g, "Remove", 48, 358 + rise, 150, o, Danger, 0xFFC2353A);
        SecondaryButton(g, "Keep it", 216, 358 + rise, 100, o);
    }

    void DrawRemoved(Gfx g, float rise, double o)
    {
        Check(g, 48, 122 + rise, o);
        Headline(g, "All gone.", rise + 72, o, single: true);
        Paragraph(g, "App volumes stay where they are in Windows. You can change them under Settings > System > Sound > Volume mixer.", 256 + rise, o);
        PrimaryButton(g, "Close", 48, 348 + rise, 130, o, 0xFF2A2F37, 0xFF2A2F37);
    }

    static void Headline(Gfx g, string text, float rise, double o, bool single = false)
    {
        g.Text(text, 45, (single ? 120 : 102) + rise, 380, single ? 70 : 160, DisplayBold, single ? 44 : 54, Gfx.Fade(White, o));
    }

    static void Paragraph(Gfx g, string text, float y, double o)
    {
        g.Text(text, 48, y, 340, 64, Body, 14.5f, Gfx.Fade(Soft, o));
    }

    void LocationRow(Gfx g, float x, float y, double o)
    {
        const float changeX = 292, changeW = 50;
        bool problem = LocationProblem.Length > 0;
        g.Text(problem ? LocationProblem : $"Install to {LocationText}", x, y, changeX - 4, 18, Body, 12, Gfx.Fade(problem ? Danger : Dim, o));

        _hits.Add((Hit.Location, x + changeX, y - 4, changeW, 24));
        double hover = _hover.GetValueOrDefault(Hit.Location);
        g.Text("Change", x + changeX, y, changeW, 18, BodyStrong, 12, Gfx.Fade(hover > 0.5 ? White : Soft, o), Gfx.Align.Right);
        FocusRing(g, Hit.Location, x + changeX, y - 1, changeW, 18, 4, o);
    }

    void LicenseLine(Gfx g, float x, float y, double o)
    {
        const string prefix = "By installing, you agree to the ";
        const string link = "LiteMixer License";
        _hits.Add((Hit.License, x + 160, y - 2, 104, 20));
        double hover = _hover.GetValueOrDefault(Hit.License);
        g.Text(prefix, x, y, 180, 18, Body, 12, Gfx.Fade(Dim, o));
        g.Text(link, x + 160, y, 110, 18, BodyStrong, 12, Gfx.Fade(hover > 0.5 ? White : Soft, o));
    }

    static void Footer(Gfx g, string text, double o)
    {
        g.Text(text, 48, 432, 360, 18, Body, 12, Gfx.Fade(Dim, o));
    }

    static void Check(Gfx g, float x, float y, double o)
    {
        g.Ellipse(x + 22, y + 22, 22, 22, Gfx.Fade(Teal, o));
        uint ink = Gfx.Fade(Background, o);
        g.Line(x + 12.5f, y + 22.5f, x + 19.5f, y + 29.5f, 3.4f, ink);
        g.Line(x + 19.5f, y + 29.5f, x + 32, y + 15.5f, 3.4f, ink);
    }

    void PrimaryButton(Gfx g, string text, float x, float y, float w, double o, uint from, uint to)
    {
        const float h = 50;
        _hits.Add((Hit.Primary, x, y, w, h));
        double hover = _hover.GetValueOrDefault(Hit.Primary);
        bool down = Pressed == Hit.Primary && Hot == Hit.Primary;
        float inset = down ? 1.5f : 0;

        if (from != 0xFF2A2F37)
            g.Glow(x + w / 2, y + h / 2 + 8, w * 0.72f, h * 1.15f, Gfx.Fade(from, (0.30 + 0.28 * hover) * o));
        g.RoundRectGradient(x + inset, y + inset, w - inset * 2, h - inset * 2, 13, Gfx.Fade(Lighten(from, hover), o), Gfx.Fade(Lighten(to, hover), o), vertical: false);
        g.Text(text, x, y + 14, w, 24, BodyStrong, 15.5f, Gfx.Fade(0xFFFFFFFF, o), Gfx.Align.Center);
        FocusRing(g, Hit.Primary, x, y, w, h, 13, o);
    }

    void SecondaryButton(Gfx g, string text, float x, float y, float w, double o)
    {
        const float h = 50;
        _hits.Add((Hit.Secondary, x, y, w, h));
        double hover = _hover.GetValueOrDefault(Hit.Secondary);
        g.RoundRect(x, y, w, h, 13, Gfx.Fade(0xFF1C2027, (0.55 + 0.45 * hover) * o));
        g.Text(text, x, y + 15, w, 24, BodyStrong, 14, Gfx.Fade(Soft, o), Gfx.Align.Center);
        FocusRing(g, Hit.Secondary, x, y, w, h, 13, o);
    }

    void Toggle(Gfx g, string text, float x, float y, double o)
    {
        const float w = 38, h = 22;
        _hits.Add((Hit.Toggle, x, y - 2, 330 - (x - 48), h + 4));
        double hover = _hover.GetValueOrDefault(Hit.Toggle);
        uint off = 0xFF2A2F37;
        g.RoundRect(x, y, w, h, 11, Gfx.Fade(ToggleOn ? Blue : off, (0.85 + 0.15 * hover) * o));
        float knob = ToggleOn ? x + w - 11 : x + 11;
        g.Ellipse(knob, y + 11, 8, 8, Gfx.Fade(0xFFF5F6F8, o));
        g.Text(text, x + w + 10, y + 2.5f, 280, 20, Body, 13.5f, Gfx.Fade(Soft, o));
        FocusRing(g, Hit.Toggle, x, y, w, h, 11, o);
    }

    void FocusRing(Gfx g, Hit id, float x, float y, float w, float h, float r, double o)
    {
        if (Focused != id) return;
        g.Line(x - 3, y + h + 4, x + w + 3, y + h + 4, 2, Gfx.Fade(0xFFFFFFFF, 0.85 * o));
    }

    void DrawWindowButtons(Gfx g)
    {
        _hits.Add((Hit.Minimize, Width - 84, 8, 36, 32));
        double minHover = _hover.GetValueOrDefault(Hit.Minimize);
        if (minHover > 0.01) g.RoundRect(Width - 84, 8, 36, 32, 7, Gfx.Fade(0x24FFFFFF, minHover));
        g.Line(Width - 71, 24, Width - 61, 24, 1.4f, Soft);

        if (!CanClose) return;
        _hits.Add((Hit.Close, Width - 44, 8, 36, 32));
        double hover = _hover.GetValueOrDefault(Hit.Close);
        if (hover > 0.01) g.RoundRect(Width - 44, 8, 36, 32, 7, Gfx.Fade(Danger, hover));
        float cx = Width - 26, cy = 24;
        g.Line(cx - 5, cy - 5, cx + 5, cy + 5, 1.4f, Soft);
        g.Line(cx + 5, cy - 5, cx - 5, cy + 5, 1.4f, Soft);
    }

    static uint Lighten(uint color, double amount)
    {
        amount *= 0.12;
        uint r = (uint)Math.Min(255, ((color >> 16) & 255) + 255 * amount);
        uint gr = (uint)Math.Min(255, ((color >> 8) & 255) + 255 * amount);
        uint b = (uint)Math.Min(255, (color & 255) + 255 * amount);
        return (color & 0xFF000000) | (r << 16) | (gr << 8) | b;
    }
}
