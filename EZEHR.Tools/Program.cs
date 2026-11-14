using EZEHR.Tools.Commands;

// ezehr-tools: laptop-side commands (certs, provider, kidney-check, broker, demo reset).
const string Usage = "usage: ezehr-tools <certs|provider|kidney-check|broker|demo> ...";

if (args.Length == 0)
{
    Console.Error.WriteLine(Usage);
    Console.Error.WriteLine(CertsCommand.Usage);
    Console.Error.WriteLine(ProviderCommand.Usage);
    return 1;
}

switch (args[0])
{
    case "certs":
        return await CertsCommand.RunAsync(args[1..]);
    case "provider":
        return await ProviderCommand.RunAsync(args[1..]);
    default:
        Console.Error.WriteLine($"unknown command: {args[0]}");
        Console.Error.WriteLine(Usage);
        return 1;
}
