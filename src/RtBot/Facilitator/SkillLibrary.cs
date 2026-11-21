using System.Text.Json.Nodes;
using RtBot.Configuration;

namespace RtBot.Facilitator;

/// <summary>
/// Loads the instance profile and the skill library, and picks the skills that fit a meeting.
/// </summary>
/// <remarks>
/// Built-in skills ship with the app; custom skills live in <see cref="FacilitatorOptions.CustomSkillsPath"/>
/// so they survive redeploys, and override a built-in skill of the same name. Files are read at
/// each join, so edits take effect from the next meeting without a restart.
/// </remarks>
public sealed class SkillLibrary
{
    private const int MaxActiveSkills = 3;

    private const string SelectPrompt = """
        You choose which specialist skills an AI meeting facilitator should apply to a meeting.
        You get the meeting subject, the agenda, and the available skills. Return JSON:
        {"skills":["skill-name", ...]}

        Pick at most three, only skills that clearly fit. Return {"skills":[]} if none fit.
        """;

    private readonly FacilitatorOptions _options;
    private readonly TextModelClient _model;
    private readonly ILogger<SkillLibrary> _logger;
    private readonly string _builtInRoot = Path.Combine(AppContext.BaseDirectory, "skills");

    public SkillLibrary(FacilitatorOptions options, TextModelClient model, ILogger<SkillLibrary> logger)
    {
        _options = options;
        _model = model;
        _logger = logger;
    }

    /// <summary>The profile and active skills as an instructions block for either model.</summary>
    public static string RenderInstructions(AgentProfile profile, IReadOnlyList<MeetingSkill> skills)
    {
        System.Text.StringBuilder sb = new();
        sb.AppendLine().Append("YOUR PURPOSE: ").AppendLine(profile.Purpose);
        if (!string.IsNullOrWhiteSpace(profile.Guidance))
        {
            sb.AppendLine(Clip(profile.Guidance, 2000));
        }

        foreach (MeetingSkill skill in skills)
        {
            sb.AppendLine().Append("SPECIALIST SKILL: ").Append(skill.Name).Append(" - ").AppendLine(skill.Description);
            if (skill.Track.Count > 0)
            {
                sb.Append("Listen for and record with track_item, under these categories: ").AppendLine(string.Join(", ", skill.Track));
            }

            if (skill.Checklist.Count > 0)
            {
                sb.Append("A good meeting of this kind covers: ").AppendLine(string.Join("; ", skill.Checklist));
                sb.AppendLine("When one of these has been dealt with, call mark_covered. Near the end, point out what was missed.");
            }

            sb.AppendLine(Clip(skill.Guidance, 2500));
        }

        return sb.ToString();
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "\u2026";

    public AgentProfile LoadProfile()
    {
        // A bare name only: the profile is joined into a path.
        if (!System.Text.RegularExpressions.Regex.IsMatch(_options.Profile, "^[A-Za-z0-9_-]{1,64}$"))
        {
            _logger.LogWarning("Profile name {Profile} is not a plain name; using the default.", _options.Profile);
            return AgentProfile.Default;
        }

        foreach (string dir in new[] { Path.Combine(_options.CustomSkillsPath, "profiles"), Path.Combine(_builtInRoot, "profiles") })
        {
            string path = Path.Combine(dir, $"{_options.Profile}.md");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                return SkillParser.ParseProfile(File.ReadAllText(path), path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Profile {Path} is invalid; using the default.", path);
            }
        }

        return AgentProfile.Default;
    }

    public IReadOnlyList<MeetingSkill> LoadSkills()
    {
        Dictionary<string, MeetingSkill> skills = new(StringComparer.OrdinalIgnoreCase);

        // Built-in first so custom files with the same name replace them.
        foreach (string root in new[] { _builtInRoot, _options.CustomSkillsPath })
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories))
            {
                try
                {
                    MeetingSkill skill = SkillParser.ParseSkill(File.ReadAllText(file), file);
                    skills[skill.Name] = skill;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Skipping invalid skill file {Path}.", file);
                }
            }
        }

        return [.. skills.Values.OrderBy(s => s.Name)];
    }

    /// <summary>Profile skills plus the ones that fit this meeting's subject and agenda.</summary>
    public async Task<IReadOnlyList<MeetingSkill>> SelectAsync(
        IReadOnlyList<MeetingSkill> available,
        AgentProfile profile,
        string? subject,
        IReadOnlyList<AgendaItem> agenda,
        CancellationToken cancellationToken)
    {
        List<MeetingSkill> chosen = [.. available.Where(s => profile.AlwaysOnSkills.Contains(s.Name, StringComparer.OrdinalIgnoreCase))];
        IReadOnlyList<MeetingSkill> candidates = [.. available.Where(s => !chosen.Contains(s))];
        if (candidates.Count == 0 || (string.IsNullOrWhiteSpace(subject) && agenda.Count == 0))
        {
            return chosen;
        }

        string context = $"SUBJECT: {subject}\nAGENDA: {string.Join("; ", agenda.Select(a => a.Title))}";
        List<string> picked = [];

        try
        {
            string catalogue = string.Join("\n", candidates.Select(s => $"- {s.Name}: {s.Description} (signals: {string.Join(", ", s.Triggers)})"));
            JsonNode? result = await _model.CompleteJsonAsync(
                _options.ScribeModel, SelectPrompt, $"{context}\n\nAVAILABLE SKILLS:\n{catalogue}", cancellationToken).ConfigureAwait(false);

            if (result?["skills"] is JsonArray array)
            {
                picked.AddRange(array.Select(n => n?.GetValue<string>()).OfType<string>());
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Skill selection by model failed; falling back to keyword triggers.");
            picked.AddRange(candidates
                .Where(s => s.Triggers.Any(t => context.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .Select(s => s.Name));
        }

        foreach (string name in picked)
        {
            if (chosen.Count >= MaxActiveSkills)
            {
                break;
            }

            if (candidates.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } skill && !chosen.Contains(skill))
            {
                chosen.Add(skill);
            }
        }

        return chosen;
    }
}
