using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using EZEHR.Models;
using EZEHR.Services;

namespace EZEHR.Tools.Cli;

/// <summary>
/// Whose certificate a call presents: `none`, `broker` (self-signed), `platform`, any organization the tools know
/// (.pki/{slug}.pfx), or a path to a .pfx.
/// </summary>
[TypeConverter(typeof(IdentityReferenceConverter))]
public sealed record IdentityReference(string Text, string? PfxPath)
{
    public const string None = "none";

    public const string Broker = "broker";

    public const string Platform = "platform";

    public static IdentityReference Parse(string text)
    {
        string trimmed = text.Trim();
        if (trimmed == None)
        {
            return new IdentityReference(trimmed, null);
        }
        if (trimmed.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase))
        {
            return new IdentityReference(trimmed, Path.GetFullPath(trimmed));
        }
        List<string> known = [Broker, Platform, .. KnownSlugs()];
        // An organization issued a certificate here but never admitted (`certs provider harbor`) is an identity too:
        // calling as it is how to check that the network refuses it.
        bool issuedHere =
            Regex.IsMatch(trimmed, OrgDirectory.SlugPattern)
            && File.Exists(ToolPaths.Pki($"{trimmed}.pfx"));
        if (known.Contains(trimmed) || issuedHere)
        {
            return new IdentityReference(trimmed, ToolPaths.Pki($"{trimmed}.pfx"));
        }
        throw new FormatException(
            $"`{text}` isn't an identity: give {None}, {string.Join(", ", known)}, or a path to a .pfx."
        );
    }

    /// <summary>"none", or "lakeside.providers.ezehr, issued by EZEHR Issuing CA".</summary>
    public string Describe()
    {
        return PfxPath is null ? None : PfxFile.Describe(PfxPath);
    }

    /// <summary>The certificate's path, or which command makes it when it's missing.</summary>
    public string RequirePfx()
    {
        if (PfxPath is null)
        {
            throw new ToolException("`none` presents no certificate.");
        }
        if (File.Exists(PfxPath))
        {
            return PfxPath;
        }
        if (Text.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException($"{PfxPath} not found.");
        }
        throw new ToolException($"{PfxPath} not found. Run `ezehr-tools {CertsCommand()}` first.");
    }

    private string CertsCommand()
    {
        if (Text == Broker)
        {
            return "certs broker";
        }
        if (Text == Platform)
        {
            return "certs platform";
        }
        Organization? organization = ToolOrganizations
            .All()
            .FirstOrDefault(candidate => ToolOrganizations.Slug(candidate) == Text);
        if (organization is null)
        {
            return "certs issue";
        }
        if (OrgDirectory.DemoOrganizations.Contains(organization))
        {
            return "certs issue";
        }
        return organization.Kind == PartyKind.App ? $"certs app {Text}" : $"certs provider {Text}";
    }

    private static IEnumerable<string> KnownSlugs()
    {
        return ToolOrganizations.All().Select(ToolOrganizations.Slug);
    }

    public override string ToString()
    {
        return Text;
    }
}

public sealed class IdentityReferenceConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
    {
        return sourceType == typeof(string);
    }

    public override object ConvertFrom(
        ITypeDescriptorContext? context,
        CultureInfo? culture,
        object value
    )
    {
        return IdentityReference.Parse((string)value);
    }
}
