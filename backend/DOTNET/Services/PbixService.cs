// using System;
// using System.Collections.Generic;
// using System.IO;
// using System.IO.Compression;
// using System.Linq;
// using System.Text;
// using System.Threading.Tasks;
// using Microsoft.Extensions.Logging;
// using Newtonsoft.Json;
// using Newtonsoft.Json.Linq;
// using PbixMigrator.Models;

// namespace PbixMigrator.Services
// {
//     public class PbixService
//     {
//         private readonly ILogger<PbixService> _log;
//         public PbixService(ILogger<PbixService> log) => _log = log;


//         // ── Extract Report/Layout JSON from .pbix archive ─────────────────────
// public async Task<JObject> ExtractLayoutAsync(byte[] pbixBytes)
// {
//     await Task.CompletedTask;
//     using var ms = new MemoryStream(pbixBytes);
//     using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

//     // 1) Old format
//     var legacy = zip.GetEntry("Report/Layout");
//     if (legacy != null)
//     {
//         using var sr = new StreamReader(legacy.Open(), Encoding.Unicode);
//         var raw = sr.ReadToEnd();
//         _log.LogInformation("ExtractLayoutAsync: using legacy Report/Layout");
//         return JObject.Parse(raw);
//     }

//     // 2) New format: read visual.json files and build synthetic layout
//     _log.LogInformation("ExtractLayoutAsync: Report/Layout not found — scanning for visual.json (new format)");

//     var sections = new JArray();
//     var pageMap = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

//     foreach (var entry in zip.Entries)
//     {
//         var name = entry.FullName.Replace('\\', '/');
//         if (!name.EndsWith("/visual.json", StringComparison.OrdinalIgnoreCase) &&
//             !name.EndsWith("visual.json", StringComparison.OrdinalIgnoreCase))
//             continue;

//         string json;
//         using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
//             json = reader.ReadToEnd();

//         JObject visualObj;
//         try { visualObj = JObject.Parse(json); }
//         catch { continue; }

//         if (visualObj["visual"] == null && visualObj["visualType"] == null)
//             continue;

//         _log.LogInformation("FOUND VISUAL: {EntryName}", name);
//         _log.LogInformation("VISUAL CONTENT: {Content}", json);

//         var pageName = "Page 1";
//         var parts = name.Split('/');
//         for (int i = 0; i < parts.Length; i++)
//         {
//             if (parts[i].Equals("pages", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
//             {
//                 pageName = parts[i + 1];
//                 break;
//             }
//         }

//         if (!pageMap.TryGetValue(pageName, out var section))
//         {
//             section = new JObject
//             {
//                 ["displayName"] = pageName,
//                 ["name"] = pageName,
//                 ["visualContainers"] = new JArray()
//             };
//             pageMap[pageName] = section;
//             sections.Add(section);
//         }

//         var containers = (JArray)section["visualContainers"]!;
//         containers.Add(new JObject
//         {
//             ["name"] = visualObj["name"]?.ToString() ?? Guid.NewGuid().ToString("N"),
//             ["config"] = visualObj.ToString(Formatting.None)
//         });
//     }

//     if (sections.Count == 0)
//         throw new InvalidDataException("Report/Layout not found in PBIX and no visual.json files were found");

//     _log.LogInformation("ExtractLayoutAsync: built synthetic layout from {Count} page(s)", sections.Count);
//     return new JObject { ["sections"] = sections };
// }

//         // ── Extract Report/Layout JSON from .pbix archive ─────────────────────
//         public async Task<JObject> ExtractLayoutAsync(byte[] pbixBytes)
//         {
//             await Task.CompletedTask;
//             using var ms  = new MemoryStream(pbixBytes);
//             using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

//             _log.LogInformation("===== NEW VISUAL JSON TEST IS RUNNING =====");

//              // TEMPORARY: inspect one visual.json
//     int count = 0;

