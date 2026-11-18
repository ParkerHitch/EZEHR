using System.ComponentModel;
using System.Globalization;
using EZEHR.Models;

namespace EZEHR.Tools.Calls;

/// <summary>
/// What a manual check expects the server to answer: `ok`, an HTTP status (403), or a deny reason (ScopeDenied).
/// </summary>
[TypeConverter(typeof(ExpectedOutcomeConverter))]
public sealed record ExpectedOutcome(bool ExpectsSuccess, int? Status, DenyReason? Reason)
{
    public static ExpectedOutcome Parse(string text)
    {
        string trimmed = text.Trim();
        if (string.Equals(trimmed, "ok", StringComparison.OrdinalIgnoreCase))
        {
            return new ExpectedOutcome(true, null, null);
        }
        if (int.TryParse(trimmed, out int status) && status is >= 100 and < 600)
        {
            return new ExpectedOutcome(false, status, null);
        }
        if (
            !int.TryParse(trimmed, out _)
            && Enum.TryParse(trimmed, ignoreCase: true, out DenyReason reason)
        )
        {
            return new ExpectedOutcome(false, null, reason);
        }
        throw new FormatException(
            $"`{text}` isn't an outcome: give ok, an HTTP status such as 403, or one of {string.Join(", ", Enum.GetNames<DenyReason>())}."
        );
    }

    public bool Matches(ReadOutcome outcome)
    {
        if (ExpectsSuccess)
        {
            return outcome.Succeeded;
        }
        if (Status is int status)
        {
            return (int)outcome.Status == status;
        }
        return outcome.DenyReason == Reason.ToString();
    }

    public override string ToString()
    {
        if (ExpectsSuccess)
        {
            return "ok";
        }
        return Status?.ToString() ?? Reason.ToString()!;
    }
}

public sealed class ExpectedOutcomeConverter : TypeConverter
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
        return ExpectedOutcome.Parse((string)value);
    }
}
