namespace EZEHR.Tools;

/// <summary>Where the tools read and write: .pki/ at the repo root (git-ignored), and the seed data shipped with the tool.</summary>
public static class ToolPaths
{
    public static string PkiDirectory
    {
        get { return Path.Combine(FindRepoRoot(), ".pki"); }
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
