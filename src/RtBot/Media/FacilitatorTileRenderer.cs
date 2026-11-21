using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using RtBot.Facilitator;

namespace RtBot.Media;

public enum AgentPresence
{
    Ready,
    Listening,
    Thinking,
    Speaking,
}

/// <summary>What the main panel shows: the overview, or one list in full on request.</summary>
public enum TileView
{
    Overview,
    Actions,
    Decisions,
    Tracked,
    Parked,
    Agenda,
    Capture,
    Insights,
}

/// <summary>Everything the tile shows, captured once per frame.</summary>
public sealed record TileState(
    string AgentName,
    AgentPresence Presence,
    string? Subject,
    TimeSpan Elapsed,
    TimeSpan? Remaining,
    TimeSpan? ScheduledLength,
    AgendaStatus Agenda,
    MeetingTimer? Timer,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<ActionItem> Actions,
    IReadOnlyList<string> Parked,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Tracked,
    int People,
    bool Restricted,
    string? Caption,
    DateTimeOffset? CaptionAt,
    string? Specialism = null,
    TileView View = TileView.Overview,
    VisualNote? Capture = null,
    IReadOnlyList<MeetingInsight>? Insights = null,
    int? Alignment = null,
    string? AlignmentNote = null,
    IReadOnlyList<int>? AlignmentTrend = null,
    IReadOnlyList<(string Name, double Share)>? Voices = null,
    string? Searching = null,
    IReadOnlyList<ActiveSkillBadge>? Skills = null);

/// <summary>
/// Draws Aria's video tile: a calm, modern slate HUD with a slim meeting bar, active skill pills,
/// a live intelligence feed (Work IQ, specialist skills, risks) and structured outcomes.
/// </summary>
/// <remarks>
/// Rendered with GDI+ into one reused BGRA bitmap and converted to NV12, the raw format the media
/// platform encodes. Laid out on a 1280x720 canvas and scaled. One renderer per call; not thread-safe.
/// </remarks>
public sealed class FacilitatorTileRenderer : IDisposable
{
    private static readonly Color CanvasTop = Color.FromArgb(10, 14, 23);
    private static readonly Color CanvasBottom = Color.FromArgb(15, 21, 34);
    private static readonly Color Surface = Color.FromArgb(19, 26, 41);
    private static readonly Color SurfaceSubtle = Color.FromArgb(25, 34, 53);
    private static readonly Color Hairline = Color.FromArgb(24, 255, 255, 255);
    private static readonly Color Ink = Color.FromArgb(248, 250, 252);
    private static readonly Color BodyInk = Color.FromArgb(203, 213, 225);
    private static readonly Color Muted = Color.FromArgb(148, 163, 184);
    private static readonly Color Dim = Color.FromArgb(100, 116, 139);

    private static readonly Color WorkIqTone = Color.FromArgb(56, 189, 248);
    private static readonly Color SkillTone = Color.FromArgb(167, 139, 250);
    private static readonly Color Ok = Color.FromArgb(52, 211, 153);
    private static readonly Color Warn = Color.FromArgb(251, 191, 36);
    private static readonly Color Over = Color.FromArgb(251, 113, 133);
    private static readonly Color DecisionTone = Color.FromArgb(96, 165, 250);
    private static readonly Color ActionTone = Color.FromArgb(52, 211, 153);
    private static readonly Color TrackedTone = Color.FromArgb(167, 139, 250);
    private static readonly Color InsightTone = Color.FromArgb(129, 140, 248);

    // Typographic formats add no padding, so text measured for a box fits it exactly.
    private static readonly StringFormat RightAligned = new(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far };
    private static readonly StringFormat Centred = new(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center };

    private readonly Bitmap _bitmap;
    private readonly Graphics _g;
    private readonly Font _h1;
    private readonly Font _h2;
    private readonly Font _cardTitle;
    private readonly Font _body;
    private readonly Font _small;
    private readonly Font _micro;
    private readonly Font _clock;
    private readonly Font _metric;
    private readonly Font _caption;
    private readonly byte[] _nv12;
    private double _t;

