using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RtBot.Facilitator;
using RtBot.Teams;

namespace RtBot.Controllers;

/// <summary>
/// A companion dashboard for a live call: the same notes, insights and Work IQ findings that
/// appear on Aria's video tile, in a full browser window.
/// </summary>
/// <remarks>
/// Anonymous by design, because meeting attendees do not hold tokens for this app. Access is by
/// an unguessable per-call key that Aria posts only in that meeting's chat, and the page stops
/// working when the call ends. While an external guest is present, organisation-sourced findings
/// are withheld, the same rule the tile and tools follow.
/// </remarks>
[ApiController]
[AllowAnonymous]
public sealed class LiveController : ControllerBase
{
    private readonly TeamsCallingService _calling;

    public LiveController(TeamsCallingService calling) => _calling = calling;

    [HttpGet("live/{callId}")]
    public IActionResult Page(string callId)
    {
        // The page holds no data; the key travels in the URL fragment and is sent to the API as a header.
        Harden();
        Response.Headers.ContentSecurityPolicy =
            "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        return Content(LivePage.Html, "text/html; charset=utf-8");
    }

    [HttpGet("api/live/{callId}")]
    public IActionResult State(string callId)
    {
        CallHandler? handler = Find(callId, Request.Headers["X-Live-Key"].ToString());
        if (handler is null)
        {
            return NotFound();
        }

        Harden();
        MeetingContext m = handler.Meeting;
        MeetingNotes notes = m.SnapshotNotes();
        AgendaStatus agenda = m.SnapshotAgenda();
        bool restricted = m.IsRestricted;

        IEnumerable<MeetingInsight> insights = m.Insights;
        if (restricted)
        {
            insights = insights.Where(i => !i.Kind.Equals("knowledge", StringComparison.OrdinalIgnoreCase));
        }

        return Ok(new
        {
            agent = handler.AgentName,
            subject = m.Subject,
            restricted,
            elapsed = (int)m.Elapsed.TotalSeconds,
            remaining = m.Remaining is { } r ? (int?)r.TotalSeconds : null,
            alignment = m.Alignment,
            alignmentNote = m.AlignmentNote,
            trend = m.AlignmentTrend,
            voices = m.Voices().Select(v => new { name = v.Name, share = v.Share }),
            skills = m.ActiveSkillBadges.Select(s => new { name = s.Name, label = s.Label, description = s.Description, dynamic = s.Dynamic, at = s.ActivatedAt }),
            uncovered = m.UncoveredChecklist,
            agenda = new
            {
                items = agenda.Items.Select((a, idx) => new
                {
                    title = a.Title,
                    minutes = a.Minutes,
                    spent = agenda.ActualMinutes is not null && agenda.ActualMinutes.TryGetValue(idx, out double sm) ? Math.Round(sm, 2) : 0,
                }),
                current = agenda.CurrentIndex,
                currentElapsed = (int)agenda.CurrentElapsed.TotalSeconds,
                done = agenda.Completed,
            },
            milestones = m.Milestones.TakeLast(10).Select(ms => new
            {
                label = ms.Label,
                at = (int)ms.AtElapsed.TotalSeconds,
                kind = ms.Kind,
            }),
            summary = notes.Summary,
            decisions = notes.Decisions,
            actions = notes.Actions.Select(a => new { owner = a.Owner, task = a.Task, due = a.Due }),
            questions = notes.OpenQuestions,
            parked = m.ParkingLot,
            tracked = m.Tracked,
            insights = insights.TakeLast(40).Select(i => new
            {
                kind = i.Kind,
                severity = i.Severity,
                title = i.Title,
                text = i.Text,
                citation = i.Citation,
                at = i.At,
            }),
            searching = restricted ? null : m.Searching,
            transcript = m.RecentTurns(40).Select(t => new { at = t.At, speaker = t.Speaker, text = t.Text, agent = t.IsAgent }),
        });
    }

    private CallHandler? Find(string callId, string? key)
    {
        if (string.IsNullOrEmpty(key) || !_calling.TryGetHandler(callId, out CallHandler? handler) || handler is null)
        {
            return null;
        }

        byte[] given = Encoding.UTF8.GetBytes(key);
        byte[] expected = Encoding.UTF8.GetBytes(handler.Meeting.ViewKey);
        return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected) ? handler : null;
    }

    private void Harden()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["X-Robots-Tag"] = "noindex";
    }
}
