namespace EZEHR.Tools;

/// <summary>
/// Where the tools read and write: .pki/ at the repo root (git-ignored), or EZEHR_PKI_DIRECTORY when set, and the
/// seed data shipped with the tool.
/// </summary>
public static class ToolPaths
{
    public const string PkiDirectoryVariable = "EZEHR_PKI_DIRECTORY";

    public static string PkiDirectory
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable(PkiDirectoryVariable);
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(FindRepoRoot(), ".pki")
                // Absolute now: the trust-file loader resolves relative paths against bin/, not here.
                : Path.GetFullPath(configured);
        }
    }

    public static string SeedDirectory
    {
        get { return Path.Combine(AppContext.BaseDirectory, "seed"); }
    }

    public static string Pki(string fileName)
    {
        return Path.Combine(PkiDirectory, fileName);
    }

    /// <summary>The directory holding EZEHR.slnx, searched upward from the working directory; else the working directory.</summary>
    private static string FindRepoRoot()
    {
        string start = Directory.GetCurrentDirectory();
        for (
            DirectoryInfo? directory = new(start);
            directory is not null;
            directory = directory.Parent
        )
        {
            if (File.Exists(Path.Combine(directory.FullName, "EZEHR.slnx")))
            {
                return directory.FullName;
            }
        }
        return start;
    }
}
