using System.Globalization;
using System.Reflection;

namespace CueVideo;

/// <summary>Which build of the app is running: the version, the git commit it was built from
/// (the SDK adds it to the informational version as "+hash"), and when it was built (stamped
/// by ChurchfrontVideo.csproj as the BuildDate assembly metadata).</summary>
public static class AppInfo
{
    private static Assembly? App => Assembly.GetEntryAssembly();

    private static string Informational =>
        App?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? App?.GetName().Version?.ToString() ?? "";

    /// <summary>"0.1.2".</summary>
    public static string Version => Informational.Split('+')[0];

    /// <summary>The commit, shortened to 7 characters ("9e45c9d"), or "" if not known.</summary>
    public static string Commit
    {
        get
        {
            var i = Informational.IndexOf('+');
            if (i < 0) return "";
            var c = Informational[(i + 1)..];
            return c.Length > 7 ? c[..7] : c;
        }
    }

    /// <summary>When this build was made, in local time, or null if not stamped.</summary>
    public static DateTime? Built
    {
        get
        {
            var v = App?.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildDate")?.Value;
            return DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime() : null;
        }
    }

    /// <summary>"Version 0.1.2 (build 9e45c9d), built Tuesday, November 24, 2026 at 4:40 PM",
    /// with the date written the way the language in use writes it.</summary>
    public static string Text =>
        $"Version {Version}{(Commit.Length > 0 ? $" (build {Commit})" : "")}" +
        (Built is DateTime b ? $", built {When(b)}" : "");

    private static string When(DateTime d)
    {
        if (L.IsEnglish) return d.ToString("dddd, MMMM d, yyyy 'at' h:mm tt", CultureInfo.CurrentCulture);
        try
        {
            var c = CultureInfo.GetCultureInfo(L.Code);
            return d.ToString("D", c) + ", " + d.ToString("t", c);
        }
        catch (CultureNotFoundException) { return d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }
    }
}
