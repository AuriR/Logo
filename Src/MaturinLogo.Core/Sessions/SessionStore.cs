using System.Text.Json;
using System.Text.RegularExpressions;
using MaturinLogo.Core.Primitives;

namespace MaturinLogo.Core.Sessions;

/// <summary>
/// Decides session names, keeps the list in order, and enforces the twelve-session cap so a child
/// cannot fill up localStorage. Numbers never get reused after a delete: SESSION3.logo stays gone
/// even if it was the third of twelve, because a name a child remembers is worth protecting.
/// </summary>
public static partial class SessionNaming
{
    public const int MaxSessions = 12;
    public const string Prefix = "SESSION";
    public const string Extension = ".logo";

    [GeneratedRegex(@"^SESSION(\d+)\.logo$", RegexOptions.IgnoreCase)]
    private static partial Regex SessionNamePattern();

    /// <summary>
    /// The next free SESSIONn.logo name, or null when the cap is reached.
    /// </summary>
    /// <param name="lastIssued">
    /// The highest number ever handed out, kept in the store. Counting only the names still on disk
    /// would let a deleted SESSION2.logo be handed out again, and a child who was told "your work is
    /// in SESSION2" would find someone else's code sitting there.
    /// </param>
    public static string? NextName(IReadOnlyList<string> existingNames, int lastIssued = 0)
    {
        int highest = lastIssued;
        foreach (var name in existingNames)
        {
            var match = SessionNamePattern().Match(name);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int n) && n > highest)
                highest = n;
        }

        if (existingNames.Count >= MaxSessions) return null;
        return Prefix + (highest + 1) + Extension;
    }

    /// <summary>The number inside a session name, or 0 when the name is not one of ours.</summary>
    public static int NumberOf(string name)
    {
        var match = SessionNamePattern().Match(name);
        return match.Success && int.TryParse(match.Groups[1].Value, out int n) ? n : 0;
    }

    /// <summary>True when the name is one of ours, so a downloaded file can be recognised.</summary>
    public static bool IsSessionName(string name) => SessionNamePattern().IsMatch(name);

    /// <summary>Sort SESSION1 before SESSION2 and SESSION10 last, rather than alphabetically.</summary>
    public static IReadOnlyList<string> Ordered(IEnumerable<string> names)
    {
        var list = names.ToList();
        list.Sort((a, b) => Number(a).CompareTo(Number(b)));
        return list;
    }

    private static int Number(string name)
    {
        var match = SessionNamePattern().Match(name);
        return match.Success && int.TryParse(match.Groups[1].Value, out int n) ? n : int.MaxValue;
    }
}

/// <summary>
/// Serialises sessions to and from the JSON that lives in browser localStorage. The store holds
/// every session under one namespaced key with a version field, so the format can be migrated
/// rather than discarded when it changes.
/// </summary>
public sealed class SessionStore
{
    /// <summary>The localStorage key. Versioned so an old layout never collides with a new one.</summary>
    public const string StorageKey = "maturinlogo.sessions.v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The wrapper that is actually stored, so version and current session travel together.</summary>
    public sealed class StoreFile
    {
        public int Version { get; set; } = LogoSession.FormatVersion;

        public List<LogoSession> Sessions { get; set; } = new();

        /// <summary>Which session was open, so a refresh returns to the same place.</summary>
        public string CurrentName { get; set; } = "";

        /// <summary>Green or amber phosphor, remembered between visits.</summary>
        public string Theme { get; set; } = "green";

        public bool CrtEmulation { get; set; }

        public string Mode { get; set; } = "EASY";

        /// <summary>
        /// The highest SESSIONn number ever handed out, so a name a child deletes is never issued
        /// again. See SessionNaming.NextName for why counting live sessions is not enough.
        /// </summary>
        public int LastIssuedSessionNumber { get; set; }

        /// <summary>The number the next new session should take; store it back when one is made.</summary>
        public int NextSessionNumberToIssue()
        {
            int highest = LastIssuedSessionNumber;
            foreach (var session in Sessions)
                highest = Math.Max(highest, SessionNaming.NumberOf(session.Name));
            return highest;
        }
    }

    public static string Serialize(StoreFile file) => JsonSerializer.Serialize(file, JsonOptions);

    public static StoreFile? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var file = JsonSerializer.Deserialize<StoreFile>(json, JsonOptions);
            if (file is null) return null;
            // A newer format than this build understands should not be silently mangled.
            if (file.Version > LogoSession.FormatVersion) return null;
            file.Version = LogoSession.FormatVersion;
            return file;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string SerializeSession(LogoSession session) => JsonSerializer.Serialize(session, JsonOptions);

    public static LogoSession? DeserializeSession(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var session = JsonSerializer.Deserialize<LogoSession>(json, JsonOptions);
            return session is not null && session.Version <= LogoSession.FormatVersion ? session : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A brand-new session with a header comment so the file is not empty on disk.</summary>
    public static LogoSession Create(string name) => new()
    {
        Name = name,
        Source = $"; {LogoInfo.Product} - {name}\n; TYPE YOUR LOGO HERE, THEN PRESS ESC TO GO BACK\n",
    };
}
