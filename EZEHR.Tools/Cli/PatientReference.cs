using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using EZEHR.Services;

namespace EZEHR.Tools.Cli;

/// <summary>
/// A patient as a tester names one: the EZEHR patient id itself, `sam`, or `provider:chart` (lakeside:LUC-20417).
/// Only the id works without the database.
/// </summary>
[TypeConverter(typeof(PatientReferenceConverter))]
public sealed record PatientReference(
    string Text,
    Guid? PatientId,
    string? ProviderSlug,
    string? Chart,
    bool IsSam
)
{
    public const string Forms =
        "an EZEHR patient id, `sam`, or `<provider>:<chart>` such as `lakeside:LUC-20417`";

    public static readonly PatientReference Sam = new("sam", null, null, null, true);

    public static PatientReference Parse(string text)
    {
        string trimmed = text.Trim();
        if (Guid.TryParse(trimmed, out Guid patientId))
        {
            return new PatientReference(trimmed, patientId, null, null, false);
        }
        if (string.Equals(trimmed, "sam", StringComparison.OrdinalIgnoreCase))
        {
            return Sam;
        }
        int separator = trimmed.IndexOf(':');
        if (separator > 0 && separator < trimmed.Length - 1)
        {
            string slug = trimmed[..separator];
            string chart = trimmed[(separator + 1)..];
            if (Regex.IsMatch(slug, OrgDirectory.SlugPattern))
            {
                return new PatientReference(trimmed, null, slug, chart, false);
            }
        }
        throw new FormatException($"`{text}` isn't a patient: give {Forms}.");
    }

    /// <summary>The EZEHR patient id, looked up in the database unless it was given.</summary>
    public async Task<Guid> ResolveAsync()
    {
        if (PatientId is Guid patientId)
        {
            return patientId;
        }
        if (IsSam)
        {
            return await ToolDatabase.FindSamAsync()
                ?? throw new ToolException(
                    "Sam isn't enrolled yet: run `ezehr-tools provider invite lakeside LUC-20417` and enroll with the link."
                );
        }
        return await ToolDatabase.FindByChartAsync(ProviderSlug!, Chart!)
            ?? throw new ToolException(
                $"No patient is linked to {ProviderSlug} chart {Chart} yet: run `ezehr-tools provider invite {ProviderSlug} {Chart}` and enroll with the link."
            );
    }

    public override string ToString()
    {
        return Text;
    }
}

public sealed class PatientReferenceConverter : TypeConverter
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
        return PatientReference.Parse((string)value);
    }
}
