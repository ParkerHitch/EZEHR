using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace EZEHR.Tools.Cli;

/// <summary>Which CA issues, and with what key: the options every command that issues a certificate takes.</summary>
public class CertificateAuthoritySettings : NetworkSettings
{
    [CommandOption("--ca <authority>")]
    [Description("local (a test CA on this laptop) or ezca (portal.ezca.io, as az login)")]
    [DefaultValue("local")]
    public string Authority { get; init; } = "local";

    [CommandOption("--key <type>")]
    [Description("The new key: ec (P-256) or rsa (2048)")]
    [DefaultValue("ec")]
    public string KeyType { get; init; } = "ec";

    [CommandOption("--ca-id <id>")]
    [Description("With --ca ezca: which CA, when the account sees more than one")]
    public string? CaId { get; init; }

    [CommandOption("--template-id <id>")]
    [Description("With --ca ezca: which template, when the CA has more than one")]
    public string? TemplateId { get; init; }

    public override ValidationResult Validate()
    {
        if (Authority is not ("local" or "ezca"))
        {
            return ValidationResult.Error("--ca takes local or ezca");
        }
        if (KeyType is not ("ec" or "rsa"))
        {
            return ValidationResult.Error("--key takes ec or rsa");
        }
        return ValidationResult.Success();
    }
}
