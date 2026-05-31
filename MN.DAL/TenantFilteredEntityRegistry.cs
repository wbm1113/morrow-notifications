using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Text.RegularExpressions;

namespace MN.DAL;

/// <summary>
/// Discovers entity types that carry an EF global query filter and builds SQL identifier
/// matchers for each mapped table name.  Table names come from the EF model, not hard-coded
/// strings, so new filtered entities are picked up automatically.
/// </summary>
public static class TenantFilteredEntityRegistry
{
    private static IReadOnlyList<FilteredEntityMatch>? _matches;
    private static readonly Lock Sync = new();

    public static IReadOnlyList<FilteredEntityMatch> GetMatches(IModel model)
    {
        if (_matches is not null)
            return _matches;

        lock (Sync)
        {
            return _matches ??= model.GetEntityTypes()
                .Where(e => e.GetDeclaredQueryFilters().Any())
                .Select(e => e.GetTableName())
                .Where(name => name is not null)
                .Select(name => new FilteredEntityMatch(name!, BuildIdentifierPattern(name!)))
                .ToList();
        }
    }

    /// <summary>
    /// Resets cached matchers.  Intended for unit tests only.
    /// </summary>
    public static void ResetCacheForTesting() => _matches = null;

    public static Regex BuildIdentifierPattern(string identifier)
    {
        var escaped = Regex.Escape(identifier);

        // Match a SQL identifier in common provider formats (quoted, bracketed, or bare word).
        // Aliases such as FROM "RoutingRules" AS "r" still contain the quoted table token.
        var pattern =
            $"""(?<![\w\[\]"`])(?:\[{escaped}\]|"{escaped}"|`{escaped}`|\b{escaped}\b)(?![\w\[\]"`])""";

        return new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }

    public static string StripSqlComments(string sql)
    {
        var withoutBlocks = Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"--[^\r\n]*", " ");
    }
}

public readonly record struct FilteredEntityMatch(string TableName, Regex Pattern);
