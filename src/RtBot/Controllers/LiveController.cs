using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using RtBot.Configuration;
using RtBot.Facilitator;
using RtBot.Teams;

namespace RtBot.Controllers;

/// <summary>
/// A companion dashboard for a live call: the same notes, insights and Work IQ findings that
/// appear on Aria's video tile, in a full browser window or Copilot Managed Runtime player.
/// </summary>
/// <remarks>
/// Anonymous by design, because meeting attendees do not hold tokens for this app. Access is by
/// an unguessable per-call key that Aria posts only in that meeting's chat, and the page stops
/// working when the call ends. While an external guest is present, organisation-sourced findings
/// are withheld, the same rule the tile and tools follow.
/// </remarks>
[ApiController]
[EnableCors("CmrWorkspace")]
[AllowAnonymous]
public sealed class LiveController : ControllerBase
{
    private readonly TeamsCallingService _calling;
    private readonly VoiceOptions _voice;
    private readonly FacilitatorOptions _facilitator;
    private readonly AvatarOptions _avatar;
    private readonly IWebHostEnvironment _env;

    public LiveController(
        TeamsCallingService calling,
        VoiceOptions voice,
        FacilitatorOptions facilitator,
        AvatarOptions avatar,
        IWebHostEnvironment env)
    {
        _calling = calling;
        _voice = voice;
        _facilitator = facilitator;
        _avatar = avatar;
        _env = env;
    }

