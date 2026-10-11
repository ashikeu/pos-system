using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace RetailFlow.Infrastructure.Platform.Persistence;

/// <summary>
/// Mapping helpers that keep provider-specific decisions out of the entity configurations, so the
/// model stays portable to PostgreSQL and MySQL (ADR-0003).
/// </summary>
internal static class ModelBuilderExtensions
{
    /// <summary>
    /// Marks a text column as unbounded instead of naming a provider type: SQL Server maps this to
    /// <c>nvarchar(max)</c>, PostgreSQL to <c>text</c>, MySQL to <c>longtext</c>.
    /// </summary>
    public static PropertyBuilder IsLargeText(this PropertyBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Metadata.SetMaxLength(null);
        builder.Metadata.SetIsUnicode(true);
        return builder;
    }

    /// <summary>
    /// Configures the optimistic concurrency token. SQL Server uses <c>rowversion</c>, which the
    /// database maintains; on another provider this single method is what changes (ADR-0003).
    /// </summary>
    public static PropertyBuilder UseConcurrencyToken(this PropertyBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.IsRowVersion();
        return builder;
    }
}