// foreach (var e in zip.Entries)
// {
//     if (e.FullName.EndsWith("/visual.json", StringComparison.OrdinalIgnoreCase))
//     {
//         _log.LogInformation("FOUND VISUAL: {EntryName}", e.FullName);

//         using var reader = new StreamReader(e.Open());
//         var content = reader.ReadToEnd();

//         _log.LogInformation("VISUAL CONTENT: {Content}", content);

//         count++;

//         if (count >= 3)
//             break;
//     }
// }
//             var entry = zip.GetEntry("Report/Layout")
//                         ?? throw new InvalidDataException("Report/Layout not found in PBIX");

//             using var sr = new StreamReader(entry.Open(), Encoding.Unicode);
//             var raw = sr.ReadToEnd();
//             return JObject.Parse(raw);
//         }

        // ─────────────────────────────────────────────────────────────────────
        // Extract FULL VISUAL METADATA from the Report/Layout JSON.
        //
        // For each visual on each page, we extract:
        //   - Page name
        //   - Visual internal name (GUID) and display title
        //   - Visual type (barChart, card, slicer, table, matrix, …)
        //   - Every column the visual is currently bound to, with its data role
        //
        // Power BI stores field references in the visual config JSON as:
        //   { "Entity": "TableName", "Property": "ColumnName" }
        // inside data role projections, e.g.:
        //   config.singleVisual.projections.Values[].queryRef → maps to Entity/Property
        //   config.singleVisual.prototypeQuery.Select[].Column.Entity/Property
        //
        // We use both paths: prototypeQuery (richer role info) + projections
        // (catches any that the prototype misses).
        // ─────────────────────────────────────────────────────────────────────
        // public List<VisualMetadata> ExtractVisualMetadata(JObject layout)
        // {
        //     var results  = new List<VisualMetadata>();
        //     var sections = layout["sections"] as JArray ?? new JArray();

        //     foreach (var section in sections)
        //     {
        //         var pageName   = section["displayName"]?.ToString() ?? section["name"]?.ToString() ?? "";
        //         var containers = section["visualContainers"] as JArray ?? new JArray();

        //         foreach (var vc in containers)
        //         {
        //             var configRaw = vc["config"]?.ToString();
        //             if (string.IsNullOrEmpty(configRaw)) continue;

        //             JObject cfg;
        //             try   { cfg = JObject.Parse(configRaw); }
        //             catch { continue; }

        //             var sv          = cfg["singleVisual"];
        //             if (sv == null) continue;

        //             var visualName  = vc["name"]?.ToString()             ?? "";
        //             var visualType  = sv["visualType"]?.ToString()        ?? "";
        //             var vcfg        = sv["vcObjects"] ?? sv["objects"];
        //             var titleVal    = vcfg?["title"]?.FirstOrDefault()?["properties"]?["text"]?["expr"]?["Literal"]?["Value"]?.ToString();
        //             var visualTitle = titleVal?.Trim('\'') ?? visualType;

        //             var boundColumns = new List<VisualColumnBinding>();

        //             // Path 1: prototypeQuery → Select[] gives Entity/Property + role hint
        //             var protoQuery = sv["prototypeQuery"];
        //             if (protoQuery != null)
        //             {
        //                 var selects = protoQuery["Select"] as JArray ?? new JArray();
        //                 foreach (var sel in selects)
        //                 {
        //                     var colNode = sel["Column"] ?? sel["Measure"] ?? sel["Aggregation"]?["Expression"]?["Column"];
        //                     if (colNode == null) continue;

        //                     var entity   = colNode["Expression"]?["SourceRef"]?["Entity"]?.ToString()
        //                                 ?? colNode["Entity"]?.ToString() ?? "";
        //                     var property = colNode["Property"]?.ToString() ?? "";
        //                     if (string.IsNullOrEmpty(entity) || string.IsNullOrEmpty(property)) continue;

        //                     var role = InferRoleFromSelect(sel, visualType);
        //                     boundColumns.Add(new VisualColumnBinding
        //                     {
        //                         Table    = entity,
        //                         Column   = property,
        //                         DataRole = role
        //                     });
        //                 }
        //             }

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PbixMigrator.Models;
 
