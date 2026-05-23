// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq;
using System.Text.RegularExpressions;

namespace Microsoft.AspNetCore.Grpc.Swagger;

/// <summary>
/// Represents a single path override registered with a
/// <see cref="GrpcSwaggerPathOverrideBuilder"/>. Returned from each
/// <c>Add(...)</c> call to allow further configuration such as parameter renames.
/// </summary>
public sealed partial class GrpcSwaggerPathOverride
{
    private readonly List<(string From, string To)> _renames = new();

    internal GrpcSwaggerPathOverride(string? path)
    {
        Path = path;
    }

    internal string? Path { get; }

    internal IReadOnlyList<(string From, string To)> Renames => _renames;

    /// <summary>
    /// Rename a path parameter token. After the override's path has been determined,
    /// every occurrence of <c>{<paramref name="from"/>}</c> in the path is rewritten to
    /// <c>{<paramref name="to"/>}</c>. The renamed parameter retains the schema and
    /// documentation metadata of the original.
    /// </summary>
    /// <param name="from">The current parameter name (the token inside <c>{ }</c>).</param>
    /// <param name="to">The new parameter name to use in the rendered OpenAPI path.</param>
    /// <returns>This <see cref="GrpcSwaggerPathOverride"/> for fluent chaining.</returns>
    public GrpcSwaggerPathOverride RenameParameter(string from, string to)
    {
        ArgumentException.ThrowIfNullOrEmpty(from);
        ArgumentException.ThrowIfNullOrEmpty(to);

        _renames.Add((from, to));
        return this;
    }

    /// <summary>
    /// Produce the <see cref="GrpcSwaggerPathResolution"/> for this rule applied to the given context.
    /// </summary>
    internal GrpcSwaggerPathResolution Apply(GrpcSwaggerPathContext context)
    {
        // 1. Compute the path: explicit path wins; otherwise substitute renames into the default path
        //    in a single regex pass so that sequential renames cannot cascade unexpectedly.
        var path = Path ?? ApplyRenamesToPath(context.DefaultPath);

        // 2. If no renames were declared, return a path-only resolution. The provider's
        //    AutoExtractParameters ladder handles parameter derivation uniformly.
        if (_renames.Count == 0)
        {
            return new GrpcSwaggerPathResolution(path);
        }

        // 3. Build a parameter list. Renamed tokens preserve the source field's metadata;
        //    unrenamed tokens fall through to the default parameter list lookup. If any token
        //    can't be resolved, return path-only so the provider's auto-extraction handles it.
        if (TryBuildParameters(path, context.DefaultParameters, out var parameters))
        {
            return new GrpcSwaggerPathResolution(path, parameters);
        }

        return new GrpcSwaggerPathResolution(path);
    }

    private string ApplyRenamesToPath(string defaultPath)
    {
        if (_renames.Count == 0)
        {
            return defaultPath;
        }

        // Map each "from" token to its "to". Later RenameParameter entries with the same "from"
        // overwrite earlier ones (last-write-wins for a given source name).
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (from, to) in _renames)
        {
            map[from] = to;
        }

        // Single-pass regex over {token}. The match-evaluator picks the rename target if the
        // token is mapped; otherwise the original token is preserved. This avoids the cascade
        // bug where renaming "name" -> "name1" then "name1" -> "childId" would chain.
        return PathTokenRegex().Replace(defaultPath, match =>
        {
            var token = match.Groups[1].Value;
            return map.TryGetValue(token, out var replacement)
                ? "{" + replacement + "}"
                : match.Value;
        });
    }

    private bool TryBuildParameters(
        string path,
        IReadOnlyList<GrpcSwaggerPathParameter> defaultParameters,
        out List<GrpcSwaggerPathParameter> parameters)
    {
        // Reverse-lookup table: rendered token name -> the source proto parameter name.
        // For a rename ("name" -> "thingId"), seeing {thingId} in the path means "look up
        // 'name' in defaultParameters to get the field".
        var reverseMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (from, to) in _renames)
        {
            reverseMap[to] = from;
        }

        var defaultByName = defaultParameters.ToDictionary(p => p.Name, p => p.Field, StringComparer.Ordinal);

        var result = new List<GrpcSwaggerPathParameter>();
        foreach (Match match in PathTokenRegex().Matches(path))
        {
            var token = match.Groups[1].Value;

            // (i) Rename target -> source field via the reverse-map
            if (reverseMap.TryGetValue(token, out var sourceName)
                && defaultByName.TryGetValue(sourceName, out var renamedField))
            {
                result.Add(new GrpcSwaggerPathParameter { Name = token, Field = renamedField });
                continue;
            }

            // (ii) Unrenamed token already in the default parameter list
            if (defaultByName.TryGetValue(token, out var defaultField))
            {
                result.Add(new GrpcSwaggerPathParameter { Name = token, Field = defaultField });
                continue;
            }

            // (iii) Unresolvable: bail out and let the provider's auto-extraction handle the
            // whole path uniformly via the path-only resolution form.
            parameters = null!;
            return false;
        }

        parameters = result;
        return true;
    }

    [GeneratedRegex(@"\{([^}]+)\}")]
    private static partial Regex PathTokenRegex();
}
