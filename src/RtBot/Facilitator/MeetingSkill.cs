using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RtBot.Facilitator;

/// <summary>
/// A domain or role specialism the facilitator can take on: what to listen for, what to record,
/// and what a good meeting of this kind must cover.
/// </summary>
/// <remarks>
/// Authored as a <c>SKILL.md</c> file: YAML front matter plus Markdown guidance. The same shape
/// as Agent 365, Claude and VS Code skills, so one library can serve several agents.
/// </remarks>
public sealed record MeetingSkill(
    string Name,
    string Description,
    IReadOnlyList<string> Triggers,
    IReadOnlyList<string> Track,
    IReadOnlyList<string> Checklist,
    string Guidance,
    string Source);

/// <summary>Who this instance works for and what it is for. One per deployment.</summary>
public sealed record AgentProfile(
    string Name,
    string Purpose,
    IReadOnlyList<string> AlwaysOnSkills,
    string Guidance)
{
    public static AgentProfile Default { get; } = new("default", "A general-purpose meeting facilitator.", [], string.Empty);
}

/// <summary>Parses skill and profile files. Malformed files are reported, not fatal.</summary>
public static class SkillParser
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static MeetingSkill ParseSkill(string markdown, string source)
    {
        (SkillFrontMatter meta, string body) = Split<SkillFrontMatter>(markdown, source);
        if (string.IsNullOrWhiteSpace(meta.Name))
        {
            throw new FormatException($"{source}: front matter needs a 'name'.");
        }

        return new MeetingSkill(
            meta.Name.Trim(),
            meta.Description?.Trim() ?? string.Empty,
            Clean(meta.Triggers),
            Clean(meta.Track),
            Clean(meta.Checklist),
            body,
            source);
    }

    public static AgentProfile ParseProfile(string markdown, string source)
    {
        (ProfileFrontMatter meta, string body) = Split<ProfileFrontMatter>(markdown, source);
        return new AgentProfile(
            string.IsNullOrWhiteSpace(meta.Name) ? Path.GetFileNameWithoutExtension(source) : meta.Name.Trim(),
            meta.Purpose?.Trim() ?? AgentProfile.Default.Purpose,
            Clean(meta.Skills),
            body);
    }

    private static (T Meta, string Body) Split<T>(string markdown, string source)
        where T : new()
    {
        string text = markdown.Replace("\r\n", "\n").TrimStart('\uFEFF');
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
        {
            throw new FormatException($"{source}: missing '---' front matter.");
        }

        int end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new FormatException($"{source}: front matter is not closed with '---'.");
        }

        T meta = Yaml.Deserialize<T>(text[4..end]) ?? new T();

        // Skip the rest of the closing '---' line only; the body may itself start with '-'.
        int bodyStart = text.IndexOf('\n', end + 1);
        string body = bodyStart < 0 ? string.Empty : text[(bodyStart + 1)..].Trim();
        return (meta, body);
    }

    private static List<string> Clean(List<string>? items) =>
        items?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList() ?? [];

    private sealed class SkillFrontMatter
    {
        public string? Name { get; set; }

        public string? Description { get; set; }

        public List<string>? Triggers { get; set; }

        public List<string>? Track { get; set; }

        public List<string>? Checklist { get; set; }
    }

    private sealed class ProfileFrontMatter
    {
        public string? Name { get; set; }

        public string? Purpose { get; set; }

        public List<string>? Skills { get; set; }
    }
}