    [HttpGet("live/{callId}")]
    [HttpGet("console")]
    public IActionResult Page(string? callId = null)
    {
        // The page holds no data; the key travels in the URL fragment and is sent to the API as a header.
        Harden();
        string spaPath = Path.Combine(_env.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot"), "console", "index.html");
        if (System.IO.File.Exists(spaPath))
        {
            Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self'; font-src 'self' data:; img-src 'self' data: blob: https://*.d-id.com https://*.amazonaws.com; media-src 'self' blob: https://*.d-id.com; connect-src 'self' https://api.d-id.com wss://notifications.d-id.com wss://*.livekit.cloud https://*.livekit.cloud; base-uri 'self'; form-action 'none'; frame-ancestors 'self' https://*.managedapps.cloud.microsoft https://*.island.powerplatformusercontent.com";
            return PhysicalFile(spaPath, "text/html; charset=utf-8");
        }

        Response.Headers.ContentSecurityPolicy =
            "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        return Content(LivePage.Html, "text/html; charset=utf-8");
    }

    [HttpGet("api/live/active")]
    public IActionResult Active()
    {
        if (!MatchesShowcaseKey(Request.Headers["X-Live-Key"].ToString()))
        {
            return Unauthorized();
        }

        Harden();
        CallHandler? handler = _calling.ActiveHandlers
            .OrderByDescending(h => h.Meeting.JoinedAt)
            .FirstOrDefault();

        if (handler is null)
        {
            return NoContent();
        }

        return Ok(BuildStatePayload(handler.Meeting.CallId, handler));
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
        return Ok(BuildStatePayload(callId, handler));
    }

    private object BuildStatePayload(string callId, CallHandler handler)
    {
        MeetingContext m = handler.Meeting;
        MeetingNotes notes = m.SnapshotNotes();
        AgendaStatus agenda = m.SnapshotAgenda();
        bool restricted = m.IsRestricted;

        IEnumerable<MeetingInsight> insights = m.Insights;
        if (restricted)
        {
            insights = insights.Where(i => !i.Kind.Equals("knowledge", StringComparison.OrdinalIgnoreCase));
        }

        double audioMinutes = Math.Max(0.1, m.Elapsed.TotalMinutes);
        int scribePasses = m.ScribePassCount;
        int workIqCalls = m.WorkIqCallCount;
        double voiceCostUsd = Math.Round(audioMinutes * 0.0108, 4);
        double scribeCostUsd = Math.Round(scribePasses * 0.00055, 4);
        double workIqCostUsd = Math.Round(workIqCalls * 0.0025, 4);
        double totalLiveCostUsd = Math.Round(voiceCostUsd + scribeCostUsd + workIqCostUsd, 4);

        return new
        {
            callId,
            agent = handler.AgentName,
            subject = m.Subject,
            restricted,
            presence = handler.Presence.ToString().ToLowerInvariant(),
            speaking = handler.IsAgentSpeaking,
            caption = handler.LatestCaption,
            elapsed = (int)m.Elapsed.TotalSeconds,
            remaining = m.Remaining is { } r ? (int?)r.TotalSeconds : null,
            alignment = m.Alignment,
            alignmentNote = m.AlignmentNote,
            trend = m.AlignmentTrend,
            voices = m.Voices().Select(v => new { name = v.Name, share = v.Share }),
            skills = m.ActiveSkillDetails.Select(s => new
            {
                name = s.Name,
                label = s.Label,
                description = s.Description,
                dynamic = s.Dynamic,
                at = s.ActivatedAt,
                checklist = s.Checklist.Select(c => new { item = c.Item, covered = c.Covered }),
                tracks = s.Track,
            }),
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
            milestones = m.Milestones.TakeLast(12).Select(ms => new
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
            visuals = restricted
                ? Array.Empty<object>()
                : m.Visuals.Select((v, idx) => (object)new
                {
                    index = idx,
                    title = v.Title,
                    summary = v.Summary,
                    keyPoints = v.KeyPoints,
                    source = v.Source,
                    capturedFrom = v.CapturedFrom,
                    at = v.At,
                }).ToArray(),
            finops = new
            {
                voiceModel = _voice.Model,
                scribeModel = _facilitator.ScribeModel,
                audioMinutes = Math.Round(audioMinutes, 2),
                scribePasses,
                workIqCalls,
                workIqAvgMs = m.WorkIqAvgLatencyMs,
                voiceCostUsd,
                scribeCostUsd,
                workIqCostUsd,
                totalLiveCostUsd,
                costPerMinuteUsd = Math.Round(totalLiveCostUsd / Math.Max(1.0, audioMinutes), 4),
            },
            insights = insights.TakeLast(40).Select(i => new
            {
                id = $"{i.At.ToUnixTimeMilliseconds()}-{i.Kind}-{Math.Abs(StringComparer.Ordinal.GetHashCode(i.Title))}",
                kind = i.Kind,
                severity = i.Severity,
                title = i.Title,
                text = i.Text,
                citation = i.Citation,
                at = i.At,
                elapsedSec = Math.Max(0, (int)(i.At - m.JoinedAt).TotalSeconds),
            }),
            searching = restricted ? null : m.Searching,
            transcript = m.RecentTurns(40).Select(t => new { at = t.At, speaker = t.Speaker, text = t.Text, agent = t.IsAgent }),
        };
    }

    [HttpGet("api/live/{callId}/visuals/{index:int}")]
    public IActionResult VisualJpeg(string callId, int index)
    {
        CallHandler? handler = Find(callId, Request.Headers["X-Live-Key"].ToString());
        if (handler is null || handler.Meeting.IsRestricted)
        {
            return NotFound();
        }

        IReadOnlyList<VisualNote> visuals = handler.Meeting.Visuals;
        if (index < 0 || index >= visuals.Count || visuals[index].Jpeg.Length == 0)
        {
            return NotFound();
        }

        Harden();
        return File(visuals[index].Jpeg, "image/jpeg");
    }

    [HttpPost("api/live/{callId}/summary")]
    public async Task<IActionResult> SpeakBoardSummary(
        string callId,
        CancellationToken cancellationToken)
    {
        CallHandler? handler = Find(callId, Request.Headers["X-Live-Key"].ToString());
        if (handler is null)
        {
            return NotFound();
        }

        Harden();
        string speech = await handler.SynthesiseBoardSummaryAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new { speech });
    }

    private CallHandler? Find(string callId, string? key)
    {
        if (string.IsNullOrEmpty(key) || !_calling.TryGetHandler(callId, out CallHandler? handler) || handler is null)
        {
            return null;
        }

        byte[] given = Encoding.UTF8.GetBytes(key);
        byte[] expected = Encoding.UTF8.GetBytes(handler.Meeting.ViewKey);
        if (given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected))
        {
            return handler;
        }

        return MatchesShowcaseKey(key) ? handler : null;
    }

    private bool MatchesShowcaseKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || !OperatingSystem.IsWindows())
        {
            return false;
        }

        string? stored = ProtectedSecretFile.TryRead(_avatar.ShowcaseKeyFile);
        if (string.IsNullOrWhiteSpace(stored))
        {
            return false;
        }

        byte[] given = Encoding.UTF8.GetBytes(key);
        byte[] expected = Encoding.UTF8.GetBytes(stored.Trim());
        return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
    }

    private void Harden()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["X-Robots-Tag"] = "noindex";
    }
}