namespace PbixMigrator.Services
{
    public class PbixService
    {
        private readonly ILogger<PbixService> _log;
        public PbixService(ILogger<PbixService> log) => _log = log;
 
 
        // ── Extract Report/Layout JSON from .pbix archive ─────────────────────
public async Task<JObject> ExtractLayoutAsync(byte[] pbixBytes)
{
    await Task.CompletedTask;
    using var ms = new MemoryStream(pbixBytes);
    using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
 
    // 1) Old format
    var legacy = zip.GetEntry("Report/Layout");
    if (legacy != null)
    {
        using var sr = new StreamReader(legacy.Open(), Encoding.Unicode);
        var raw = sr.ReadToEnd();
        _log.LogInformation("ExtractLayoutAsync: using legacy Report/Layout");
        return JObject.Parse(raw);
    }
 
    // 2) New format: read visual.json files and build synthetic layout
    _log.LogInformation("ExtractLayoutAsync: Report/Layout not found — scanning for visual.json (new format)");
 
    var sections = new JArray();
    var pageMap = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
 
    foreach (var entry in zip.Entries)
    {
        var name = entry.FullName.Replace('\\', '/');
        if (!name.EndsWith("/visual.json", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith("visual.json", StringComparison.OrdinalIgnoreCase))
            continue;
 
        string json;
        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
            json = reader.ReadToEnd();
 
        JObject visualObj;
        try { visualObj = JObject.Parse(json); }
        catch { continue; }
 
        if (visualObj["visual"] == null && visualObj["visualType"] == null)
            continue;
 
        _log.LogInformation("FOUND VISUAL: {EntryName}", name);
        _log.LogInformation("VISUAL CONTENT: {Content}", json);
 
        var pageName = "Page 1";
        var parts = name.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Equals("pages", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
            {
                pageName = parts[i + 1];
                break;
            }
        }
 
        if (!pageMap.TryGetValue(pageName, out var section))
        {
            section = new JObject
            {
                ["displayName"] = pageName,
                ["name"] = pageName,
                ["visualContainers"] = new JArray()
            };
            pageMap[pageName] = section;
            sections.Add(section);
        }
 
        var containers = (JArray)section["visualContainers"]!;
        containers.Add(new JObject
        {
            ["name"] = visualObj["name"]?.ToString() ?? Guid.NewGuid().ToString("N"),
            ["config"] = visualObj.ToString(Formatting.None)
        });
    }
 
    if (sections.Count == 0)
        throw new InvalidDataException("Report/Layout not found in PBIX and no visual.json files were found");
 
    _log.LogInformation("ExtractLayoutAsync: built synthetic layout from {Count} page(s)", sections.Count);
    return new JObject { ["sections"] = sections };
}
 
//         // ── Extract Report/Layout JSON from .pbix archive ─────────────────────
//         public async Task<JObject> ExtractLayoutAsync(byte[] pbixBytes)
//         {
//             await Task.CompletedTask;
//             using var ms  = new MemoryStream(pbixBytes);
//             using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
 
//             _log.LogInformation("===== NEW VISUAL JSON TEST IS RUNNING =====");
 
//              // TEMPORARY: inspect one visual.json
//     int count = 0;
 
// foreach (var e in zip.Entries)
// {
//     if (e.FullName.EndsWith("/visual.json", StringComparison.OrdinalIgnoreCase))
//     {
//         _log.LogInformation("FOUND VISUAL: {EntryName}", e.FullName);
 
//         using var reader = new StreamReader(e.Open());
//         var content = reader.ReadToEnd();
 
//         _log.LogInformation("VISUAL CONTENT: {Content}", content);
 
//         count++;
 
//         if (count >= 3)
//             break;
//     }
// }
//             var entry = zip.GetEntry("Report/Layout")
//                         ?? throw new InvalidDataException("Report/Layout not found in PBIX");
 
//             using var sr = new StreamReader(entry.Open(), Encoding.Unicode);
//             var raw = sr.ReadToEnd();
//             return JObject.Parse(raw);
//         }
 
        // ─────────────────────────────────────────────────────────────────────
        // Extract FULL VISUAL METADATA from the Report/Layout JSON.
        //
        // For each visual on each page, we extract:
        //   - Page name
        //   - Visual internal name (GUID) and display title
        //   - Visual type (barChart, card, slicer, table, matrix, …)
        //   - Every column the visual is currently bound to, with its data role
        //
        // Power BI stores field references in the visual config JSON as:
        //   { "Entity": "TableName", "Property": "ColumnName" }
        // inside data role projections, e.g.:
        //   config.singleVisual.projections.Values[].queryRef → maps to Entity/Property
        //   config.singleVisual.prototypeQuery.Select[].Column.Entity/Property
        //
        // We use both paths: prototypeQuery (richer role info) + projections
        // (catches any that the prototype misses).
        // ─────────────────────────────────────────────────────────────────────
        // public List<VisualMetadata> ExtractVisualMetadata(JObject layout)
        // {
        //     var results  = new List<VisualMetadata>();
        //     var sections = layout["sections"] as JArray ?? new JArray();
 
        //     foreach (var section in sections)
        //     {
        //         var pageName   = section["displayName"]?.ToString() ?? section["name"]?.ToString() ?? "";
        //         var containers = section["visualContainers"] as JArray ?? new JArray();
 
        //         foreach (var vc in containers)
        //         {
        //             var configRaw = vc["config"]?.ToString();
        //             if (string.IsNullOrEmpty(configRaw)) continue;
 
        //             JObject cfg;
        //             try   { cfg = JObject.Parse(configRaw); }
        //             catch { continue; }
 
        //             var sv          = cfg["singleVisual"];
        //             if (sv == null) continue;
 
        //             var visualName  = vc["name"]?.ToString()             ?? "";
        //             var visualType  = sv["visualType"]?.ToString()        ?? "";
        //             var vcfg        = sv["vcObjects"] ?? sv["objects"];
        //             var titleVal    = vcfg?["title"]?.FirstOrDefault()?["properties"]?["text"]?["expr"]?["Literal"]?["Value"]?.ToString();
        //             var visualTitle = titleVal?.Trim('\'') ?? visualType;
 
        //             var boundColumns = new List<VisualColumnBinding>();
 
        //             // Path 1: prototypeQuery → Select[] gives Entity/Property + role hint
        //             var protoQuery = sv["prototypeQuery"];
        //             if (protoQuery != null)
        //             {
        //                 var selects = protoQuery["Select"] as JArray ?? new JArray();
        //                 foreach (var sel in selects)
        //                 {
        //                     var colNode = sel["Column"] ?? sel["Measure"] ?? sel["Aggregation"]?["Expression"]?["Column"];
        //                     if (colNode == null) continue;
 
        //                     var entity   = colNode["Expression"]?["SourceRef"]?["Entity"]?.ToString()
        //                                 ?? colNode["Entity"]?.ToString() ?? "";
        //                     var property = colNode["Property"]?.ToString() ?? "";
        //                     if (string.IsNullOrEmpty(entity) || string.IsNullOrEmpty(property)) continue;
 
        //                     var role = InferRoleFromSelect(sel, visualType);
        //                     boundColumns.Add(new VisualColumnBinding
        //                     {
        //                         Table    = entity,
        //                         Column   = property,
        //                         DataRole = role
        //                     });
        //                 }
        //             }
 
       public List<VisualMetadata> ExtractVisualMetadata(JObject layout)
{
    var results = new List<VisualMetadata>();
    var sections = layout["sections"] as JArray ?? new JArray();
 
    foreach (var section in sections)
    {
        var pageName = section["displayName"]?.ToString()
                    ?? section["name"]?.ToString()
                    ?? "";
        var containers = section["visualContainers"] as JArray ?? new JArray();
 
        foreach (var vc in containers)
        {
            var configRaw = vc["config"]?.ToString();
            if (string.IsNullOrEmpty(configRaw)) continue;
 
            JObject cfg;
            try { cfg = JObject.Parse(configRaw); }
            catch { continue; }
 
            // Old format: singleVisual | New format: visual
            var sv = cfg["singleVisual"] as JObject
                  ?? cfg["visual"] as JObject
                  ?? cfg;
 
            var visualName = vc["name"]?.ToString()
                          ?? cfg["name"]?.ToString()
                          ?? "";
            var visualType = sv["visualType"]?.ToString() ?? "";
 
            var vcfg = sv["vcObjects"] ?? sv["objects"];
            var titleVal = vcfg?["title"]?.FirstOrDefault()?["properties"]?["text"]?["expr"]?["Literal"]?["Value"]?.ToString();
            var visualTitle = titleVal?.Trim('\'') ?? visualType;
 
            var boundColumns = new List<VisualColumnBinding>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Path 1 can only GUESS a role ("Values"); remember those bindings so a
            // later projection pass can correct the role instead of adding a duplicate.
            var guessedRole = new HashSet<VisualColumnBinding>();
 
            // Adds one binding, or — when the same field was already captured by
            // Path 1 with a guessed role — enriches that binding (role / aggregation /
            // measure flag) instead of creating a duplicate field.
            void AddBinding(string entity, string property, string role,
                            string agg, bool isMeasure, bool roleIsGuess)
            {
                if (string.IsNullOrEmpty(entity) || string.IsNullOrEmpty(property)) return;
 
                if (!roleIsGuess)
                {
                    var existing = boundColumns.FirstOrDefault(b =>
                        guessedRole.Contains(b) &&
                        b.Table.Equals(entity, StringComparison.OrdinalIgnoreCase) &&
                        b.Column.Equals(property, StringComparison.OrdinalIgnoreCase) &&
                        (b.Aggregation.Length == 0 || agg.Length == 0 ||
                         b.Aggregation.Equals(agg, StringComparison.OrdinalIgnoreCase)));
                    if (existing != null)
                    {
                        existing.DataRole = role;
                        if (existing.Aggregation.Length == 0) existing.Aggregation = agg;
                        existing.IsMeasure |= isMeasure;
                        guessedRole.Remove(existing);
                        seen.Add(entity + "." + property + "." + role + "." + existing.Aggregation);
                        return;
                    }
                }
 
                var newKey = entity + "." + property + "." + role + "." + agg;
                if (!seen.Add(newKey)) return;
 
                var nb = new VisualColumnBinding
                {
                    Table = entity,
                    Column = property,
                    DataRole = role,
                    Aggregation = agg,
                    IsMeasure = isMeasure
                };
                boundColumns.Add(nb);
                if (roleIsGuess) guessedRole.Add(nb);
            }
 
            _log.LogInformation(
    "VISUAL DEBUG: {VisualName} | Type={VisualType} | configLength={ConfigLength}",
    visualName,
    visualType,
    configRaw?.Length ?? 0
);
 
_log.LogInformation(
    "VISUAL DEBUG RAW CONFIG: {VisualName} => {Config}",
    visualName,
    configRaw
);
 
            // Path 1: prototypeQuery.Select
            // (distinguishes Measure vs Column vs Aggregation{Function} for each select)
            var protoQuery = sv["prototypeQuery"];
            if (protoQuery != null)
            {
                var selects = protoQuery["Select"] as JArray ?? new JArray();
                foreach (var sel in selects)
                {
                    var aggNode = sel["Aggregation"];
                    var colNode = sel["Column"]
                               ?? sel["Measure"]
                               ?? aggNode?["Expression"]?["Column"];
                    if (colNode == null) continue;
 
                    var entity = colNode["Expression"]?["SourceRef"]?["Entity"]?.ToString()
                              ?? colNode["Entity"]?.ToString()
                              ?? "";
                    var property = colNode["Property"]?.ToString() ?? "";
 
                    var hasExplicitRole = !string.IsNullOrEmpty(sel["Role"]?.ToString());
                    AddBinding(
                        entity,
                        property,
                        InferRoleFromSelect(sel, visualType),
                        AggFromFunction(aggNode?["Function"]),
                        sel["Measure"] != null,
                        !hasExplicitRole);
                }
            }
 
            // Path 2: projections
            var projections = sv["projections"] as JObject;
            if (projections != null)
            {
                foreach (var proj in projections.Properties())
                {
                    var roleName = proj.Name;
                    var items = proj.Value as JArray ?? new JArray();
                    foreach (var item in items)
                    {
                        var queryRef = item["queryRef"]?.ToString() ?? "";
                        // Strip Sum(...), Count(...), etc. so "Sum(Fact_Sales.Profit)" → "Fact_Sales.Profit"
                        // — but KEEP which aggregation it was, instead of discarding it.
                        var aggFromRef = "";
                        var m = System.Text.RegularExpressions.Regex.Match(
                            queryRef,
                            @"^(Sum|Count|CountNonNull|Average|Avg|Min|Max)\((.+)\)$",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (m.Success)
                        {
                            aggFromRef = AggFromQueryRefName(m.Groups[1].Value);
                            queryRef = m.Groups[2].Value;
                        }
 
                        var parts = queryRef.Split('.', 2);
                        if (parts.Length != 2) continue;
 
                        AddBinding(
                            parts[0],
                            parts[1],
                            NormalizeRoleName(roleName, visualType),
                            aggFromRef,
                            false,
                            false);
                    }
                }
            }
 
            // Path 3: new format query.queryState.*.projections
            var queryState = sv["query"]?["queryState"] as JObject;
if (queryState != null)
{
    foreach (var roleProp in queryState.Properties())
    {
        var roleName = roleProp.Name;
        var roleProjections = roleProp.Value?["projections"] as JArray ?? new JArray();
 
        foreach (var item in roleProjections)
        {
            var field = item["field"] as JObject;
            if (field == null) continue;
 
            var aggNode = field["Aggregation"];
            var node = aggNode?["Expression"]?["Column"]
            ?? field["Measure"]
            ?? field["Column"];
            var fieldIsMeasure = field["Measure"] != null;
            var fieldAgg = AggFromFunction(aggNode?["Function"]);
            string entity, property;
 
            if (node != null)
{
    entity = node["Expression"]?["SourceRef"]?["Entity"]?.ToString()
          ?? node["Entity"]?.ToString()
          ?? "";
    property = node["Property"]?.ToString() ?? "";
}
else if (field["HierarchyLevel"] != null)
{
    // Power BI's automatic Date Hierarchy (Year/Quarter/Month/Day)
    // on an axis field carries ALL FOUR levels, but only ONE is
    // actually the level the chart displays — marked "active": true
    // on the projection item itself (a sibling of "field"). Skip
    // every level that isn't the active one, so we capture only the
    // single field the original chart actually showed (e.g. "Month"),
    // not all four.
    var isActive = item["active"]?.Value<bool>() ?? true;
    if (!isActive)
    {
        entity = "";
        property = "";
    }
    else
    {
        var hierLevel = field["HierarchyLevel"];
                        entity = hierLevel?["Expression"]?["Hierarchy"]?["Expression"]
                            ?["PropertyVariationSource"]?["Expression"]?["SourceRef"]?["Entity"]?.ToString() ?? "";
                        property = hierLevel?["Level"]?.ToString() ?? "";
    }
}
else
{
    continue;
}
 
            if (string.IsNullOrEmpty(entity) || string.IsNullOrEmpty(property)) continue;
 
            AddBinding(
                entity,
                property,
                NormalizeRoleName(roleName, visualType),
                fieldAgg,
                fieldIsMeasure,
                false);
        }
    }
}
            results.Add(new VisualMetadata
            {
                PageName = pageName,
                VisualName = visualName,
                VisualTitle = visualTitle,
                VisualType = visualType,
                BoundColumns = boundColumns
            });
        }
    }
 
   
 
    _log.LogInformation(
        "ExtractVisualMetadata: {pages} pages, {visuals} visuals, {cols} total column bindings",
        sections.Count,
        results.Count,
        results.Sum(v => v.BoundColumns.Count));
 
    return results;
}
 
        // ── Infer Power BI data role from prototypeQuery Select node ─────────
        private static string InferRoleFromSelect(JToken sel, string visualType)
        {
            // Role can be encoded in "Role" property or inferred from position
            var explicitRole = sel["Role"]?.ToString();
            if (!string.IsNullOrEmpty(explicitRole))
                return NormalizeRoleName(explicitRole, visualType);
 
            // Name hint from "Name" field (e.g. "Sum of Sales" → "Values")
            var name = sel["Name"]?.ToString() ?? "";
            if (name.StartsWith("Sum of", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Count of", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Avg of", StringComparison.OrdinalIgnoreCase))
                return "Values";
 
            return "Values";  // safe default
        }
 
        // ── Aggregation captured from the ORIGINAL visual ─────────────────────
        // prototypeQuery Aggregation.Function codes → canonical aggregation name.
        // Returns "" when the field carries no aggregation (plain column / measure).
        private static string AggFromFunction(JToken? fn)
        {
            if (fn == null || fn.Type != JTokenType.Integer) return "";
            return ((int)fn) switch
            {
                0 => "Sum",
                1 => "Average",
                2 => "DistinctCount",
                3 => "Min",
                4 => "Max",
                5 => "Count",              // CountNonNull = "Count" in the UI
                6 => "Median",
                7 => "StandardDeviation",
                8 => "Variance",
                _ => ""
            };
        }
 
        // queryRef wrappers, e.g. "Sum(Fact_Sales.Profit)" → "Sum"
        private static string AggFromQueryRefName(string fn) => fn.ToLowerInvariant() switch
        {
            "sum"          => "Sum",
            "avg"          => "Average",
            "average"      => "Average",
            "min"          => "Min",
            "max"          => "Max",
            "count"        => "DistinctCount",   // queryRef Count(...) is Count (Distinct)
            "countnonnull" => "Count",
            _              => ""
        };
 
       private static string NormalizeRoleName(string role, string visualType)
{
    var r = role.ToLowerInvariant();
    var t = (visualType ?? "").ToLowerInvariant();
 
    return r switch
    {
        "values" or "value" or "measure"     => "Values",
        "category" or "axis" or "rows"       => t.Contains("matrix") ? "Rows" : "Category",
        "columns"                            => "Columns",
        "legend" or "series"                 => "Series",
        "y" or "yaxis"                       => "Values", // Changed from "Y" to "Values"
        "x" or "xaxis"                       => "Category", // Changed from "X" to "Category"
        "tooltips" or "tooltip"              => "Tooltips",
        "filters" or "filter"                => "Filters",
        _                                    => "Values"
    };
}
        // ── Collect all table/column references (flat list, for legacy use) ───
        public List<(string Table, string Column)> ExtractFieldReferences(JObject layout)
        {
            var refs = new HashSet<(string, string)>();
            var sections = layout["sections"] as JArray ?? new JArray();
            foreach (var section in sections)
            {
                var visuals = section["visualContainers"] as JArray ?? new JArray();
                foreach (var v in visuals)
                {
                    var configRaw = v["config"]?.ToString();
                    if (string.IsNullOrEmpty(configRaw)) continue;
                    try
                    {
                        var cfg = JObject.Parse(configRaw);
                        ExtractFromToken(cfg, refs);
                    }
                    catch { }
                }
            }
            return refs.ToList();
        }
 
        private void ExtractFromToken(JToken token, HashSet<(string, string)> refs)
        {
            if (token is JObject obj)
            {
                if (obj["Entity"] != null && obj["Property"] != null)
                    refs.Add((obj["Entity"]!.ToString(), obj["Property"]!.ToString()));
                foreach (var p in obj.Properties())
                    ExtractFromToken(p.Value, refs);
            }
            else if (token is JArray arr)
                foreach (var item in arr)
                    ExtractFromToken(item, refs);
        }
 
    // ── Rewrite Layout JSON ───────────────────────────────────────────────
// ── Rewrite Layout JSON ───────────────────────────────────────────────
public JObject RewriteLayout(JObject layout, List<VisualFieldMap> fieldMaps)
{
    var lookup = new Dictionary<(string, string), (string schema, string view, string col)>();
    foreach (var vm in fieldMaps)
    {
        foreach (var b in vm.Bindings)
        {
            if (!string.IsNullOrEmpty(b.OriginalTable) && !string.IsNullOrEmpty(b.OriginalColumn))
            {
                lookup[(b.OriginalTable, b.OriginalColumn)] =
                    (b.SnowflakeSchema, b.SnowflakeView, b.SnowflakeColumn);
            }
        }
    }
 
    var clone = (JObject)layout.DeepClone();
    RebindToken(clone, lookup);
    return clone;
}
 
private void RebindToken(
    JToken token,
    Dictionary<(string, string), (string schema, string view, string col)> lookup)
{
    if (token is JObject obj)
    {
        var entity = obj["Entity"]?.ToString();
        var property = obj["Property"]?.ToString();
 
        if (entity != null && property != null)
        {
            // 1. Check if the field is a Measure (from Measures_Table or common measure names)
            if (IsMeasureField(entity, property))
            {
                obj["Entity"] = "_Measures";
                obj["Property"] = NormalizeFieldName(property);
            }
            // 2. Standard exact lookup for fact/dimension columns
            else if (lookup.TryGetValue((entity, property), out var mapped))
            {
                obj["Entity"] = mapped.view;
                obj["Property"] = mapped.col;
            }
        }
 
        foreach (var p in obj.Properties().ToList())
            RebindToken(p.Value, lookup);
    }
    else if (token is JArray arr)
    {
        foreach (var item in arr)
            RebindToken(item, lookup);
    }
}
 
private bool IsMeasureField(string entity, string property)
{
    return entity.Equals("Measures_Table", StringComparison.OrdinalIgnoreCase) ||
           entity.Equals("_Measures", StringComparison.OrdinalIgnoreCase) ||
           property.StartsWith("Total", StringComparison.OrdinalIgnoreCase) ||
           property.Contains("Margin", StringComparison.OrdinalIgnoreCase);
}
 
private string NormalizeFieldName(string field)
{
    if (string.IsNullOrWhiteSpace(field)) return field;
    return field.Trim().ToUpper().Replace(" ", "_").Replace("%", "");
}
 
        // ── Repack PBIX ───────────────────────────────────────────────────────
        public async Task<byte[]> RepackPbixAsync(byte[] originalPbix, JObject newLayout)
        {
            var outputMs = new MemoryStream();
            using (var originalMs = new MemoryStream(originalPbix))
            using (var srcZip = new ZipArchive(originalMs, ZipArchiveMode.Read))
            using (var dstZip = new ZipArchive(outputMs, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var entry in srcZip.Entries)
                {
                    var dst = dstZip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                    if (entry.FullName == "Report/Layout")
                    {
                        var json  = JsonConvert.SerializeObject(newLayout);
                        var bytes = Encoding.Unicode.GetBytes(json);
                        using var ws = dst.Open();
                        await ws.WriteAsync(bytes);
                    }
                    else
                    {
                        using var src = entry.Open();
                        using var ws  = dst.Open();
                        await src.CopyToAsync(ws);
                    }
                }
            }
            return outputMs.ToArray();
        }
    }
}