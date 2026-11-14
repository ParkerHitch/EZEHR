// ezehr-tools: laptop-side commands (certs, provider, kidney-check, broker, demo reset).
// Commands are added by Tasks 3, 9, 10 and 13.
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: ezehr-tools <certs|provider|kidney-check|broker|demo> ...");
    return 1;
}
Console.Error.WriteLine($"unknown command: {args[0]}");
return 1;
