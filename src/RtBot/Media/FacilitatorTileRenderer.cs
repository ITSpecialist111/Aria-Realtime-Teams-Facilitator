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
    IReadOnlyList<MeetingInsight>? Insights = null);

/// <summary>
/// Draws Aria's video tile: presence orb and meeting clock across the top, a caption strip, and
/// summary cards of what she has picked up, or one list in full when someone asks for it.
/// </summary>
/// <remarks>
/// Rendered with GDI+ into one reused BGRA bitmap and converted to NV12, the raw format the media
/// platform encodes. Laid out on a 1280x720 canvas and scaled. One renderer per call; not thread-safe.
/// </remarks>
public sealed class FacilitatorTileRenderer : IDisposable
{
    private static readonly Color Background1 = Color.FromArgb(14, 14, 34);
    private static readonly Color Background2 = Color.FromArgb(32, 26, 82);
    private static readonly Color Muted = Color.FromArgb(160, 164, 196);
    private static readonly Color Ok = Color.FromArgb(52, 211, 153);
    private static readonly Color Warn = Color.FromArgb(251, 191, 36);
    private static readonly Color Over = Color.FromArgb(248, 113, 113);
    private static readonly Color DecisionTone = Color.FromArgb(96, 165, 250);
    private static readonly Color ActionTone = Color.FromArgb(52, 211, 153);
    private static readonly Color TrackedTone = Color.FromArgb(244, 114, 182);

    // Typographic formats add no padding, so text measured for a box fits it exactly.
    private static readonly StringFormat RightAligned = new(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far };
    private static readonly StringFormat Centred = new(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center };

    private readonly Bitmap _bitmap;
    private readonly Graphics _g;
    private readonly Font _title;
    private readonly Font _name;
    private readonly Font _body;
    private readonly Font _item;
    private readonly Font _small;
    private readonly Font _big;
    private readonly Font _count;
    private readonly Font _label;
    private readonly Font _caption;
    private readonly byte[] _nv12;

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

        _title = new Font("Segoe UI Semibold", 26, GraphicsUnit.Pixel);
        _name = new Font("Segoe UI Semibold", 34, GraphicsUnit.Pixel);
        _body = new Font("Segoe UI", 21, GraphicsUnit.Pixel);
        _item = new Font("Segoe UI", 19, GraphicsUnit.Pixel);
        _small = new Font("Segoe UI", 16, GraphicsUnit.Pixel);
        _big = new Font("Segoe UI Light", 50, GraphicsUnit.Pixel);
        _count = new Font("Segoe UI Semibold", 36, GraphicsUnit.Pixel);
        _label = new Font("Segoe UI Semibold", 15, GraphicsUnit.Pixel);
        _caption = new Font("Segoe UI", 21, FontStyle.Italic, GraphicsUnit.Pixel);
        _nv12 = new byte[width * height * 3 / 2];
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>NV12 bytes for the frame, valid until the next call.</summary>
    public byte[] Render(TileState s, double seconds)
    {
        using (LinearGradientBrush bg = new(new Rectangle(0, 0, 1280, 720), Background1, Background2, 70f))
        {
            _g.FillRectangle(bg, 0, 0, 1280, 720);
        }

        DrawPresence(s, seconds);

        if (s.View == TileView.Overview)
        {
            DrawHeader(s);
            DrawCaption(s, 340);
            DrawCards(s);
        }
        else if (s.View == TileView.Capture && s.Capture is { } capture)
        {
            DrawCapture(s, capture);
        }
        else
        {
            DrawBreakdown(s);
        }

        return ToNv12();
    }

    private Bitmap? _captureImage;
    private VisualNote? _captureFor;

