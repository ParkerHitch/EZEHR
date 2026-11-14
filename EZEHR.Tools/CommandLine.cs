namespace EZEHR.Tools;

public static class CommandLine
{
    /// <summary>The value after "--name", or null when the option is absent.</summary>
    public static string? Option(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length)
        {
            return null;
        }
        return args[index + 1];
    }
}
