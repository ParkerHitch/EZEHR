using System.Text.RegularExpressions;
using EZEHR.Models;
using EZEHR.Services;
using Spectre.Console;

namespace EZEHR.Tools.Cli;

/// <summary>The checks shared by every command that names a new organization.</summary>
public static class OrganizationValidation
{
    public static ValidationResult Slug(string slug)
    {
        return Regex.IsMatch(slug, OrgDirectory.SlugPattern)
            ? ValidationResult.Success()
            : ValidationResult.Error(
                $"`{slug}` isn't a slug: use lowercase letters, digits and hyphens, such as harbor"
            );
    }

    public static ValidationResult Name(string? displayName)
    {
        return string.IsNullOrWhiteSpace(displayName)
            ? ValidationResult.Error(
                "--name takes the display name patients see, such as \"Harbor Clinic\""
            )
            : ValidationResult.Success();
    }

    public static ValidationResult AppScope(GrantScope? scope, string? description)
    {
        if (scope is null or GrantScope.Full)
        {
            return ValidationResult.Error(
                $"--scope takes {GrantScope.LabsAndNotes} or {GrantScope.MedicationsLabsAndNotes}: an app never gets the full record"
            );
        }
        return string.IsNullOrWhiteSpace(description)
            ? ValidationResult.Error(
                "--description takes the sentence patients read before turning the app on"
            )
            : ValidationResult.Success();
    }

    /// <summary>The first failure, or success.</summary>
    public static ValidationResult All(params ValidationResult[] results)
    {
        return results.FirstOrDefault(result => !result.Successful) ?? ValidationResult.Success();
    }
}