    private void DrawCapture(TileState s, VisualNote capture)
    {
        const float x = 310, y = 22, w = 948, h = 676;
        FillRounded(Color.FromArgb(24, 255, 255, 255), x, y, w, h, 20);
        FillRounded(TrackedTone, x, y + 24, 6, 44, 3);
        Text("Captured", _label, TrackedTone, new RectangleF(x + 28, y + 20, 300, 20), null);
        Text(Ellipsize(capture.Title, _name, w - 56), _name, Color.White, new RectangleF(x + 28, y + 38, w - 56, 46), null);
        Text(Ellipsize($"{capture.Source}{(capture.CapturedFrom is null ? string.Empty : " \u00B7 " + capture.CapturedFrom)} \u00B7 {capture.At.ToLocalTime():HH:mm}", _small, w - 56), _small, Muted, new RectangleF(x + 28, y + 86, w - 56, 22), null);

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

        const float ix = x + 28, iy = y + 124, iw = 520, ih = 520;
        if (_captureImage is { } image)
        {
            float scale = Math.Min(iw / image.Width, ih / image.Height);
            float dw = image.Width * scale, dh = image.Height * scale;
            FillRounded(Color.FromArgb(40, 0, 0, 0), ix, iy, iw, ih, 12);
            _g.DrawImage(image, ix + ((iw - dw) / 2), iy + ((ih - dh) / 2), dw, dh);
        }

        float tx = ix + iw + 26, tw = x + w - 28 - tx, ty = iy;
        foreach (string line in Wrap(capture.Summary, _item, tw, 8))
        {
            Text(line, _item, Color.White, new RectangleF(tx, ty, tw, 26), null);
            ty += 25;
        }

        ty += 14;
        foreach (string point in capture.KeyPoints.Take(6))
        {
            using (SolidBrush bullet = new(TrackedTone))
            {
                _g.FillEllipse(bullet, tx, ty + 9, 7, 7);
            }

            foreach (string line in Wrap(point, _small, tw - 18, 2))
            {
                Text(line, _small, Muted, new RectangleF(tx + 16, ty, tw - 18, 22), null);
                ty += 21;
            }

            ty += 8;
            if (ty > y + h - 40)
            {
                break;
            }
        }
    }

    // ---- Presence (left column) ---------------------------------------------------------

