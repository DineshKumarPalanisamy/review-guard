using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReviewGuard.Core;

/// <summary>
/// Glob matching for repository paths. Pure function, no file system.
/// "**" = any folders (also none), "*" = any characters inside one folder name, "?" = one character.
/// Case-insensitive. A leading "/" on the path or the pattern is ignored.
/// Examples: "**/Payments/**" matches "/src/App/Payments/Charge.cs".
/// </summary>
public static class PathMatcher
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsMatch(string path, string pattern)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(pattern);
        return Cache.GetOrAdd(pattern, ToRegex).IsMatch(path.TrimStart('/'));
    }

    /// <summary>The paths that match at least one pattern, in their original order.</summary>
    public static IReadOnlyList<string> Matching(IEnumerable<string> paths, IReadOnlyCollection<string> patterns) =>
        [.. paths.Where(p => patterns.Any(pattern => IsMatch(p, pattern)))];

    private static Regex ToRegex(string pattern)
    {
        var p = pattern.Trim().TrimStart('/');
        var sb = new StringBuilder("^");
        for (var i = 0; i < p.Length; i++)
        {
            var c = p[i];
            if (c == '*' && i + 1 < p.Length && p[i + 1] == '*')
            {
                // "**/" = zero or more folders. "**" at the end = everything below.
                var slash = i + 2 < p.Length && p[i + 2] == '/';
                sb.Append(slash ? "(?:.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else if (c == '*')
            {
                sb.Append("[^/]*");
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}

/// <summary>
/// The critical path lists from critical-paths.json. The lists add up: the default list,
/// plus the project list, plus the "Project/Repository" list. So a team can add paths but
/// can't drop a default one by mistake. See DESIGN.md, decisions of 2026-11-25.
/// </summary>
public sealed class CriticalPathConfig
{
    public static readonly CriticalPathConfig Empty = new([], new Dictionary<string, IReadOnlyList<string>>());

    private readonly IReadOnlyList<string> defaults;
    private readonly Dictionary<string, IReadOnlyList<string>> projects;

    public CriticalPathConfig(IReadOnlyList<string> defaults, IReadOnlyDictionary<string, IReadOnlyList<string>> projects)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(projects);
        this.defaults = defaults;
        this.projects = new(projects, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>{ "default": [...], "projects": { "Internal": [...], "Internal/repo": [...] } }</summary>
    public static CriticalPathConfig FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;

        static string[] List(JsonElement e) =>
            e.ValueKind == JsonValueKind.Array ? [.. e.EnumerateArray().Select(x => x.GetString()!).Where(x => !string.IsNullOrWhiteSpace(x))] : [];

        var defaults = root.TryGetProperty("default", out var d) ? List(d) : [];
        var projects = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("projects", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in p.EnumerateObject())
            {
                projects[entry.Name] = List(entry.Value);
            }
        }

        return new(defaults, projects);
    }

    public IReadOnlyList<string> PatternsFor(string? project, string? repository)
    {
        var result = new List<string>(defaults);
        if (!string.IsNullOrEmpty(project) && projects.TryGetValue(project, out var forProject))
        {
            result.AddRange(forProject);
        }

        if (!string.IsNullOrEmpty(project) && !string.IsNullOrEmpty(repository)
            && projects.TryGetValue($"{project}/{repository}", out var forRepo))
        {
            result.AddRange(forRepo);
        }

        return [.. result.Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
