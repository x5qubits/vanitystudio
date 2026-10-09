namespace VanityStudio.Infra;

/// <summary>Path resolution for the file and shell tools. The agent is a general-purpose CLI: by default it may read
/// and write anywhere the operator can. With <see cref="Sandbox"/> on (the `--sandbox` switch or `/sandbox on`) every
/// file tool is confined to the workspace, and a path that escapes it is refused.</summary>
public static class VanityPathHelper
{
    /// <summary>Confine the file tools to the workspace.</summary>
    public static bool Sandbox { get; set; }

    /// <summary>Folder name under the agent home where oversized tool outputs are parked for later reads.</summary>
    public const string ScratchDir = "scratch";

    public static string NormalizeAndResolve(string rawPath, string? baseDir = null)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return rawPath;

        var p = rawPath.Trim().Trim('"', '\'');
        // Git-bash / unix drive mapping on Windows (e.g. /c/Users/... or /C/Users/...)
        if (OperatingSystem.IsWindows() && p.Length >= 3 && p[0] == '/' && char.IsLetter(p[1]) && p[2] == '/')
            p = $"{char.ToUpperInvariant(p[1])}:{p[2..]}";
        // ~ expands to the home folder
        if (p == "~" || p.StartsWith("~/") || p.StartsWith("~\\"))
            p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.Length > 1 ? p[2..] : "");

        p = p.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(p)) return Path.GetFullPath(p);

        var root = baseDir ?? Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(root, p));
    }

    /// <summary>Resolves <paramref name="rawPath"/>; in sandbox mode it also verifies the result stays inside
    /// <paramref name="baseDir"/> and throws otherwise.</summary>
    public static string NormalizeAndResolveStrict(string rawPath, string baseDir)
    {
        var full = NormalizeAndResolve(rawPath, baseDir);
        if (!Sandbox) return full;
        if (!IsInside(full, baseDir))
            throw new ArgumentException($"Path '{rawPath}' escapes the workspace (sandbox mode is on; /sandbox off to allow it).");
        return full;
    }

    public static bool IsInside(string fullPath, string baseDir)
    {
        var anchor = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.Equals(anchor, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(anchor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Paths the agent must not touch. Empty by default; the host may deny folders with `--deny`.</summary>
    public static string[] DeniedPaths { get; set; } = [];

    /// <summary>True when the path lies under a denied folder. <paramref name="message"/> says why.</summary>
    public static bool IsDeniedForAgent(string resolvedFullPath, string baseDir, out string message)
    {
        message = "";
        foreach (var d in DeniedPaths)
        {
            var full = NormalizeAndResolve(d, baseDir);
            if (IsInside(resolvedFullPath, full))
            {
                message = $"Error: '{d}' is outside your scope: the operator denied access to it.";
                return true;
            }
        }
        return false;
    }

    /// <summary>Same rule for shell commands: a denied folder mentioned as a path token blocks the command.</summary>
    public static bool CommandTouchesDeniedPath(string command, out string message)
    {
        message = "";
        if (DeniedPaths.Length == 0 || string.IsNullOrWhiteSpace(command)) return false;
        foreach (var d in DeniedPaths)
        {
            var clean = d.Trim().Trim('/', '\\');
            if (clean.Length == 0) continue;
            var pattern = @"(?<![\w.\-])(?:\./)?" + System.Text.RegularExpressions.Regex.Escape(clean) + @"(?=/|\\|\s|$|[""'])";
            if (System.Text.RegularExpressions.Regex.IsMatch(command, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                message = $"Error: '{clean}' is outside your scope: the operator denied access to it.";
                return true;
            }
        }
        return false;
    }
}
