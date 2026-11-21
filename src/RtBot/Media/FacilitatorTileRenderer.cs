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
    int Decisions,
    int Actions,
    int Parked,
    int People,
    bool Restricted,
    string? Caption,
    DateTimeOffset? CaptionAt,
    string? Specialism = null);

/// <summary>
/// Draws Aria's video tile: a presence orb that reacts to listening, thinking and speaking, and a
/// facilitator panel with the clock, agenda timebox, timers and the running tallies.
/// </summary>
/// <remarks>
/// Rendered with GDI+ into one reused BGRA bitmap and converted to NV12, the raw format the media
/// platform encodes. One renderer per call; not thread-safe.
/// </remarks>
public sealed class FacilitatorTileRenderer : IDisposable
{
    private static readonly Color Background1 = Color.FromArgb(14, 14, 34);
    private static readonly Color Background2 = Color.FromArgb(32, 26, 82);
    private static readonly Color Muted = Color.FromArgb(160, 164, 196);
    private static readonly Color Ok = Color.FromArgb(52, 211, 153);
    private static readonly Color Warn = Color.FromArgb(251, 191, 36);
    private static readonly Color Over = Color.FromArgb(248, 113, 113);

    private readonly Bitmap _bitmap;
    private readonly Graphics _g;
    private readonly float _scale;
    private readonly Font _title;
    private readonly Font _name;
    private readonly Font _body;
    private readonly Font _small;
    private readonly Font _big;
    private readonly Font _label;
    private readonly Font _caption;
    private readonly byte[] _nv12;

    public FacilitatorTileRenderer(int width, int height)
    {
        Width = width;
        Height = height;
        _scale = width / 1280f;
        _bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        _g = Graphics.FromImage(_bitmap);
        _g.SmoothingMode = SmoothingMode.AntiAlias;
        _g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        _g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        _g.ScaleTransform(_scale, _scale);

        _title = new Font("Segoe UI Semibold", 26, GraphicsUnit.Pixel);
        _name = new Font("Segoe UI Semibold", 46, GraphicsUnit.Pixel);
        _body = new Font("Segoe UI", 22, GraphicsUnit.Pixel);
        _small = new Font("Segoe UI", 17, GraphicsUnit.Pixel);
        _big = new Font("Segoe UI Light", 54, GraphicsUnit.Pixel);
        _label = new Font("Segoe UI Semibold", 15, GraphicsUnit.Pixel);
        _caption = new Font("Segoe UI", 23, FontStyle.Italic, GraphicsUnit.Pixel);
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
        DrawPanel(s);
        DrawCaption(s);
        return ToNv12();
    }