    public FacilitatorTileRenderer(int width, int height)
    {
        Width = width;
        Height = height;
        _bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        _g = Graphics.FromImage(_bitmap);
        _g.SmoothingMode = SmoothingMode.AntiAlias;
        _g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        _g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        _g.ScaleTransform(width / 1280f, height / 720f);

        _h1 = new Font("Segoe UI Semibold", 24, GraphicsUnit.Pixel);
        _h2 = new Font("Segoe UI Semibold", 20, GraphicsUnit.Pixel);
        _cardTitle = new Font("Segoe UI Semibold", 18, GraphicsUnit.Pixel);
        _body = new Font("Segoe UI", 16.5f, GraphicsUnit.Pixel);
        _small = new Font("Segoe UI", 14.5f, GraphicsUnit.Pixel);
        _micro = new Font("Segoe UI Semibold", 12.5f, GraphicsUnit.Pixel);
        _clock = new Font("Segoe UI Semibold", 25, GraphicsUnit.Pixel);
        _metric = new Font("Segoe UI Semibold", 16, GraphicsUnit.Pixel);
        _caption = new Font("Segoe UI", 16.5f, FontStyle.Italic, GraphicsUnit.Pixel);
        _nv12 = new byte[width * height * 3 / 2];
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>NV12 bytes for the frame, valid until the next call.</summary>
    public byte[] Render(TileState s, double seconds)
    {
        _t = seconds;
        using (LinearGradientBrush bg = new(new Rectangle(0, 0, 1280, 720), CanvasTop, CanvasBottom, 90f))
        {
            _g.FillRectangle(bg, 0, 0, 1280, 720);
        }

        DrawTopBar(s, seconds);
        DrawSkillsAndPulse(s);

        if (s.View == TileView.Overview)
        {
            DrawIntelligenceFeed(s);
            DrawOutcomesColumn(s);
        }
        else if (s.View == TileView.Capture && s.Capture is { } capture)
        {
            DrawCapture(capture);
        }
        else
        {
            DrawBreakdown(s);
        }

        return ToNv12();
    }

    // ---- Zone 1: Slim top bar (y = 18..84) ----------------------------------------------

    private void DrawTopBar(TileState s, double t)
    {
        const float x = 24, y = 18, w = 1232, h = 66;
        CardBox(Surface, Hairline, x, y, w, h, 16);

        // Compact animated presence indicator on the left.
        const float cx = 56, cy = 51, r = 15;
        Color presenceTone = Tone(s.Presence);
        double pulse = 0.5 + (0.5 * Math.Sin(t * (s.Presence == AgentPresence.Speaking ? 6.0 : 2.2)));
        float halo = r + 3 + (float)(pulse * 5);
        using (SolidBrush glow = new(Color.FromArgb((int)(42 * (1 - (pulse * 0.5))), presenceTone)))
        {
            _g.FillEllipse(glow, cx - halo, cy - halo, halo * 2, halo * 2);
        }

        using (SolidBrush core = new(Color.FromArgb(45, presenceTone)))
        {
            _g.FillEllipse(core, cx - r, cy - r, r * 2, r * 2);
        }

        using (Pen ring = new(Color.FromArgb(190, presenceTone), 1.6f))
        {
            _g.DrawEllipse(ring, cx - r, cy - r, r * 2, r * 2);
        }

        if (s.Presence == AgentPresence.Speaking)
        {
            for (int i = 0; i < 3; i++)
            {
                double amp = 0.35 + (0.65 * Math.Abs(Math.Sin((t * 8.0) + (i * 1.2))));
                float bh = (float)(6 + (amp * 13));
                FillRounded(Ink, cx - 7.5f + (i * 6f), cy - (bh / 2), 3f, bh, 1.5f);
            }
        }
        else if (s.Presence == AgentPresence.Thinking)
        {
            using Pen spin = new(Ink, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            _g.DrawArc(spin, cx - 8, cy - 8, 16, 16, (float)(t * 280 % 360), 110);
        }
        else
        {
            using SolidBrush dot = new(presenceTone);
            _g.FillEllipse(dot, cx - 4.5f, cy - 4.5f, 9f, 9f);
        }

        // Agent name + status pill + meeting title.
        Text(s.AgentName, _h2, Ink, new RectangleF(84, y + 19, 120, 28), null);
        float nameW = Measure(s.AgentName, _h2);
        string stateLabel = s.Searching is not null ? "Searching M365" : s.Presence switch
        {
            AgentPresence.Speaking => "Speaking",
            AgentPresence.Thinking => "Thinking",
            AgentPresence.Listening => "Listening",
            _ => "Live",
        };
        Color stateTone = s.Searching is not null ? WorkIqTone : presenceTone;
        float pillX = 84 + nameW + 10;
        float stateW = SoftPill(stateLabel, stateTone, pillX, y + 21);

        float sepX = pillX + stateW + 14;
        using (Pen sep = new(Hairline, 1f))
        {
            _g.DrawLine(sep, sepX, y + 20, sepX, y + h - 20);
        }

        // Right cluster: clock + agenda item pill + optional timer.
        const float clockRight = x + w - 22;
        string clockStr = Clock(s.Elapsed);
        Text(clockStr, _clock, Ink, new RectangleF(clockRight - 130, y + 10, 130, 30), RightAligned);

        string remLabel = "elapsed";
        Color remTone = Muted;
        if (s.Remaining is { } left)
        {
            bool over = left < TimeSpan.Zero;
            remLabel = over ? $"+{Minutes(-left)} over" : $"{Minutes(left)} left";
            remTone = over ? Over : left.TotalMinutes <= 5 ? Warn : Muted;
        }

        Text(remLabel, _micro, remTone, new RectangleF(clockRight - 140, y + 41, 140, 18), RightAligned);

        float rightCursor = clockRight - 132;
        if (s.Agenda.Current is { } current)
        {
            string timebox = string.Empty;
            Color agendaTone = Ok;
            if (current.Minutes is { } m && m > 0 && s.Agenda.CurrentRemaining is { } ar)
            {
                bool over = ar < TimeSpan.Zero;
                double used = s.Agenda.CurrentElapsed.TotalMinutes / m;
                agendaTone = over ? Over : used >= 0.75 ? Warn : Ok;
                timebox = over ? $" \u00B7 +{Clock(-ar)}" : $" \u00B7 {Clock(ar)}";
            }

            float maxAgendaTitle = s.Timer is not null ? 130 : 185;
            string agendaText = $"{s.Agenda.CurrentIndex + 1}/{s.Agenda.Items.Count}  {Ellipsize(current.Title, _small, maxAgendaTitle)}{timebox}";
            float aw = Measure(agendaText, _small) + 34;
            float ax = rightCursor - aw;
            CardBox(SurfaceSubtle, Hairline, ax, y + 17, aw, 32, 16);
            using (SolidBrush d = new(agendaTone))
            {
                _g.FillEllipse(d, ax + 12, y + 29, 8, 8);
            }

            Text(agendaText, _small, BodyInk, new RectangleF(ax + 25, y + 23, aw - 30, 22), null);
            rightCursor = ax - 10;
        }

        if (s.Timer is { } timer)
        {
            TimeSpan tr = timer.EndsAt - DateTimeOffset.UtcNow;
            string timerText = $"{Ellipsize(timer.Label, _small, 110)} {Clock(tr < TimeSpan.Zero ? TimeSpan.Zero : tr)}";
            float tw = SoftPill(timerText, Warn, 0, -100, measureOnly: true);
            SoftPill(timerText, Warn, rightCursor - tw, y + 21);
            rightCursor -= tw + 10;
        }

        float subjX = sepX + 14;
        float subjW = Math.Max(140, rightCursor - subjX - 12);
        Text(Ellipsize(s.Subject ?? "Teams meeting", _h2, subjW), _h2, Ink, new RectangleF(subjX, y + 19, subjW, 28), null);

        // Subtle 2px progress line along the bottom of the top bar.
        if (s.Agenda.Current?.Minutes is { } mins && mins > 0)
        {
            double frac = Math.Clamp(s.Agenda.CurrentElapsed.TotalMinutes / mins, 0, 1);
            Color barTone = s.Agenda.CurrentRemaining is { } rem && rem < TimeSpan.Zero ? Over : frac >= 0.75 ? Warn : Ok;
            FillRounded(barTone, x + 16, y + h - 3, (float)((w - 32) * frac), 2.5f, 1.2f);
        }
        else if (s.ScheduledLength is { TotalSeconds: > 0 } len)
        {
            double frac = Math.Clamp(s.Elapsed.TotalSeconds / len.TotalSeconds, 0, 1);
            Color barTone = s.Remaining is { } rem && rem < TimeSpan.Zero ? Over : frac >= 0.85 ? Warn : DecisionTone;
            FillRounded(barTone, x + 16, y + h - 3, (float)((w - 32) * frac), 2.5f, 1.2f);
        }
    }

    // ---- Zone 2: Active skills strip + room pulse pills (y = 96..134) -------------------

    private void DrawSkillsAndPulse(TileState s)
    {
        const float y = 96;
        float rightEdge = 1256;

        // Right-aligned room pulse pills first so we know where the skill strip stops.
        if (s.Restricted)
        {
            float rw = SoftPill("External guest \u00B7 M365 off", Warn, 0, -100, measureOnly: true);
            SoftPill("External guest \u00B7 M365 off", Warn, rightEdge - rw, y + 4);
            rightEdge -= rw + 10;
        }

        // Voices pill.
        IReadOnlyList<(string Name, double Share)> voices = s.Voices ?? [];
        string voiceText = voices.Count == 0
            ? $"{s.People} in call"
            : string.Join(" \u00B7 ", voices.Take(2).Select(v => $"{ShortName(v.Name)} {v.Share * 100:0}%"));
        float vw = Measure(voiceText, _micro) + 24;
        CardBox(Surface, Hairline, rightEdge - vw, y + 2, vw, 32, 16);
        Text(voiceText, _micro, Muted, new RectangleF(rightEdge - vw + 12, y + 9, vw - 20, 18), null);
        rightEdge -= vw + 8;

        // Alignment pill with inline micro-bar.
        int? align = s.Alignment;
        Color alignTone = align is not { } av ? Muted : av >= 75 ? Ok : av >= 50 ? Warn : Over;
        string alignLabel = align is { } ap ? $"Alignment {ap}%" : "Alignment \u2014";
        float alw = Measure(alignLabel, _micro) + (align.HasValue ? 64 : 26);
        float alx = rightEdge - alw;
        CardBox(Surface, Hairline, alx, y + 2, alw, 32, 16);
        using (SolidBrush dot = new(alignTone))
        {
            _g.FillEllipse(dot, alx + 11, y + 14.5f, 7, 7);
        }

        Text(alignLabel, _micro, Ink, new RectangleF(alx + 22, y + 9, alw - 26, 18), null);
        if (align is { } pct)
        {
            Bar(alx + alw - 44, y + 15, 32, 6, pct / 100.0, alignTone);
        }

        rightEdge = alx - 12;

        // Left: Active specialist skill badges (newest dynamic activations stand out).
        Text("SKILLS", _micro, Dim, new RectangleF(28, y + 9, 58, 18), null);
        float sx = 82;

        List<(string Label, bool Dynamic, bool Fresh)> badges = [];
        if (s.Skills is { Count: > 0 } richSkills)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (ActiveSkillBadge b in richSkills)
            {
                bool fresh = b.Dynamic && (now - b.ActivatedAt).TotalSeconds <= 90;
                badges.Add((b.Label, b.Dynamic, fresh));
            }
        }
        else if (!string.IsNullOrWhiteSpace(s.Specialism))
        {
            foreach (string part in s.Specialism.Split('\u00B7', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                badges.Add((part, false, false));
            }
        }

        if (badges.Count == 0)
        {
            Text("Watching conversation for specialist domains\u2026", _small, Dim, new RectangleF(sx, y + 8, rightEdge - sx, 20), null);
            return;
        }

        foreach ((string label, bool dynamic, bool fresh) in badges)
        {
            string display = dynamic ? $"\u2726 {label}" : label;
            float pw = Measure(display, _micro) + (dynamic ? 22 : 28);
            if (sx + pw > rightEdge)
            {
                int hidden = badges.Count - badges.FindIndex(b => b.Label == label);
                if (hidden > 0 && sx + 44 <= rightEdge)
                {
                    CardBox(Surface, Hairline, sx, y + 2, 40, 32, 16);
                    Text($"+{hidden}", _micro, Muted, new RectangleF(sx, y + 9, 40, 18), Centred);
                }

                break;
            }

            Color fill = fresh ? Color.FromArgb(48, SkillTone) : Surface;
            Color border = fresh ? Color.FromArgb(165, SkillTone) : Color.FromArgb(60, SkillTone);
            CardBox(fill, border, sx, y + 2, pw, 32, 16);
            if (!dynamic)
            {
                using SolidBrush bDot = new(SkillTone);
                _g.FillEllipse(bDot, sx + 11, y + 14.5f, 6.5f, 6.5f);
            }

            float tx = dynamic ? sx + 11 : sx + 22;
            Text(display, _micro, fresh ? Ink : BodyInk, new RectangleF(tx, y + 9, pw - (tx - sx) - 8, 18), null);
            sx += pw + 7;
        }
    }

    // ---- Zone 3A: Live Intelligence feed (left primary panel, x = 24..800) --------------

    private void DrawIntelligenceFeed(TileState s)
    {
        const float x = 24, y = 144, w = 776, h = 554;
        CardBox(Surface, Hairline, x, y, w, h, 18);

        IReadOnlyList<MeetingInsight> insights = s.Insights ?? [];
        Text("LIVE INTELLIGENCE", _micro, Dim, new RectangleF(x + 22, y + 16, 220, 18), null);
        Text("Skills, risks & Work IQ evidence", _small, Muted, new RectangleF(x + 168, y + 14, 340, 20), null);
        if (insights.Count > 0)
        {
            Text($"{insights.Count}", _metric, Muted, new RectangleF(x + w - 80, y + 14, 58, 22), RightAligned);
        }

        using (Pen sep = new(Hairline, 1f))
        {
            _g.DrawLine(sep, x + 20, y + 44, x + w - 20, y + 44);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        double captionAge = s.CaptionAt is { } capAt ? (now - capAt).TotalSeconds : double.MaxValue;
        bool hasCaption = !string.IsNullOrWhiteSpace(s.Caption) && captionAge is >= 0 and < 20;
        float bottom = hasCaption ? y + h - 56 : y + h - 14;
        float iy = y + 56;
        bool any = false;

        if (s.Searching is { } query)
        {
            string dots = new('.', 1 + ((int)(_t * 2) % 3));
            const float sh = 46;
            CardBox(Color.FromArgb(26, WorkIqTone), Color.FromArgb(95, WorkIqTone), x + 18, iy, w - 36, sh, 12);
            float cw = TagPill("WORK IQ", WorkIqTone, x + 30, iy + 12);
            string qText = $"Searching M365{dots}  {query}";
            Text(Ellipsize(qText, _small, w - 66 - cw), _small, Ink, new RectangleF(x + 38 + cw, iy + 13, w - 66 - cw, 22), null);
            iy += sh + 10;
            any = true;
        }

        foreach (MeetingInsight insight in insights.Reverse())
        {
            bool showSource = !string.IsNullOrWhiteSpace(insight.Citation) && !insight.Kind.Equals("skill", StringComparison.OrdinalIgnoreCase);
            List<string> lines = Wrap(insight.Text, _body, w - 72, 2);
            float ch = 36 + (lines.Count * 22) + (showSource ? 21 : 0) + 12;
            if (iy + ch > bottom)
            {
                break;
            }

            Color tone = KindTone(insight.Kind);
            CardBox(SurfaceSubtle, Hairline, x + 18, iy, w - 36, ch, 12);
            FillRounded(tone, x + 18, iy + 10, 3.5f, ch - 20, 2f);

            string badge = insight.Kind.Equals("skill", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(insight.Citation)
                ? $"SKILL \u00B7 {insight.Citation.ToUpperInvariant()}"
                : ChipLabel(insight.Kind);
            float bw = TagPill(badge, tone, x + 32, iy + 10);

            float highReserve = insight.Severity == "high" ? 64 : 0;
            float titleW = w - 68 - bw - highReserve;
            Text(Ellipsize(insight.Title, _cardTitle, titleW), _cardTitle, Ink, new RectangleF(x + 40 + bw, iy + 9, titleW, 24), null);

            if (insight.Severity == "high")
            {
                TagPill("HIGH", Over, x + w - 78, iy + 10);
            }

            float ly = iy + 37;
            foreach (string line in lines)
            {
                Text(line, _body, BodyInk, new RectangleF(x + 32, ly, w - 68, 22), null);
                ly += 22;
            }

            if (showSource)
            {
                Text(Ellipsize(insight.Citation!, _small, w - 68), _small, Muted, new RectangleF(x + 32, ly + 2, w - 68, 20), null);
            }

            iy += ch + 10;
            any = true;
        }

        if (!any)
        {
            float ey = y + 180;
            Text("Listening to the conversation", _h2, Ink, new RectangleF(x + 40, ey, w - 80, 28), Centred);
            Text("Specialist skills activate automatically as topics shift, and relevant Microsoft 365 playbooks or policies appear here via Work IQ.", _body, Muted, new RectangleF(x + 80, ey + 34, w - 160, 56), Centred);
        }

        if (hasCaption)
        {
            int alpha = Math.Clamp(captionAge < 16 ? 255 : (int)(255 * (20 - captionAge) / 4), 0, 255);
            float cy = y + h - 46;
            CardBox(Color.FromArgb(alpha * 220 / 255, 11, 16, 27), Color.FromArgb(alpha * 50 / 255, SkillTone), x + 18, cy, w - 36, 34, 10);
            string spoken = $"{s.AgentName}: \u201C{s.Caption!.Trim()}\u201D";
            Text(Ellipsize(spoken, _caption, w - 64), _caption, Color.FromArgb(alpha, Ink), new RectangleF(x + 32, cy + 6, w - 64, 22), null);
        }
    }

    // ---- Zone 3B: Unified Outcomes column (right panel, x = 816..1256) ------------------

    private void DrawOutcomesColumn(TileState s)
    {
        const float x = 816, y = 144, w = 440, h = 554;
        CardBox(Surface, Hairline, x, y, w, h, 18);

        Text("OUTCOMES", _micro, Dim, new RectangleF(x + 22, y + 16, 160, 18), null);
        int totalOutcomes = s.Decisions.Count + s.Actions.Count;
        Text($"{totalOutcomes} captured", _small, Muted, new RectangleF(x + w - 160, y + 14, 138, 20), RightAligned);

        using (Pen sep = new(Hairline, 1f))
        {
            _g.DrawLine(sep, x + 20, y + 44, x + w - 20, y + 44);
        }

        // Section 1: Decisions (y = 56..196)
        DrawOutcomeSection(
            x + 20, y + 54, w - 40, 138,
            "DECISIONS", s.Decisions.Count, DecisionTone,
            [.. s.Decisions.TakeLast(2).Reverse().Select(d => (d, (string?)null))]);

        using (Pen sep = new(Hairline, 1f))
        {
            _g.DrawLine(sep, x + 20, y + 198, x + w - 20, y + 198);
        }

        // Section 2: Actions (y = 208..384)
        DrawOutcomeSection(
            x + 20, y + 208, w - 40, 174,
            "ACTIONS", s.Actions.Count, ActionTone,
            [.. s.Actions.TakeLast(2).Reverse().Select(a => (a.Task, (string?)$"{a.Owner}{(string.IsNullOrWhiteSpace(a.Due) ? string.Empty : " \u00B7 " + a.Due)}"))]);

        using (Pen sep = new(Hairline, 1f))
        {
            _g.DrawLine(sep, x + 20, y + 388, x + w - 20, y + 388);
        }

        // Section 3: Specialist Tracked (or Parking Lot)
        int trackedCount = s.Tracked.Sum(kv => kv.Value.Count);
        if (trackedCount > 0)
        {
            List<(string, string?)> tracked = [.. s.Tracked
                .SelectMany(kv => kv.Value.Select(v => (v, (string?)kv.Key)))
                .TakeLast(2)
                .Reverse()];
            DrawOutcomeSection(x + 20, y + 398, w - 40, 146, "SPECIALIST TRACKING", trackedCount, TrackedTone, tracked);
        }
        else
        {
            DrawOutcomeSection(
                x + 20, y + 398, w - 40, 146,
                "PARKING LOT", s.Parked.Count, Warn,
                [.. s.Parked.TakeLast(2).Reverse().Select(p => (p, (string?)null))]);
        }
    }

    private void DrawOutcomeSection(
        float x, float y, float w, float h,
        string label, int count, Color tone,
        IReadOnlyList<(string Text, string? Meta)> items)
    {
        using (SolidBrush dot = new(tone))
        {
            _g.FillEllipse(dot, x + 2, y + 5, 7, 7);
        }

        Text(label, _micro, Muted, new RectangleF(x + 15, y + 1, w - 60, 18), null);
        Text(count.ToString(), _metric, count > 0 ? Ink : Dim, new RectangleF(x + w - 48, y - 1, 48, 20), RightAligned);

        float iy = y + 26;
        if (items.Count == 0)
        {
            Text("None recorded yet", _small, Dim, new RectangleF(x + 15, iy + 2, w - 20, 20), null);
            return;
        }

        foreach ((string text, string? meta) in items)
        {
            if (iy + 20 > y + h)
            {
                return;
            }

            using (SolidBrush itemDot = new(Color.FromArgb(150, tone)))
            {
                _g.FillEllipse(itemDot, x + 3, iy + 8, 5, 5);
            }

            int maxLines = meta is null ? 2 : 1;
            foreach (string line in Wrap(text, _body, w - 18, maxLines))
            {
                if (iy + 20 > y + h)
                {
                    return;
                }

                Text(line, _body, Ink, new RectangleF(x + 15, iy, w - 18, 22), null);
                iy += 21;
            }

            if (meta is not null && iy + 18 <= y + h)
            {
                Text(Ellipsize(meta, _small, w - 18), _small, tone, new RectangleF(x + 15, iy, w - 18, 19), null);
                iy += 20;
            }

            iy += 9;
        }
    }

    // ---- Capture & Breakdown views (full width below top strips) ------------------------

    private Bitmap? _captureImage;
    private VisualNote? _captureFor;

    private void DrawCapture(VisualNote capture)
    {
        const float x = 24, y = 144, w = 1232, h = 554;
        CardBox(Surface, Hairline, x, y, w, h, 18);

        TagPill("CAPTURED VISUAL", TrackedTone, x + 24, y + 18);
        Text(Ellipsize(capture.Title, _h1, w - 240), _h1, Ink, new RectangleF(x + 180, y + 14, w - 420, 32), null);
        string meta = $"{capture.Source}{(capture.CapturedFrom is null ? string.Empty : " \u00B7 " + capture.CapturedFrom)} \u00B7 {capture.At.ToLocalTime():HH:mm}";
        Text(Ellipsize(meta, _small, 260), _small, Muted, new RectangleF(x + w - 284, y + 20, 260, 22), RightAligned);

        // Decode once per capture, not once per frame.
        if (!ReferenceEquals(_captureFor, capture))
        {
            _captureImage?.Dispose();
            _captureImage = null;
            _captureFor = capture;
            try
            {
                using MemoryStream ms = new(capture.Jpeg);
                using Image img = Image.FromStream(ms);
                _captureImage = new Bitmap(img);
            }
            catch (ArgumentException)
            {
                // Unreadable image: the text still shows.
            }
        }

        const float ix = x + 24, iy = y + 64, iw = 620, ih = 466;
        CardBox(CanvasTop, Hairline, ix, iy, iw, ih, 12);
        if (_captureImage is { } image)
        {
            float scale = Math.Min((iw - 16) / image.Width, (ih - 16) / image.Height);
            float dw = image.Width * scale, dh = image.Height * scale;
            _g.DrawImage(image, ix + ((iw - dw) / 2), iy + ((ih - dh) / 2), dw, dh);
        }

        float tx = ix + iw + 28, tw = x + w - 28 - tx, ty = iy + 4;
        foreach (string line in Wrap(capture.Summary, _body, tw, 6))
        {
            Text(line, _body, Ink, new RectangleF(tx, ty, tw, 24), null);
            ty += 23;
        }

        ty += 14;
        foreach (string point in capture.KeyPoints.Take(6))
        {
            using (SolidBrush bullet = new(TrackedTone))
            {
                _g.FillEllipse(bullet, tx, ty + 7, 6, 6);
            }

            foreach (string line in Wrap(point, _small, tw - 18, 2))
            {
                Text(line, _small, BodyInk, new RectangleF(tx + 15, ty, tw - 18, 21), null);
                ty += 20;
            }

            ty += 8;
            if (ty > y + h - 32)
            {
                break;
            }
        }
    }

    private void DrawBreakdown(TileState s)
    {
        const float x = 24, y = 144, w = 1232, h = 554;
        (string title, Color tone, List<(string Text, string? Meta)> items) = s.View switch
        {
            TileView.Actions => ("Actions", ActionTone, s.Actions.Select(a =>
                (a.Task, (string?)$"{a.Owner}{(string.IsNullOrWhiteSpace(a.Due) ? string.Empty : " \u00B7 due " + a.Due)}")).ToList()),
            TileView.Decisions => ("Decisions", DecisionTone, s.Decisions.Select(d => (d, (string?)null)).ToList()),
            TileView.Parked => ("Parking lot", Warn, s.Parked.Select(p => (p, (string?)null)).ToList()),
            TileView.Tracked => ("Specialist tracking", TrackedTone, s.Tracked.SelectMany(kv => kv.Value.Select(v => (v, (string?)kv.Key))).ToList()),
            TileView.Insights => ("Live intelligence", InsightTone, (s.Insights ?? []).Select(i =>
                ($"{i.Title} \u2014 {i.Text}", (string?)(i.Citation ?? KindLabel(i.Kind)))).ToList()),
            _ => ("Agenda", Ok, s.Agenda.Items.Select((a, i) => (
                $"{(s.Agenda.Completed.Contains(i) ? "\u2713 " : i == s.Agenda.CurrentIndex ? "\u25B6 " : string.Empty)}{a.Title}",
                (string?)(a.Minutes is { } m ? $"{m:0.#} min" : null))).ToList()),
        };

        CardBox(Surface, Hairline, x, y, w, h, 18);
        FillRounded(tone, x + 22, y + 18, 4, 28, 2);
        Text(title, _h1, Ink, new RectangleF(x + 36, y + 15, 420, 34), null);
        Text($"{items.Count} total", _small, Muted, new RectangleF(x + w - 200, y + 22, 176, 22), RightAligned);

        using (Pen sep = new(Hairline, 1f))
        {
            _g.DrawLine(sep, x + 22, y + 58, x + w - 22, y + 58);
        }

        bool longForm = s.View == TileView.Insights;
        int maxRows = longForm ? 5 : 9;
        IEnumerable<(string Text, string? Meta)> shown = items.Count > maxRows ? items.Skip(items.Count - maxRows) : items;
        int number = Math.Max(0, items.Count - maxRows);
        float iy = y + 74;

        if (items.Count == 0)
        {
            Text("Nothing recorded yet.", _body, Muted, new RectangleF(x + 36, iy, w - 72, 26), null);
            return;
        }

        foreach ((string text, string? meta) in shown)
        {
            if (iy > y + h - 48)
            {
                break;
            }

            number++;
            Text($"{number}.", _body, tone, new RectangleF(x + 22, iy, 32, 24), RightAligned);

            if (longForm)
            {
                foreach (string line in Wrap(text, _body, w - 96, 2))
                {
                    Text(line, _body, Ink, new RectangleF(x + 64, iy, w - 96, 24), null);
                    iy += 23;
                }

                if (meta is not null)
                {
                    Text(Ellipsize(meta, _small, w - 96), _small, Muted, new RectangleF(x + 64, iy + 1, w - 96, 20), null);
                    iy += 22;
                }

                iy += 14;
                continue;
            }

            float metaW = meta is null ? 0 : Math.Min(320, Measure(meta, _small) + 12);
            Text(Ellipsize(text, _body, w - 96 - metaW), _body, Ink, new RectangleF(x + 64, iy, w - 96 - metaW, 24), null);
            if (meta is not null)
            {
                Text(Ellipsize(meta, _small, 320), _small, tone, new RectangleF(x + w - 28 - metaW, iy + 2, metaW, 22), RightAligned);
            }

            iy += 46;
        }
    }

    // ---- Helpers & primitives -----------------------------------------------------------

    private float Measure(string text, Font font) =>
        _g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;

    private static string ShortName(string full)
    {
        int space = full.IndexOf(' ');
        return space > 0 ? full[..space] : full;
    }

    private float TagPill(string label, Color tone, float x, float y)
    {
        float w = Measure(label, _micro) + 18;
        FillRounded(Color.FromArgb(38, tone), x, y, w, 22, 6);
        Text(label, _micro, tone, new RectangleF(x + 9, y + 3, w, 18), null);
        return w;
    }

    private float SoftPill(string label, Color tone, float x, float y, bool measureOnly = false)
    {
        float w = Measure(label, _micro) + 22;
        if (!measureOnly)
        {
            CardBox(Color.FromArgb(32, tone), Color.FromArgb(90, tone), x, y, w, 24, 12);
            Text(label, _micro, tone, new RectangleF(x + 11, y + 4, w, 18), null);
        }

        return w;
    }

    private void CardBox(Color fill, Color border, float x, float y, float w, float h, float radius)
    {
        using GraphicsPath path = RoundedPath(x, y, w, h, radius);
        using (SolidBrush brush = new(fill))
        {
            _g.FillPath(brush, path);
        }

        using Pen pen = new(border, 1f);
        _g.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedPath(float x, float y, float w, float h, float radius)
    {
        GraphicsPath path = new();
        float d = Math.Max(0.5f, Math.Min(radius * 2, Math.Min(w, h)));
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color KindTone(string kind) => kind switch
    {
        "knowledge" => WorkIqTone,
        "skill" => SkillTone,
        "risk" => Over,
        "alignment" => Warn,
        "pace" => InsightTone,
        _ => InsightTone,
    };

    private static string ChipLabel(string kind) => kind switch
    {
        "knowledge" => "WORK IQ",
        "skill" => "SKILL",
        "risk" => "RISK",
        "alignment" => "ALIGNMENT",
        "pace" => "PACE",
        _ => "FACILITATION",
    };

    private static string KindLabel(string kind) => kind switch
    {
        "knowledge" => "Work IQ",
        "risk" => "Risk",
        "alignment" => "Alignment",
        "pace" => "Pace",
        "skill" => "Skill",
        _ => "Facilitation",
    };

    private static Color Tone(AgentPresence presence) => presence switch
    {
        AgentPresence.Listening => Ok,
        AgentPresence.Thinking => Warn,
        AgentPresence.Speaking => SkillTone,
        _ => DecisionTone,
    };

    private void Bar(float x, float y, float w, float h, double fraction, Color tone)
    {
        FillRounded(Color.FromArgb(35, 255, 255, 255), x, y, w, h, h / 2);
        float fw = (float)Math.Max(h, w * Math.Clamp(fraction, 0, 1));
        FillRounded(tone, x, y, fw, h, h / 2);
    }

    private void FillRounded(Color color, float x, float y, float w, float h, float radius)
    {
        using GraphicsPath path = RoundedPath(x, y, w, h, radius);
        using SolidBrush brush = new(color);
        _g.FillPath(brush, path);
    }

    private void Text(string text, Font font, Color color, RectangleF area, StringFormat? format)
    {
        using SolidBrush brush = new(color);
        _g.DrawString(text, font, brush, area, format ?? StringFormat.GenericTypographic);
    }

    /// <summary>Word-wraps to at most <paramref name="maxLines"/>, ellipsizing the last line.</summary>
    private List<string> Wrap(string text, Font font, float width, int maxLines)
    {
        List<string> lines = [];
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string current = string.Empty;
        int i = 0;

        for (; i < words.Length && lines.Count < maxLines - 1; i++)
        {
            string candidate = current.Length == 0 ? words[i] : current + " " + words[i];
            if (current.Length > 0 && _g.MeasureString(candidate, font, PointF.Empty, StringFormat.GenericTypographic).Width > width)
            {
                lines.Add(current);
                current = words[i];
            }
            else
            {
                current = candidate;
            }
        }

        string rest = string.Join(' ', new[] { current }.Concat(words.Skip(i)).Where(s => s.Length > 0));
        if (rest.Length > 0)
        {
            lines.Add(Ellipsize(rest, font, width));
        }

        return lines;
    }

    private string Ellipsize(string text, Font font, float width)
    {
        // Measured with the same format it is drawn with, or small sizes clip the last glyphs.
        float Measure(string s) => _g.MeasureString(s, font, PointF.Empty, StringFormat.GenericTypographic).Width;

        if (width <= 0 || Measure(text) <= width)
        {
            return text;
        }

        int len = text.Length;
        while (len > 1 && Measure(text[..len] + "\u2026") > width)
        {
            len--;
        }

        return text[..len].TrimEnd() + "\u2026";
    }

    private static string Clock(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";

    private static string Minutes(TimeSpan t) => $"{Math.Max(1, (int)Math.Ceiling(t.TotalMinutes))} min";

    /// <summary>BGRA to NV12 (BT.601 limited range), the platform's raw send format.</summary>
    private unsafe byte[] ToNv12()
    {
        int w = Width, h = Height;
        BitmapData data = _bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte* src = (byte*)data.Scan0;
            int stride = data.Stride;

            fixed (byte* dst = _nv12)
            {
                byte* uv = dst + (w * h);
                for (int row = 0; row < h; row++)
                {
                    byte* line = src + (row * stride);
                    byte* yOut = dst + (row * w);
                    bool chromaRow = (row & 1) == 0;
                    byte* uvOut = uv + ((row >> 1) * w);

                    for (int col = 0; col < w; col++)
                    {
                        int b = line[col * 4], g = line[(col * 4) + 1], r = line[(col * 4) + 2];
                        yOut[col] = (byte)(((66 * r) + (129 * g) + (25 * b) + 128 >> 8) + 16);

                        if (chromaRow && (col & 1) == 0)
                        {
                            uvOut[col] = (byte)(((-38 * r) - (74 * g) + (112 * b) + 128 >> 8) + 128);
                            uvOut[col + 1] = (byte)(((112 * r) - (94 * g) - (18 * b) + 128 >> 8) + 128);
                        }
                    }
                }
            }
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }

        return _nv12;
    }

    /// <summary>The current frame as PNG, for diagnostics and documentation.</summary>
    public byte[] SnapshotPng()
    {
        using MemoryStream ms = new();
        _bitmap.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    public void Dispose()
    {
        _captureImage?.Dispose();
        _g.Dispose();
        _bitmap.Dispose();
        _h1.Dispose();
        _h2.Dispose();
        _cardTitle.Dispose();
        _body.Dispose();
        _small.Dispose();
        _micro.Dispose();
        _clock.Dispose();
        _metric.Dispose();
        _caption.Dispose();
    }
}
