namespace AShareRadar.Infrastructure.Runtime;

/// <summary>
/// Resolves configured executables without treating a bare command name as a
/// file relative to the application directory.
/// </summary>
public static class ExecutablePathResolver
{
    public static string Resolve(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return string.Empty;
        }

        var value = configuredPath.Trim().Trim('"');
        if (Path.IsPathFullyQualified(value) || HasDirectoryPart(value))
        {
            return Path.IsPathFullyQualified(value)
                ? value
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, value));
        }

        return FindOnPath(value) ?? value;
    }

    public static bool Exists(string resolvedPath)
    {
        return !string.IsNullOrWhiteSpace(resolvedPath) && File.Exists(resolvedPath);
    }

    private static string? FindOnPath(string executable)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        var candidates = new List<string> { executable };
        if (!Path.HasExtension(executable))
        {
            var pathExt = Environment.GetEnvironmentVariable("PATHEXT");
            if (!string.IsNullOrWhiteSpace(pathExt))
            {
                candidates.AddRange(pathExt
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(extension => executable + extension));
            }
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidate in candidates)
            {
                var fullPath = Path.Combine(directory.Trim('"'), candidate);
                if (File.Exists(fullPath))
                {
                    return Path.GetFullPath(fullPath);
                }
            }
        }

        return null;
    }

    private static bool HasDirectoryPart(string value)
    {
        return value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar)
            || value.Contains(':');
    }
}