    private void DrawPresence(TileState s, double t)
    {
        const float cx = 150, cy = 120, r = 72;
        const float k = r / 112f;
        Color tone = Tone(s.Presence);

        double speed = s.Presence == AgentPresence.Speaking ? 3.2 : 1.1;
        for (int i = 0; i < 3; i++)
        {
            double phase = ((t * speed / 2.5) + (i / 3.0)) % 1.0;
            float rr = r + (float)(phase * 42);
            using Pen ring = new(Color.FromArgb((int)(90 * (1 - phase)), tone), 2.5f);
            _g.DrawEllipse(ring, cx - rr, cy - rr, rr * 2, rr * 2);
        }

        using (GraphicsPath orb = new())
        {
            orb.AddEllipse(cx - r, cy - r, r * 2, r * 2);
            using PathGradientBrush fill = new(orb)
            {
                CenterColor = Lighten(tone, 0.55f),
                SurroundColors = [Color.FromArgb(255, (int)(tone.R * 0.45), (int)(tone.G * 0.45), (int)(tone.B * 0.55))],
                CenterPoint = new PointF(cx - (20 * k), cy - (24 * k)),
            };
            _g.FillPath(fill, orb);
        }

        switch (s.Presence)
        {
            case AgentPresence.Speaking:
                for (int i = 0; i < 5; i++)
                {
                    double level = 0.35 + (0.65 * Math.Abs(Math.Sin((t * 7.5) + (i * 1.3)) * Math.Sin((t * 3.1) + i)));
                    float h = (float)((16 + (level * 76)) * k);
                    FillRounded(Color.FromArgb(235, 255, 255, 255), cx - (58 * k) + (i * 26 * k), cy - (h / 2), 14 * k, h, 7 * k);
                }

                break;

            case AgentPresence.Thinking:
                using (Pen arc = new(Color.FromArgb(230, 255, 255, 255), 7f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    _g.DrawArc(arc, cx - 34, cy - 34, 68, 68, (float)(t * 300 % 360), 110);
                }

                break;

            case AgentPresence.Listening:
                for (int i = 0; i < 3; i++)
                {
                    double pulse = 0.5 + (0.5 * Math.Sin((t * 4) - (i * 0.9)));
                    float d = (float)(10 + (pulse * 6));
                    using SolidBrush dot = new(Color.FromArgb(235, 255, 255, 255));
                    _g.FillEllipse(dot, cx - 26 + (i * 26) - (d / 2), cy - (d / 2), d, d);
                }

                break;

            default:
                using (SolidBrush glyph = new(Color.FromArgb(240, 255, 255, 255)))
                using (StringFormat center = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (Font letter = new("Segoe UI Semibold", 60, GraphicsUnit.Pixel))
                {
                    _g.DrawString(s.AgentName.Length > 0 ? s.AgentName[..1].ToUpperInvariant() : "A", letter, glyph, new RectangleF(cx - r, cy - r - 3, r * 2, r * 2), center);
                }

                break;
        }
        Text(s.AgentName, _name, Color.White, new RectangleF(cx - 140, cy + 92, 280, 44), Centred);
        Text(Ellipsize(s.Specialism ?? "AI meeting facilitator", _small, 270), _small, Muted, new RectangleF(cx - 140, cy + 134, 280, 22), Centred);

        string status = s.Presence switch
        {
            AgentPresence.Listening => "Listening",
            AgentPresence.Thinking => "Working on it",
            AgentPresence.Speaking => "Speaking",
            _ => $"Say \u201C{s.AgentName}\u201D",
        };

        SizeF size = _g.MeasureString(status, _label);
        float pw = size.Width + 34, px = cx - (pw / 2), py = cy + 164;
        FillRounded(Color.FromArgb(60, tone), px, py, pw, 30, 15);
        using (SolidBrush dot = new(tone))
        {
            _g.FillEllipse(dot, px + 12, py + 10, 10, 10);
        }

        Text(status, _label, Color.White, new RectangleF(px + 26, py + 5, pw - 26, 22), null);
    }

    // ---- Overview: header, caption, cards ----------------------------------------------

    private void DrawHeader(TileState s)
    {
        const float x = 310, y = 22, w = 948, h = 300;
        FillRounded(Color.FromArgb(22, 255, 255, 255), x, y, w, h, 20);

        Text(Ellipsize(s.Subject ?? "Teams meeting", _title, w - 360), _title, Color.White, new RectangleF(x + 26, y + 18, w - 360, 34), null);

        // Clock, top right of the panel.
        {
            Text(Clock(s.Elapsed), _big, Color.White, new RectangleF(x + w - 330, y + 4, 300, 62), RightAligned);
            string sub = "elapsed";
            Color tone = Muted;
            if (s.Remaining is { } left)
            {
                bool over = left < TimeSpan.Zero;
                sub = over ? $"+{Minutes(-left)} over" : $"{Minutes(left)} left";
                tone = over ? Over : left.TotalMinutes <= 5 ? Warn : Muted;
            }

            Text(sub, _label, tone, new RectangleF(x + w - 330, y + 66, 300, 22), RightAligned);
        }

        if (s.ScheduledLength is { TotalSeconds: > 0 } length)
        {
            double used = Math.Clamp(s.Elapsed.TotalSeconds / length.TotalSeconds, 0, 1);
            bool over = s.Remaining is { } r && r < TimeSpan.Zero;
            Bar(x + 26, y + 98, w - 52, 6, used, over ? Over : used > 0.85 ? Warn : Ok);
        }

        float cy = y + 120;
        Text("NOW", _label, Muted, new RectangleF(x + 26, cy, 100, 20), null);

        if (s.Agenda.Current is { } item)
        {
            Text($"Item {s.Agenda.CurrentIndex + 1} of {s.Agenda.Items.Count}", _label, Muted, new RectangleF(x + w - 230, cy, 204, 20), RightAligned);
            Text(Ellipsize(item.Title, _title, w - 52), _title, Color.White, new RectangleF(x + 26, cy + 22, w - 52, 34), null);

            float by = cy + 70;
            if (item.Minutes is { } minutes && minutes > 0)
            {
                double used = s.Agenda.CurrentElapsed.TotalMinutes / minutes;
                Color tone = used >= 1 ? Over : used >= 0.75 ? Warn : Ok;
                Bar(x + 26, by + 6, w - 260, 12, Math.Min(used, 1), tone);
                TimeSpan rem = s.Agenda.CurrentRemaining ?? TimeSpan.Zero;
                Text(rem >= TimeSpan.Zero ? $"{Clock(rem)} left" : $"+{Clock(-rem)} over", _body, tone, new RectangleF(x + w - 220, by - 4, 194, 30), RightAligned);
            }
            else
            {
                Text($"{Clock(s.Agenda.CurrentElapsed)} on this item", _body, Muted, new RectangleF(x + 26, by - 4, 400, 30), null);
            }

            string next = s.Agenda.Next is { } n ? $"Next: {n.Title}" : "Last item";
            Text(Ellipsize(next, _body, w - 340), _body, Muted, new RectangleF(x + 26, by + 34, w - 340, 30), null);
        }
        else
        {
            string hint = s.Agenda.Items.Count > 0 ? "Agenda complete" : $"No agenda yet \u00B7 say \u201C{s.AgentName}, our agenda is\u2026\u201D";
            Text(Ellipsize(hint, _body, w - 52), _body, Muted, new RectangleF(x + 26, cy + 26, w - 52, 30), null);
        }

        if (s.Timer is { } timer)
        {
            TimeSpan rem = timer.EndsAt - DateTimeOffset.UtcNow;
            float tx = x + w - 300, ty = y + h - 54;
            FillRounded(Color.FromArgb(46, Warn), tx, ty, 274, 38, 12);
            Text(Ellipsize(timer.Label, _small, 160), _small, Color.White, new RectangleF(tx + 14, ty + 9, 160, 22), null);
            Text(Clock(rem < TimeSpan.Zero ? TimeSpan.Zero : rem), _title, Warn, new RectangleF(tx + 150, ty + 1, 110, 34), RightAligned);
        }
    }

    private void DrawCaption(TileState s, float y)
    {
        string text;
        int alpha = 255;
        if (!string.IsNullOrWhiteSpace(s.Caption) && s.CaptionAt is { } at && (DateTimeOffset.UtcNow - at).TotalSeconds <= 25)
        {
            double age = (DateTimeOffset.UtcNow - at).TotalSeconds;
            alpha = age < 20 ? 255 : (int)(255 * (25 - age) / 5);
            text = "\u201C" + s.Caption.Trim() + "\u201D";
        }
        else if (s.Restricted)
        {
            FillRounded(Color.FromArgb(70, Warn), 22, y, 1236, 40, 12);
            Text("External guest present \u00B7 internal data is off", _label, Color.White, new RectangleF(22, y + 11, 1236, 22), Centred);
            return;
        }
        else
        {
            Text($"Notes on \u00B7 {s.People} in meeting \u00B7 say \u201C{s.AgentName}, show the actions\u201D for detail", _small, Muted, new RectangleF(30, y + 10, 1220, 22), null);
            return;
        }

        FillRounded(Color.FromArgb(alpha * 110 / 255, 0, 0, 0), 22, y, 1236, 40, 12);
        Text(Ellipsize(text, _caption, 1196), _caption, Color.FromArgb(alpha, 255, 255, 255), new RectangleF(40, y + 7, 1196, 30), null);
    }

    private void DrawCards(TileState s)
    {
        const float y = 392, h = 306, gap = 14, w = (1236 - (3 * gap)) / 4f;

        DrawCard(22, y, w, h, "Decisions", s.Decisions.Count, DecisionTone,
            [.. s.Decisions.TakeLast(3).Reverse().Select(d => (d, (string?)null))]);

        DrawCard(22 + w + gap, y, w, h, "Actions", s.Actions.Count, ActionTone,
            [.. s.Actions.TakeLast(3).Reverse().Select(a => (a.Task, (string?)$"{a.Owner}{(string.IsNullOrWhiteSpace(a.Due) ? string.Empty : " \u00B7 " + a.Due)}"))]);

        // Specialist tracking when a skill is active; otherwise the parking lot.
        List<(string, string?)> tracked = [.. s.Tracked
            .SelectMany(kv => kv.Value.Select(v => (v, (string?)kv.Key)))
            .TakeLast(3).Reverse()];
        int trackedCount = s.Tracked.Sum(kv => kv.Value.Count);
        if (trackedCount > 0)
        {
            DrawCard(22 + (2 * (w + gap)), y, w, h, "Tracked", trackedCount, TrackedTone, tracked,
                s.Parked.Count > 0 ? $"+ {s.Parked.Count} parked" : null);
        }
        else
        {
            DrawCard(22 + (2 * (w + gap)), y, w, h, "Parked", s.Parked.Count, Warn,
                [.. s.Parked.TakeLast(3).Reverse().Select(p => (p, (string?)null))]);
        }

        IReadOnlyList<MeetingInsight> insights = s.Insights ?? [];
        DrawCard(22 + (3 * (w + gap)), y, w, h, "Insights", insights.Count, InsightTone,
            [.. insights.TakeLast(2).Reverse().Select(i => (i.Title + ": " + i.Text, (string?)(i.Citation ?? KindLabel(i.Kind))))],
            dotColours: [.. insights.TakeLast(2).Reverse().Select(i => SeverityTone(i.Severity))]);
    }

    private static readonly Color InsightTone = Color.FromArgb(129, 140, 248);

    private static Color SeverityTone(string severity) => severity switch
    {
        "high" => Over,
        "medium" => Warn,
        _ => Color.FromArgb(96, 165, 250),
    };

    private static string KindLabel(string kind) => kind switch
    {
        "knowledge" => "Knowledge",
        "risk" => "Risk",
        "alignment" => "Alignment",
        "pace" => "Pace",
        "skill" => "Skill",
        _ => "Facilitation",
    };

    private void DrawCard(float x, float y, float w, float h, string title, int count, Color tone, IReadOnlyList<(string Text, string? Meta)> items, string? footer = null, IReadOnlyList<Color>? dotColours = null)
    {
        FillRounded(Color.FromArgb(24, 255, 255, 255), x, y, w, h, 18);
        FillRounded(tone, x, y + 18, 5, 40, 2.5f);

        Text(count.ToString(), _count, Color.White, new RectangleF(x + 22, y + 10, 90, 46), null);
        float countWidth = _g.MeasureString(count.ToString(), _count, PointF.Empty, StringFormat.GenericTypographic).Width;
        Text(title.ToUpperInvariant(), _label, tone, new RectangleF(x + 30 + countWidth, y + 26, w - countWidth - 50, 20), null);

        float iy = y + 70;
        if (items.Count == 0)
        {
            Text("Nothing yet", _item, Muted, new RectangleF(x + 22, iy, w - 44, 26), null);
        }

        for (int index = 0; index < items.Count; index++)
        {
            (string text, string? meta) = items[index];
            Color dot = dotColours is { Count: > 0 } dc ? dc[Math.Min(index, dc.Count - 1)] : tone;
            using (SolidBrush bullet = new(dot))
            {
                _g.FillEllipse(bullet, x + 22, iy + 10, 7, 7);
            }

            foreach (string line in Wrap(text, _item, w - 60, meta is null ? 2 : (dotColours is null ? 1 : 3)))
            {
                Text(line, _item, Color.White, new RectangleF(x + 38, iy, w - 60, 26), null);
                iy += 25;
            }

            if (meta is not null)
            {
                Text(Ellipsize(meta, _small, w - 60), _small, Muted, new RectangleF(x + 38, iy, w - 60, 22), null);
                iy += 22;
            }

            iy += 14;
        }

        if (footer is not null)
        {
            Text(footer, _small, Muted, new RectangleF(x + 22, y + h - 32, w - 44, 22), null);
        }
    }

    // ---- Breakdown view ----------------------------------------------------------------

    private void DrawBreakdown(TileState s)
    {
        const float x = 310, y = 22, w = 948, h = 676;
        (string title, Color tone, List<(string Text, string? Meta)> items) = s.View switch
        {
            TileView.Actions => ("Actions", ActionTone, s.Actions.Select(a =>
                (a.Task, (string?)$"{a.Owner}{(string.IsNullOrWhiteSpace(a.Due) ? string.Empty : " \u00B7 due " + a.Due)}")).ToList()),
            TileView.Decisions => ("Decisions", DecisionTone, s.Decisions.Select(d => (d, (string?)null)).ToList()),
            TileView.Parked => ("Parking lot", Warn, s.Parked.Select(p => (p, (string?)null)).ToList()),
            TileView.Tracked => ("Tracked", TrackedTone, s.Tracked.SelectMany(kv => kv.Value.Select(v => (v, (string?)kv.Key))).ToList()),
            TileView.Insights => ("Insights", InsightTone, (s.Insights ?? []).Select(i =>
                ($"{i.Title}: {i.Text}", (string?)(i.Citation ?? KindLabel(i.Kind)))).ToList()),
            _ => ("Agenda", Ok, s.Agenda.Items.Select((a, i) => (
                $"{(s.Agenda.Completed.Contains(i) ? "\u2713 " : i == s.Agenda.CurrentIndex ? "\u25B6 " : string.Empty)}{a.Title}",
                (string?)(a.Minutes is { } m ? $"{m:0.#} min" : null))).ToList()),
        };

        FillRounded(Color.FromArgb(24, 255, 255, 255), x, y, w, h, 20);
        FillRounded(tone, x, y + 24, 6, 44, 3);
        Text(title, _name, Color.White, new RectangleF(x + 28, y + 18, w - 300, 48), null);
        {
            Text($"{items.Count} total", _body, Muted, new RectangleF(x + w - 260, y + 30, 232, 30), RightAligned);
        }

        Text(Ellipsize(s.Subject ?? string.Empty, _small, w - 56), _small, Muted, new RectangleF(x + 28, y + 70, w - 56, 22), null);

        // Insights are sentences, not labels: give them room to wrap, with the source underneath.
        bool longForm = s.View == TileView.Insights;
        int maxRows = longForm ? 5 : 10;
        IEnumerable<(string Text, string? Meta)> shown = items.Count > maxRows ? items.Skip(items.Count - maxRows) : items;
        int number = Math.Max(0, items.Count - maxRows);
        float iy = y + 112;

        if (items.Count == 0)
        {
            Text("Nothing recorded yet.", _body, Muted, new RectangleF(x + 28, iy, w - 56, 30), null);
        }

        foreach ((string text, string? meta) in shown)
        {
            number++;
            Text($"{number}", _body, tone, new RectangleF(x + 20, iy, 34, 30), RightAligned);

            if (longForm)
            {
                foreach (string line in Wrap(text, _body, w - 110, 2))
                {
                    Text(line, _body, Color.White, new RectangleF(x + 68, iy, w - 110, 30), null);
                    iy += 29;
                }

                if (meta is not null)
                {
                    Text(Ellipsize(meta, _item, w - 110), _item, Muted, new RectangleF(x + 68, iy + 2, w - 110, 26), null);
                    iy += 28;
                }

                iy += 22;
                continue;
            }

            float metaWidth = meta is null ? 0 : Math.Min(300, _g.MeasureString(meta, _item, PointF.Empty, StringFormat.GenericTypographic).Width + 8);
            Text(Ellipsize(text, _body, w - 110 - metaWidth), _body, Color.White, new RectangleF(x + 68, iy, w - 110 - metaWidth, 30), null);
            if (meta is not null)
            {
                Text(Ellipsize(meta, _item, 300), _item, Muted, new RectangleF(x + w - 28 - metaWidth, iy + 2, metaWidth, 28), RightAligned);
            }

            iy += 54;
        }

        if (items.Count > maxRows)
        {
            Text($"Showing the latest {maxRows}. The full list is in the recap.", _small, Muted, new RectangleF(x + 28, y + h - 34, w - 56, 22), null);
        }

        // Caption still matters in breakdown mode: it is how people see what Aria just said.
        if (!string.IsNullOrWhiteSpace(s.Caption) && s.CaptionAt is { } at && (DateTimeOffset.UtcNow - at).TotalSeconds <= 25)
        {
            FillRounded(Color.FromArgb(110, 0, 0, 0), 22, 640, 270, 58, 12);
            Text(Ellipsize("\u201C" + s.Caption.Trim() + "\u201D", _small, 250), _small, Color.White, new RectangleF(32, 657, 250, 24), null);
        }
    }

    // ---- Primitives ----------------------------------------------------------------------

    private static Color Tone(AgentPresence presence) => presence switch
    {
        AgentPresence.Listening => Color.FromArgb(45, 212, 191),
        AgentPresence.Thinking => Warn,
        AgentPresence.Speaking => Color.FromArgb(167, 139, 250),
        _ => Color.FromArgb(110, 106, 240),
    };

    private void Bar(float x, float y, float w, float h, double fraction, Color tone)
    {
        FillRounded(Color.FromArgb(40, 255, 255, 255), x, y, w, h, h / 2);
        float fw = (float)Math.Max(h, w * Math.Clamp(fraction, 0, 1));
        FillRounded(tone, x, y, fw, h, h / 2);
    }

    private void FillRounded(Color color, float x, float y, float w, float h, float radius)
    {
        using GraphicsPath path = new();
        float d = Math.Max(0.5f, Math.Min(radius * 2, Math.Min(w, h)));
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
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

    private static Color Lighten(Color c, float amount) => Color.FromArgb(
        c.A,
        (int)(c.R + ((255 - c.R) * amount)),
        (int)(c.G + ((255 - c.G) * amount)),
        (int)(c.B + ((255 - c.B) * amount)));

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
        _title.Dispose();
        _name.Dispose();
        _body.Dispose();
        _item.Dispose();
        _small.Dispose();
        _big.Dispose();
        _count.Dispose();
        _label.Dispose();
        _caption.Dispose();
    }
}
