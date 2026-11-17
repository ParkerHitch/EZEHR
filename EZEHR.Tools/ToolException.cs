namespace EZEHR.Tools;

/// <summary>
/// An expected failure the tools explain in one line, with no stack trace: a missing certificate or setting, an
/// unknown patient, a server that doesn't answer. The command exits 1.
/// </summary>
public sealed class ToolException(string message) : Exception(message);