    private void DrawPresence(TileState s, double t)
    {
        const float cx = 250, cy = 280, r = 112;
        Color tone = s.Presence switch
        {
            AgentPresence.Listening => Color.FromArgb(45, 212, 191),
            AgentPresence.Thinking => Warn,
            AgentPresence.Speaking => Color.FromArgb(167, 139, 250),
            _ => Color.FromArgb(110, 106, 240),
        };

        // Halo rings breathe slowly at rest and quickly while speaking.
        double speed = s.Presence == AgentPresence.Speaking ? 3.2 : 1.1;
        for (int i = 0; i < 3; i++)
        {
            double phase = ((t * speed / 2.5) + (i / 3.0)) % 1.0;
            float rr = r + (float)(phase * 70);
            int alpha = (int)(90 * (1 - phase));
            using Pen ring = new(Color.FromArgb(alpha, tone), 3f);
            _g.DrawEllipse(ring, cx - rr, cy - rr, rr * 2, rr * 2);
        }

        using (GraphicsPath orb = new())
        {
            orb.AddEllipse(cx - r, cy - r, r * 2, r * 2);
            using PathGradientBrush fill = new(orb)
            {
                CenterColor = Lighten(tone, 0.55f),
                SurroundColors = [Color.FromArgb(255, (int)(tone.R * 0.45), (int)(tone.G * 0.45), (int)(tone.B * 0.55))],
                CenterPoint = new PointF(cx - 30, cy - 36),
            };
            _g.FillPath(fill, orb);
        }

        switch (s.Presence)
        {
            case AgentPresence.Speaking:
                for (int i = 0; i < 5; i++)
                {
                    double level = 0.35 + (0.65 * Math.Abs(Math.Sin((t * 7.5) + (i * 1.3)) * Math.Sin((t * 3.1) + i)));
                    float h = (float)(16 + (level * 76));
                    float x = cx - 58 + (i * 26);
                    FillRounded(Color.FromArgb(235, 255, 255, 255), x, cy - (h / 2), 14, h, 7);
                }

                break;

            case AgentPresence.Thinking:
                using (Pen arc = new(Color.FromArgb(230, 255, 255, 255), 9f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    _g.DrawArc(arc, cx - 52, cy - 52, 104, 104, (float)(t * 300 % 360), 110);
                }

                break;

            case AgentPresence.Listening:
                for (int i = 0; i < 3; i++)
                {
                    double pulse = 0.5 + (0.5 * Math.Sin((t * 4) - (i * 0.9)));
                    float d = (float)(14 + (pulse * 8));
                    using SolidBrush dot = new(Color.FromArgb(235, 255, 255, 255));
                    _g.FillEllipse(dot, cx - 40 + (i * 40) - (d / 2), cy - (d / 2), d, d);
                }

                break;

            default:
                using (SolidBrush glyph = new(Color.FromArgb(240, 255, 255, 255)))
                using (StringFormat center = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (Font letter = new("Segoe UI Semibold", 92, GraphicsUnit.Pixel))
                {
                    _g.DrawString(s.AgentName.Length > 0 ? s.AgentName[..1].ToUpperInvariant() : "A", letter, glyph, new RectangleF(cx - r, cy - r - 4, r * 2, r * 2), center);
                }

                break;
        }

        using StringFormat c = new() { Alignment = StringAlignment.Center };
        Text(s.AgentName, _name, Color.White, new RectangleF(cx - 220, cy + 150, 440, 60), c);
        Text(Ellipsize(s.Specialism ?? "AI meeting facilitator", _small, 440), _small, Muted, new RectangleF(cx - 220, cy + 206, 440, 26), c);

        string status = s.Presence switch
        {
            AgentPresence.Listening => "Listening",
            AgentPresence.Thinking => "Working on it",
            AgentPresence.Speaking => "Speaking",
            _ => $"Say \u201C{s.AgentName}\u201D to ask",
        };

        SizeF size = _g.MeasureString(status, _label);
        float pw = size.Width + 34, px = cx - (pw / 2), py = cy + 246;
        FillRounded(Color.FromArgb(60, tone), px, py, pw, 34, 17);
        using (SolidBrush dot = new(tone))
        {
            _g.FillEllipse(dot, px + 12, py + 12, 10, 10);
        }

        Text(status, _label, Color.White, new RectangleF(px + 26, py + 7, pw - 26, 24), null);
    }

    private void DrawPanel(TileState s)
    {
        const float x = 520, y = 40, w = 720, h = 520;
        FillRounded(Color.FromArgb(22, 255, 255, 255), x, y, w, h, 22);

        float cy = y + 26;
        Text(Ellipsize(s.Subject ?? "Teams meeting", _title, w - 60), _title, Color.White, new RectangleF(x + 30, cy, w - 60, 36), null);
        cy += 44;

        // Meeting clock.
        Text(Clock(s.Elapsed), _big, Color.White, new RectangleF(x + 26, cy - 6, 260, 70), null);
        Text("elapsed", _small, Muted, new RectangleF(x + 32, cy + 60, 120, 24), null);

        if (s.Remaining is { } left)
        {
            bool over = left < TimeSpan.Zero;
            string remaining = over ? $"+{Minutes(-left)} over" : $"{Minutes(left)} left";
            using StringFormat right = new() { Alignment = StringAlignment.Far };
            Text(remaining, _title, over ? Over : left.TotalMinutes <= 5 ? Warn : Color.White, new RectangleF(x + w - 330, cy + 8, 300, 36), right);
            Text("in this meeting", _small, Muted, new RectangleF(x + w - 330, cy + 46, 300, 24), right);

            if (s.ScheduledLength is { TotalSeconds: > 0 } length)
            {
                double used = Math.Clamp(s.Elapsed.TotalSeconds / length.TotalSeconds, 0, 1);
                Bar(x + 30, cy + 96, w - 60, 8, used, over ? Over : used > 0.85 ? Warn : Ok);
            }
        }

        cy += 124;

        // Agenda.
        Text("NOW", _label, Muted, new RectangleF(x + 30, cy, 200, 22), null);
        if (s.Agenda.Current is { } item)
        {
            using StringFormat right = new() { Alignment = StringAlignment.Far };
            Text($"{s.Agenda.CurrentIndex + 1} of {s.Agenda.Items.Count}", _label, Muted, new RectangleF(x + w - 230, cy, 200, 22), right);
            cy += 24;
            Text(Ellipsize(item.Title, _title, w - 60), _title, Color.White, new RectangleF(x + 30, cy, w - 60, 36), null);
            cy += 42;

            if (item.Minutes is { } minutes && minutes > 0)
            {
                double used = s.Agenda.CurrentElapsed.TotalMinutes / minutes;
                Color tone = used >= 1 ? Over : used >= 0.75 ? Warn : Ok;
                Bar(x + 30, cy + 4, w - 240, 12, Math.Min(used, 1), tone);
                TimeSpan rem = s.Agenda.CurrentRemaining ?? TimeSpan.Zero;
                string label = rem >= TimeSpan.Zero ? $"{Clock(rem)} left" : $"+{Clock(-rem)} over";
                using StringFormat r2 = new() { Alignment = StringAlignment.Far };
                Text(label, _body, tone, new RectangleF(x + w - 200, cy - 6, 170, 30), r2);
            }
            else
            {
                Text($"{Clock(s.Agenda.CurrentElapsed)} on this item", _body, Muted, new RectangleF(x + 30, cy - 6, 400, 30), null);
            }

            cy += 34;
            string next = s.Agenda.Next is { } n ? $"Next: {n.Title}" : "Last item";
            Text(Ellipsize(next, _body, w - 60), _body, Muted, new RectangleF(x + 30, cy, w - 60, 30), null);
            cy += 40;
        }
        else
        {
            cy += 26;
            string hint = s.Agenda.Items.Count > 0 ? "Agenda complete" : $"No agenda yet. Say \u201C{s.AgentName}, our agenda is\u2026\u201D";
            Text(Ellipsize(hint, _body, w - 60), _body, Muted, new RectangleF(x + 30, cy, w - 60, 30), null);
            cy += 74;
        }

        // Timer.
        if (s.Timer is { } timer)
        {
            TimeSpan rem = timer.EndsAt - DateTimeOffset.UtcNow;
            FillRounded(Color.FromArgb(40, Warn), x + 30, cy, w - 60, 40, 12);
            Text(Ellipsize($"Timer \u00B7 {timer.Label}", _body, w - 220), _body, Color.White, new RectangleF(x + 46, cy + 5, w - 220, 30), null);
            using StringFormat right = new() { Alignment = StringAlignment.Far };
            Text(Clock(rem < TimeSpan.Zero ? TimeSpan.Zero : rem), _title, Warn, new RectangleF(x + w - 200, cy + 1, 156, 36), right);
        }

        // Tallies.
        float ty = y + h - 78;
        Chip(x + 30, ty, "Decisions", s.Decisions);
        Chip(x + 250, ty, "Actions", s.Actions);
        Chip(x + 470, ty, "Parked", s.Parked);

        if (s.Restricted)
        {
            FillRounded(Color.FromArgb(70, Warn), x, y + h + 14, w, 36, 12);
            using StringFormat c = new() { Alignment = StringAlignment.Center };
            Text("External guest present \u00B7 internal data is off", _label, Color.White, new RectangleF(x, y + h + 22, w, 24), c);
        }
    }

    private void DrawCaption(TileState s)
    {
        if (string.IsNullOrWhiteSpace(s.Caption) || s.CaptionAt is not { } at)
        {
            Text($"Notes on \u00B7 {s.People} in meeting", _small, Muted, new RectangleF(40, 660, 440, 26), null);
            return;
        }

        double age = (DateTimeOffset.UtcNow - at).TotalSeconds;
        if (age > 25)
        {
            Text($"Notes on \u00B7 {s.People} in meeting", _small, Muted, new RectangleF(40, 660, 440, 26), null);
            return;
        }

        int alpha = age < 20 ? 255 : (int)(255 * (25 - age) / 5);
        FillRounded(Color.FromArgb(alpha * 140 / 255, 0, 0, 0), 40, 610, 1200, 80, 16);
        string text = s.Caption.Length > 170 ? "\u2026" + s.Caption[^168..] : s.Caption;
        Text(text, _caption, Color.FromArgb(alpha, 255, 255, 255), new RectangleF(62, 618, 1156, 66), null);
    }

    private void Chip(float x, float y, string label, int value)
    {
        FillRounded(Color.FromArgb(30, 255, 255, 255), x, y, 200, 54, 14);
        Text(value.ToString(), _title, Color.White, new RectangleF(x + 16, y + 9, 60, 36), null);
        Text(label, _small, Muted, new RectangleF(x + 70, y + 16, 124, 26), null);
    }

    private void Bar(float x, float y, float w, float h, double fraction, Color tone)
    {
        FillRounded(Color.FromArgb(40, 255, 255, 255), x, y, w, h, h / 2);
        float fw = (float)Math.Max(h, w * Math.Clamp(fraction, 0, 1));
        FillRounded(tone, x, y, fw, h, h / 2);
    }

    private void FillRounded(Color color, float x, float y, float w, float h, float radius)
    {
        using GraphicsPath path = new();
        float d = Math.Min(radius * 2, Math.Min(w, h));
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

    private string Ellipsize(string text, Font font, float width)
    {
        // Measured with the same format it is drawn with, or small sizes clip the last glyphs.
        float Measure(string s) => _g.MeasureString(s, font, PointF.Empty, StringFormat.GenericTypographic).Width;

        if (Measure(text) <= width)
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
        _g.Dispose();
        _bitmap.Dispose();
        _title.Dispose();
        _name.Dispose();
        _body.Dispose();
        _small.Dispose();
        _big.Dispose();
        _label.Dispose();
        _caption.Dispose();
    }
}
