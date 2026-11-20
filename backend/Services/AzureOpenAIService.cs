// using System;
// using System.Collections.Generic;
// using System.Linq;
// using System.Net.Http;
// using System.Text;
// using System.Text.Json;
// using System.Threading.Tasks;
// using Microsoft.Extensions.Logging;
// using PbixMigrator.Models;

// namespace PbixMigrator.Services
// {
//     public class AzureOpenAIService
//     {
//         private readonly ILogger<AzureOpenAIService> _log;
//         private readonly IHttpClientFactory _httpFactory;

//         public AzureOpenAIService(ILogger<AzureOpenAIService> log, IHttpClientFactory httpFactory)
//         {
//             _log = log;
//             _httpFactory = httpFactory;
//         }

//         // ─────────────────────────────────────────────────────────────────────
//         // Local, non-LLM fuzzy matching. Backend safeguard used two ways:
//         //   1. If the LLM call throws / returns bad JSON / drops a binding,
//         //      we still have *something* to offer instead of leaving the
//         //      visual completely unrepaired.
//         //   2. Post-processing on every LLM replacement: if the LLM marked a
//         //      column unmatched (or picked something we can verify is wrong
//         //      because it doesn't actually exist in the target view), try a
//         //      normalised name match ourselves before accepting defeat.
//         //
//         // Normalisation mirrors the prompt: lower-case, strip spaces/
//         // underscores/hyphens, and expand a small set of common abbreviations
//         // so "Row ID" and "ROW_ID" (or "RowId") are recognised as identical.
//         // ─────────────────────────────────────────────────────────────────────
//         private static readonly Dictionary<string, string> AbbreviationExpansions = new(StringComparer.OrdinalIgnoreCase)
//         {
//             ["id"] = "identifier", ["amt"] = "amount", ["qty"] = "quantity",
//             ["num"] = "number", ["no"] = "number", ["desc"] = "description",
//             ["pct"] = "percent", ["avg"] = "average", ["min"] = "minimum",
//             ["max"] = "maximum", ["dt"] = "date", ["ts"] = "timestamp",
//             ["addr"] = "address", ["cust"] = "customer", ["prod"] = "product",
//             ["cat"] = "category", ["qtr"] = "quarter", ["yr"] = "year",
//         };

//         internal static string NormalizeColumnName(string? name)
//         {
//             if (string.IsNullOrWhiteSpace(name)) return "";
//             var cleaned = new StringBuilder();
//             foreach (var ch in name)
//                 if (char.IsLetterOrDigit(ch)) cleaned.Append(char.ToLowerInvariant(ch));
//             var raw = cleaned.ToString();

//             // Also try expanding recognised abbreviation tokens from the
//             // ORIGINAL (space/underscore-delimited) name, since collapsing
//             // to letters/digits first would make tokens unrecoverable.
//             var tokens = name.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
//             var expanded = new StringBuilder();
//             foreach (var tok in tokens)
//             {
//                 var t = tok.ToLowerInvariant();
//                 expanded.Append(AbbreviationExpansions.TryGetValue(t, out var full) ? full : t);
//             }
//             var expandedForm = expanded.ToString();

//             // Prefer the expanded form when it differs — it's the more
//             // "semantic" normalisation and better for cross-source matching.
//             return expandedForm.Length > 0 ? expandedForm : raw;
//         }

//         // Best local match for a single (table, column) against the
//         // available Snowflake views. Returns null if nothing plausible.
//        private static (string ViewName, string Schema, string ColumnName, double Confidence)? LocalBestMatch(
//     string oldTable, string oldColumn, List<SnowflakeViewMeta> snowflakeViews)
// {
//     if (string.IsNullOrWhiteSpace(oldColumn) || snowflakeViews == null || snowflakeViews.Count == 0)
//         return null;

//     var normOldCol = NormalizeColumnName(oldColumn);
//     var normOldTable = NormalizeColumnName(oldTable);

//     // 1) Prefer same table name + same column
//     foreach (var view in snowflakeViews)
//     {
//         if (NormalizeColumnName(view.ViewName) != normOldTable) continue;
//         foreach (var col in view.Columns)
//         {
//             if (NormalizeColumnName(col.Name) == normOldCol)
//                 return (view.ViewName, view.Schema, col.Name, 0.97);
//         }
//     }

//     // 2) Same column name in any table (handles Measures_Table → KPI/view columns)
//     foreach (var view in snowflakeViews)
//     {
//         foreach (var col in view.Columns)
//         {
//             if (NormalizeColumnName(col.Name) == normOldCol)
//                 return (view.ViewName, view.Schema, col.Name, 0.9);
//         }
//     }

//     // 3) Contains / partial match on column
//     foreach (var view in snowflakeViews)
//     {
//         foreach (var col in view.Columns)
//         {
//             var normNewCol = NormalizeColumnName(col.Name);
//             if (normNewCol.Length == 0 || normOldCol.Length == 0) continue;
//             if (normNewCol.Contains(normOldCol) || normOldCol.Contains(normNewCol))
//                 return (view.ViewName, view.Schema, col.Name, 0.65);
//         }
//     }

//     return null;
// }
//         // ── Shared low-level call: chat completion -> raw string content ──────
//         private async Task<string> CallChatCompletionAsync(
//             PowerBIConfig cfg, string systemPrompt, string userPrompt)
//         {
//             var payload = new
//             {
//                 messages = new[]
//                 {
//                     new { role = "system", content = systemPrompt },
//                     new { role = "user",   content = userPrompt   }
//                 },
//                 max_tokens = 4096,
//                 temperature = 0.1
//             };

//             var endpoint =
//                 $"{cfg.AzureOpenAIEndpoint.TrimEnd('/')}/openai/deployments/" +
//                 $"{cfg.AzureOpenAIDeployment}/chat/completions?api-version=2024-02-15-preview";

//             using var http = _httpFactory.CreateClient();
//             http.DefaultRequestHeaders.Add("api-key", cfg.AzureOpenAIKey);

//             _log.LogDebug("[OpenAI] ▶ POST {endpoint}\nSYSTEM PROMPT:\n{sys}\nUSER PROMPT:\n{usr}",
//                 endpoint, systemPrompt, userPrompt);

//             var sw = System.Diagnostics.Stopwatch.StartNew();
//             HttpResponseMessage resp;
//             try
//             {
//                 resp = await http.PostAsync(endpoint,
//                     new StringContent(JsonSerializer.Serialize(payload),
//                         Encoding.UTF8, "application/json"));
//             }
//             catch (Exception ex)
//             {
//                 _log.LogError(ex, "[OpenAI] ✘ request failed (network/endpoint) after {ms}ms", sw.ElapsedMilliseconds);
//                 throw new InvalidOperationException(
//                     $"Azure OpenAI request failed (network/endpoint): {ex.Message}", ex);
//             }

//             var body = await resp.Content.ReadAsStringAsync();
//             sw.Stop();
//             _log.LogInformation("[OpenAI] ◀ {status} in {ms}ms — {bytes} bytes",
//                 (int)resp.StatusCode, sw.ElapsedMilliseconds, body.Length);

//             if (!resp.IsSuccessStatusCode)
//             {
//                 _log.LogError("[OpenAI] ✘ non-success response body: {body}", body);
//                 throw new InvalidOperationException(
//                     $"Azure OpenAI returned {(int)resp.StatusCode}: {body}");
//             }

//             using var doc = JsonDocument.Parse(body);
//             if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
//                 choices.GetArrayLength() == 0)
//             {
//                 throw new InvalidOperationException(
//                     $"Azure OpenAI response had no choices[]: {body}");
//             }

//             var content = choices[0]
//                 .GetProperty("message")
//                 .GetProperty("content")
//                 .GetString();

//             if (string.IsNullOrWhiteSpace(content))
//                 throw new InvalidOperationException("Azure OpenAI returned empty message content.");

//             _log.LogInformation("[OpenAI] raw message content:\n{resp}", content);

//             // Strip accidental markdown fences
//             content = content.Trim();
//             var hadFences = content.StartsWith("```");
//             if (content.StartsWith("```"))
//             {
//                 var firstNewline = content.IndexOf('\n');
//                 content = firstNewline >= 0 ? content[(firstNewline + 1)..] : content;
//             }
//             if (content.EndsWith("```"))
//                 content = content[..^3];
//             if (hadFences)
//                 _log.LogDebug("[OpenAI] stripped markdown fences from response before JSON parse");

//             return content.Trim();
//         }

//         // ─────────────────────────────────────────────────────────────────────
//         // PER-VISUAL COLUMN REBIND
//         //
//         // Core of the new pipeline. Given:
//         //   - One visual with its current column bindings (old dataset)
//         //   - All available Snowflake views + their columns (new dataset)
//         //
//         // The LLM returns, for EACH old column binding, the best matching
//         // column in the new dataset (table + column + role preserved).
//         //
//         // Processing is per-visual so:
//         //   (a) Each visual gets full context → better semantic matching
//         //   (b) One visual failure never blocks others (caller catches per-visual)
//         //   (c) Unmatched columns return Matched=false → marked as error visual
//         // ─────────────────────────────────────────────────────────────────────
//         public async Task<VisualRebindResult> RebindVisualColumnsAsync(
//             PowerBIConfig cfg,
//             VisualRebindTarget visual,
//             List<SnowflakeViewMeta> snowflakeViews)
//         {
//             _log.LogInformation("[Rebind] ▶ visual '{v}' ({title}) — {n} current binding(s): {bindings}",
//                 visual.VisualName, visual.VisualTitle, visual.CurrentBindings?.Count ?? 0,
//                 JsonSerializer.Serialize(visual.CurrentBindings));

//             var result = new VisualRebindResult
//             {
//                 VisualName  = visual.VisualName,
//                 VisualTitle = visual.VisualTitle
//             };

//             if (visual.CurrentBindings == null || visual.CurrentBindings.Count == 0)
//             {
//                 _log.LogInformation("[Rebind] visual '{v}' has zero current bindings — skipping LLM call", visual.VisualName);
//                 result.HasError    = false;
//                 result.ErrorReason = "Visual has no bound columns — nothing to rebind.";
//                 return result;
//             }

//             // Build compact view catalogue for the prompt
//             var viewCatalogue = snowflakeViews.Select(v => new
//             {
//                 tableName = v.ViewName,
//                 schema    = v.Schema,
//                 columns   = v.Columns.Select(c => new { name = c.Name, type = c.DataType }).ToList()
//             }).ToList();

//             var systemPrompt =
//                 "You are a Power BI dataset migration assistant.\n" +
//                 "A PBIX report has been republished against a NEW Snowflake dataset.\n" +
//                 "Your task: for each column binding currently in a visual, find the\n" +
//                 "BEST MATCHING column in the new Snowflake dataset and return a replacement plan.\n" +
//                 "\n" +
//                 "BE FORGIVING about surface-level naming differences. Snowflake views commonly\n" +
//                 "use SCREAMING_SNAKE_CASE while the original PBIX used PascalCase / spaced names,\n" +
//                 "so before ruling out a candidate column, normalise BOTH names by:\n" +
//                 "  - lower-casing\n" +
//                 "  - removing all spaces, underscores, and hyphens\n" +
//                 "  - treating common abbreviations as equivalent to their expansion, e.g.\n" +
//                 "    id=identifier, amt=amount, qty=quantity, num/no=number, desc=description,\n" +
//                 "    pct=percent, avg=average, min=minimum, max=maximum, dt=date, ts=timestamp,\n" +
//                 "    addr=address, qtd/mtd/ytd stay as-is, cust=customer, prod=product, cat=category\n" +
//                 "If the normalised names match (or match after abbreviation expansion), treat it as\n" +
//                 "an exact/near-exact match — do NOT reject a column just because of case, spacing,\n" +
//                 "or underscore differences. For example \"Row ID\" and \"ROW_ID\" are the SAME column\n" +
//                 "(normalise to \"rowid\" / \"rowidentifier\") and must be matched with high confidence.\n" +
//                 "\n" +
//                 "Matching rules (apply in priority order):\n" +
//                 "  1. Exact name match after the normalisation above → confidence 0.97-1.0\n" +
//                 "  2. Same semantic meaning, different wording (e.g. 'SalesAmt' → 'SALE_AMOUNT') → confidence 0.8-0.95\n" +
//                 "  3. Partial/abbreviation/substring match → confidence 0.6-0.8\n" +
//                 "  4. Genuinely no reasonable match → set matched=false, newColumn=\"\", confidence=0.0\n" +
//                 "\n" +
//                 "Err on the side of proposing your best candidate with a lower confidence score rather\n" +
//                 "than giving up (matched=false) — a partial repair with an honest confidence value is\n" +
//                 "always more useful than an empty result. Only use matched=false when there is truly no\n" +
//                 "column in the new dataset that plausibly corresponds to the original.\n" +
//                 "\n" +
//                 "For the table (view) to assign: prefer the same table if it exists in the new\n" +
//                 "dataset (again, comparing names after the same normalisation); otherwise pick the\n" +
//                 "Snowflake view whose name/purpose best matches the original table name. Try to keep\n" +
//                 "all columns of one visual in one table where possible.\n" +
//                 "\n" +
//                 "Return ONLY a JSON array — no markdown, no commentary:\n" +
//                 "[\n" +
//                 "  {\n" +
//                 "    \"oldTable\": \"<exact original table>\",\n" +
//                 "    \"oldColumn\": \"<exact original column>\",\n" +
//                 "    \"oldRole\": \"<exact original data role>\",\n" +
//                 "    \"newTable\": \"<snowflake view name or empty>\",\n" +
//                 "    \"newColumn\": \"<snowflake column name or empty>\",\n" +
//                 "    \"newRole\": \"<same role — do not change>\",\n" +
//                 "    \"matched\": true,\n" +
//                 "    \"confidence\": 0.95,\n" +
//                 "    \"matchNote\": \"<brief reason e.g. 'exact match' or 'SalesAmt→SALE_AMOUNT semantic'>\"\n" +
//                 "  }\n" +
//                 "]\n" +
//                 "Every input binding MUST appear in the output exactly once.\n" +
//                 "Do NOT omit any binding even if unmatched — set matched=false and leave newColumn empty.";

//             var userPrompt =
//                 $"Visual: \"{visual.VisualTitle}\" (type: {visual.VisualType})\n\n" +
//                 "Current column bindings to replace:\n" +
//                 JsonSerializer.Serialize(visual.CurrentBindings) +
//                 "\n\nAvailable Snowflake views and columns:\n" +
//                 JsonSerializer.Serialize(viewCatalogue);

//             // Best-effort local fallback (no LLM) for every current binding —
//             // used whenever the LLM call/parse fails outright, so the visual
//             // still gets SOME repair attempt instead of none at all.
//             List<ColumnReplacement> BuildLocalReplacements() => visual.CurrentBindings.Select(b =>
//             {
//                 var m = LocalBestMatch(b.Table, b.Column, snowflakeViews);
//                 return new ColumnReplacement
//                 {
//                     OldTable   = b.Table,
//                     OldColumn  = b.Column,
//                     OldRole    = b.DataRole,
//                     NewTable   = m?.ViewName ?? "",
//                     NewColumn  = m?.ColumnName ?? "",
//                     NewRole    = b.DataRole,
//                     Matched    = m != null,
//                     Confidence = m?.Confidence ?? 0.0,
//                 };
//             }).ToList();

//             string content;
//             try
//             {
//                 content = await CallChatCompletionAsync(cfg, systemPrompt, userPrompt);
//             }
//             catch (Exception ex)
//             {
//                 _log.LogError(ex, "LLM column rebind failed for visual '{v}' — falling back to local name matching", visual.VisualName);
//                 var local = BuildLocalReplacements();
//                 result.Replacements = local;
//                 result.HasError     = local.Any(r => !r.Matched);
//                 result.ErrorReason  = $"LLM call failed ({ex.Message}) — used local best-effort name matching instead " +
//                     $"({local.Count(r => r.Matched)}/{local.Count} matched).";
//                 return result;
//             }

//             List<ColumnReplacement>? replacements;
//             try
//             {
//                 replacements = JsonSerializer.Deserialize<List<ColumnReplacement>>(content,
//                     new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
//             }
//             catch (JsonException ex)
//             {
//                 _log.LogError(ex, "[Rebind] ✘ LLM returned non-JSON for visual '{v}': {c} — falling back to local name matching",
//                     visual.VisualName, content[..Math.Min(300, content.Length)]);
//                 var local = BuildLocalReplacements();
//                 result.Replacements = local;
//                 result.HasError     = local.Any(r => !r.Matched);
//                 result.ErrorReason  = $"LLM returned non-JSON ({ex.Message}) — used local best-effort name matching instead " +
//                     $"({local.Count(r => r.Matched)}/{local.Count} matched).";
//                 return result;
//             }

//             if (replacements == null || replacements.Count == 0)
//             {
//                 _log.LogWarning("[Rebind] ✘ LLM returned an EMPTY replacement list for visual '{v}' — " +
//                     "falling back to local best-effort name matching instead of giving up entirely.", visual.VisualName);
//                 var local = BuildLocalReplacements();
//                 result.Replacements = local;
//                 result.HasError     = local.Any(r => !r.Matched);
//                 result.ErrorReason  = $"LLM returned empty replacement list — used local best-effort name matching instead " +
//                     $"({local.Count(r => r.Matched)}/{local.Count} matched).";
//                 return result;
//             }

//             // Any binding the LLM marked unmatched (or left empty), or simply
//             // omitted from its response, gets one more chance via local
//             // normalised-name matching before we accept it as a lost cause.
//             var covered = new HashSet<string>(
//                 replacements.Select(r => $"{r.OldTable}\u0001{r.OldColumn}\u0001{r.OldRole}"),
//                 StringComparer.OrdinalIgnoreCase);

//             foreach (var r in replacements)
//             {
//                 if (r.Matched && !string.IsNullOrWhiteSpace(r.NewColumn)) continue;
//                 var m = LocalBestMatch(r.OldTable, r.OldColumn, snowflakeViews);
//                 if (m == null) continue;
//                 r.NewTable   = m.Value.ViewName;
//                 r.NewColumn  = m.Value.ColumnName;
//                 r.NewRole    = string.IsNullOrWhiteSpace(r.NewRole) ? r.OldRole : r.NewRole;
//                 r.Matched    = true;
//                 r.Confidence = m.Value.Confidence;
//                 _log.LogInformation("[Rebind] visual '{v}': local fallback upgraded '{c}' → {t}.{nc}",
//                     visual.VisualName, r.OldColumn, r.NewTable, r.NewColumn);
//             }

//             foreach (var b in visual.CurrentBindings)
//             {
//                 var key = $"{b.Table}\u0001{b.Column}\u0001{b.DataRole}";
//                 if (covered.Contains(key)) continue;

//                 var m = LocalBestMatch(b.Table, b.Column, snowflakeViews);
//                 replacements.Add(new ColumnReplacement
//                 {
//                     OldTable   = b.Table,
//                     OldColumn  = b.Column,
//                     OldRole    = b.DataRole,
//                     NewTable   = m?.ViewName ?? "",
//                     NewColumn  = m?.ColumnName ?? "",
//                     NewRole    = b.DataRole,
//                     Matched    = m != null,
//                     Confidence = m?.Confidence ?? 0.0,
//                 });
//                 _log.LogWarning("[Rebind] visual '{v}': LLM omitted binding '{t}.{c}' — filled via local fallback ({ok})",
//                     visual.VisualName, b.Table, b.Column, m != null ? "matched" : "unmatched");
//             }

//             // ─────────────────────────────────────────────────────────────
//             // BACKEND HARDENING — root-cause fix for the "bloated universal
//             // mapping hits Cards" bug: the LLM occasionally ignores the
//             // "every input binding MUST appear in the output exactly once"
//             // instruction and returns a large templated/hallucinated array
//             // (10+ entries covering columns that belong to OTHER visuals or
//             // simply don't exist in visual.CurrentBindings at all). Nothing
//             // downstream previously rejected those extra entries — they
//             // rode along in result.Replacements all the way to the frontend,
//             // which is what let 11 fields reach a single-binding Card.
//             //
//             // Two independent guards, applied in order:
//             //   1. HALLUCINATION FILTER — drop any replacement whose
//             //      (OldTable, OldColumn) doesn't correspond to one of THIS
//             //      visual's actual CurrentBindings. This is the primary
//             //      defense: it directly removes foreign/templated entries
//             //      regardless of visual type.
//             //   2. SINGLE-VALUE CAPACITY CAP — belt-and-braces: if the
//             //      visual's type identifies it as single-value (Card/KPI/
//             //      Gauge) and, even after filtering, more than one
//             //      replacement remains (e.g. genuine duplicates for the
//             //      same old binding), keep only the single best one
//             //      (matched > unmatched, then highest confidence).
//             // Both guards log loudly and record what was dropped so the
//             // condition is visible in the server log even if the frontend
//             // never has to invoke its own truncation logic at all.
//             // ─────────────────────────────────────────────────────────────
//             var currentBindingKeys = new HashSet<string>(
//                 visual.CurrentBindings.Select(b => $"{b.Table}\u0001{b.Column}"),
//                 StringComparer.OrdinalIgnoreCase);

//             var foreign = replacements.Where(r => !currentBindingKeys.Contains($"{r.OldTable}\u0001{r.OldColumn}")).ToList();
//             if (foreign.Count > 0)
//             {
//                 _log.LogError(
//                     "[Rebind] 🚨 visual '{v}' ({type}): LLM returned {n} replacement(s) for column(s) NOT present " +
//                     "in this visual's current bindings — dropping as hallucinated/templated entries: {list}. " +
//                     "This is the backend-side signature of a templated-mapping bug; the frontend should never see these.",
//                     visual.VisualName, visual.VisualType, foreign.Count,
//                     string.Join(", ", foreign.Select(f => $"{f.OldTable}.{f.OldColumn}")));
//                 replacements = replacements.Except(foreign).ToList();
//             }

//             bool isSingleValueVisual =
//                 (visual.VisualType ?? "").Contains("card", StringComparison.OrdinalIgnoreCase) ||
//                 (visual.VisualType ?? "").Contains("kpi", StringComparison.OrdinalIgnoreCase) ||
//                 string.Equals(visual.VisualType, "gauge", StringComparison.OrdinalIgnoreCase);

//             if (isSingleValueVisual && replacements.Count > 1)
//             {
//                 var best = replacements
//                     .OrderByDescending(r => r.Matched && !string.IsNullOrWhiteSpace(r.NewColumn))
//                     .ThenByDescending(r => r.Confidence)
//                     .First();
//                 var dropped = replacements.Where(r => r != best).ToList();
//                 _log.LogError(
//                     "[Rebind] 🚨 visual '{v}' is single-value (type='{type}') but {n} replacement(s) remained after " +
//                     "the hallucination filter — forcibly keeping only the best one ({keptTable}.{keptCol}) and " +
//                     "dropping: {dropped}",
//                     visual.VisualName, visual.VisualType, replacements.Count,
//                     best.NewTable ?? best.OldTable, best.NewColumn ?? best.OldColumn,
//                     string.Join(", ", dropped.Select(d => $"{d.OldTable}.{d.OldColumn}")));
//                 replacements = new List<ColumnReplacement> { best };
//             }

//             // ── CATALOG VALIDATION ───────────────────────────────────────────────
// // Force every matched replacement to use EXACT table/column names from
// // the Snowflake catalog. If AI returned a name that does not exist,
// // try LocalBestMatch; if still not found, mark unmatched.
// // This keeps mapping automatic and report-agnostic (no hardcoded KPIs).
// for (int i = 0; i < replacements.Count; i++)
// {
//     var r = replacements[i];
//     if (!r.Matched || string.IsNullOrWhiteSpace(r.NewColumn))
//         continue;

//     // Does (NewTable, NewColumn) exist in catalog (case-insensitive)?
//     var view = snowflakeViews.FirstOrDefault(v =>
//         string.Equals(v.ViewName, r.NewTable, StringComparison.OrdinalIgnoreCase));
//     var col = view?.Columns?.FirstOrDefault(c =>
//         string.Equals(c.Name, r.NewColumn, StringComparison.OrdinalIgnoreCase));

//     if (view != null && col != null)
//     {
//         // Rewrite to exact catalog casing (critical for Power BI bind)
//         r.NewTable = view.ViewName;
//         r.NewColumn = col.Name;
//         continue;
//     }

//     // Not in catalog → local match against full catalog
//     var m = LocalBestMatch(r.OldTable, r.OldColumn, snowflakeViews);
//     if (m != null)
//     {
//         _log.LogWarning(
//             "[Rebind] visual '{v}': AI target '{t}.{c}' not in catalog — corrected to '{nt}.{nc}'",
//             visual.VisualName, r.NewTable, r.NewColumn, m.Value.ViewName, m.Value.ColumnName);
//         r.NewTable = m.Value.ViewName;
//         r.NewColumn = m.Value.ColumnName;
//         r.Matched = true;
//         r.Confidence = m.Value.Confidence;
//         continue;
//     }

//     _log.LogWarning(
//         "[Rebind] visual '{v}': no catalog match for '{ot}.{oc}' (AI had '{t}.{c}') — marking unmatched",
//         visual.VisualName, r.OldTable, r.OldColumn, r.NewTable, r.NewColumn);
//     r.Matched = false;
//     r.NewTable = "";
//     r.NewColumn = "";
//     r.Confidence = 0;
// }

// // Cards / KPI / Gauge: keep role as Values (Power BI rejects "Data")
// var vt = visual.VisualType ?? "";
// if (vt.Contains("card", StringComparison.OrdinalIgnoreCase) ||
//     vt.Contains("kpi", StringComparison.OrdinalIgnoreCase) ||
//     string.Equals(vt, "gauge", StringComparison.OrdinalIgnoreCase))
// {
//     foreach (var r in replacements)
//         r.NewRole = "Values";
// }

//             result.Replacements = replacements;
//             _log.LogInformation("[Rebind] visual '{v}' — parsed {n} replacement(s) after guards: {list}",
//                 visual.VisualName, replacements.Count, JsonSerializer.Serialize(replacements));

//             // The prompt requires "every input binding MUST appear in the output
//             // exactly once". Gaps are now filled via local fallback above, and
//             // foreign/hallucinated entries are filtered above, so this should
//             // only fire for a genuine remaining mismatch (e.g. the single-value
//             // cap intentionally reducing count for a visual with >1 CurrentBindings,
//             // which itself would be unusual and worth surfacing).
//             if (replacements.Count != visual.CurrentBindings.Count)
//             {
//                 result.HasError    = true;
//                 result.ErrorReason = $"Expected {visual.CurrentBindings.Count} replacements " +
//                     $"(one per current binding) but {replacements.Count} remained after hallucination/capacity guards.";
//                 _log.LogWarning("[Rebind] ✘ visual '{v}': binding count mismatch — {expected} expected, {actual} returned",
//                     visual.VisualName, visual.CurrentBindings.Count, replacements.Count);
//             }

//             // Mark as error visual if ANY column could not be matched
//             var unmatchedCount = replacements.Count(r => !r.Matched || string.IsNullOrWhiteSpace(r.NewColumn));
//             if (unmatchedCount > 0)
//             {
//                 result.HasError    = true;
//                 result.ErrorReason = (result.ErrorReason != null ? result.ErrorReason + " " : "") +
//                     $"{unmatchedCount}/{replacements.Count} column(s) could not be matched to the new dataset.";
//             }

//             _log.LogInformation(
//                 "Visual '{v}': {total} columns → {matched} matched, {unmatched} unmatched",
//                 visual.VisualName, replacements.Count,
//                 replacements.Count - unmatchedCount, unmatchedCount);

//             return result;
//         }

//         // ── Batch: rebind columns for ALL visuals, never stopping on failure ──
//         // Returns a combined response; visuals with LLM errors are marked HasError=true.
//         public async Task<VisualColumnRebindResponse> RebindAllVisualsAsync(
//             PowerBIConfig cfg,
//             List<VisualRebindTarget> visuals,
//             List<SnowflakeViewMeta> snowflakeViews)
//         {
//             var results = new List<VisualRebindResult>();

//             foreach (var visual in visuals)
//             {
//                 // Each visual is independent — catch and mark errors, never throw
//                 VisualRebindResult r;
//                 try
//                 {
//                     r = await RebindVisualColumnsAsync(cfg, visual, snowflakeViews);
//                 }
//                 catch (Exception ex)
//                 {
//                     _log.LogError(ex, "Unexpected error rebinding visual '{v}'", visual.VisualName);
//                     r = new VisualRebindResult
//                     {
//                         VisualName  = visual.VisualName,
//                         VisualTitle = visual.VisualTitle,
//                         HasError    = true,
//                         ErrorReason = $"Unexpected: {ex.Message}"
//                     };
//                 }
//                 results.Add(r);
//             }

//             return new VisualColumnRebindResponse
//             {
//                 Results       = results,
//                 TotalVisuals  = results.Count,
//                 MappedVisuals = results.Count(r => !r.HasError),
//                 ErrorVisuals  = results.Count(r => r.HasError)
//             };
//         }

//         // ── Legacy: LLM visual-to-view binding (kept for llm-bind endpoint) ──
//         public async Task<List<LlmVisualBinding>> LlmBindVisualsAsync(
//             PowerBIConfig cfg, string systemPrompt, string userPrompt)
//         {
//             string content;
//             try
//             {
//                 content = await CallChatCompletionAsync(cfg, systemPrompt, userPrompt);
//             }
//             catch (Exception ex)
//             {
//                 _log.LogError(ex, "LlmBindVisuals call to Azure OpenAI failed");
//                 throw;
//             }

//             List<LlmVisualBinding>? result;
//             try
//             {
//                 result = JsonSerializer.Deserialize<List<LlmVisualBinding>>(content,
//                     new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
//             }
//             catch (JsonException ex)
//             {
//                 throw new InvalidOperationException(
//                     $"LLM returned non-JSON for visual binding. " +
//                     $"Raw (first 500): {content[..Math.Min(500, content.Length)]}", ex);
//             }

//             if (result == null || result.Count == 0)
//             {
//                 _log.LogWarning("LlmBindVisuals returned zero bindings.");
//                 return new List<LlmVisualBinding>();
//             }

//             var matched   = result.Count(b => !string.IsNullOrWhiteSpace(b.SnowflakeView));
//             var unmatched = result.Count - matched;
//             _log.LogInformation(
//                 "LlmBindVisuals: {total} visuals → {matched} matched, {unmatched} unmatched",
//                 result.Count, matched, unmatched);

//             return result;
//         }

//         // ── Legacy: Field mappings for layout JSON rewrite ────────────────────
//         public async Task<List<VisualFieldMap>> GenerateFieldMappingsAsync(
//             PowerBIConfig cfg,
//             List<(string Table, string Column)> pbixRefs,
//             List<SnowflakeViewMeta> snowflakeMeta)
//         {
//             if (pbixRefs.Count == 0 || snowflakeMeta.Count == 0)
//                 return new List<VisualFieldMap>();

//             var systemPrompt =
//                 "You are a Power BI to Snowflake migration assistant. " +
//                 "Given a list of Power BI field references and Snowflake view metadata, " +
//                 "return ONLY a JSON array of VisualFieldMap objects with this exact shape:\n" +
//                 "[\n" +
//                 "  {\n" +
//                 "    \"VisualName\": \"<descriptive name>\",\n" +
//                 "    \"VisualType\": \"table|chart|card|slicer|matrix|other\",\n" +
//                 "    \"Bindings\": [\n" +
//                 "      {\n" +
//                 "        \"OriginalTable\": \"<pbix table, EXACT string from input>\",\n" +
//                 "        \"OriginalColumn\": \"<pbix column, EXACT string from input>\",\n" +
//                 "        \"SnowflakeSchema\": \"<schema>\",\n" +
//                 "        \"SnowflakeView\": \"<view name>\",\n" +
//                 "        \"SnowflakeColumn\": \"<column name>\"\n" +
//                 "      }\n" +
//                 "    ]\n" +
//                 "  }\n" +
//                 "]\n" +
//                 "CRITICAL: OriginalTable and OriginalColumn must be copied verbatim. " +
//                 "Return ONLY valid JSON, no markdown fences, no commentary.";

//             var userPrompt =
//                 "PBIX field references (map every single one):\n" +
//                 JsonSerializer.Serialize(pbixRefs.Select(r => new { Table = r.Table, Column = r.Column })) +
//                 "\n\nSnowflake views and columns:\n" +
//                 JsonSerializer.Serialize(snowflakeMeta);

//             var content = await CallChatCompletionAsync(cfg, systemPrompt, userPrompt);

//             List<VisualFieldMap>? result;
//             try
//             {
//                 result = JsonSerializer.Deserialize<List<VisualFieldMap>>(content,
//                     new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
//             }
//             catch (JsonException ex)
//             {
//                 throw new InvalidOperationException(
//                     $"Azure OpenAI returned non-JSON for field mapping. Raw: {content[..Math.Min(500, content.Length)]}", ex);
//             }

//             return result ?? new List<VisualFieldMap>();
//         }
//     }
// }


using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PbixMigrator.Models;

namespace PbixMigrator.Services
{
    public class AzureOpenAIService
    {
        private readonly ILogger<AzureOpenAIService> _log;
        private readonly IHttpClientFactory _httpFactory;

        public AzureOpenAIService(ILogger<AzureOpenAIService> log, IHttpClientFactory httpFactory)
        {
            _log = log;
            _httpFactory = httpFactory;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Local, non-LLM fuzzy matching. Backend safeguard used two ways:
        //   1. If the LLM call throws / returns bad JSON / drops a binding,
        //      we still have *something* to offer instead of leaving the
        //      visual completely unrepaired.
        //   2. Post-processing on every LLM replacement: if the LLM marked a
        //      column unmatched (or picked something we can verify is wrong
        //      because it doesn't actually exist in the target view), try a
        //      normalised name match ourselves before accepting defeat.
        //
        // Normalisation mirrors the prompt: lower-case, strip spaces/
        // underscores/hyphens, and expand a small set of common abbreviations
        // so "Row ID" and "ROW_ID" (or "RowId") are recognised as identical.
        // ─────────────────────────────────────────────────────────────────────
        private static readonly Dictionary<string, string> AbbreviationExpansions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = "identifier", ["amt"] = "amount", ["qty"] = "quantity",
            ["num"] = "number", ["no"] = "number", ["desc"] = "description",
            ["pct"] = "percent", ["avg"] = "average", ["min"] = "minimum",
            ["max"] = "maximum", ["dt"] = "date", ["ts"] = "timestamp",
            ["addr"] = "address", ["cust"] = "customer", ["prod"] = "product",
            ["cat"] = "category", ["qtr"] = "quarter", ["yr"] = "year",
        };

        internal static string NormalizeColumnName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var cleaned = new StringBuilder();
            foreach (var ch in name)
                if (char.IsLetterOrDigit(ch)) cleaned.Append(char.ToLowerInvariant(ch));
            var raw = cleaned.ToString();

            var tokens = name.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
            var expanded = new StringBuilder();
            foreach (var tok in tokens)
            {
                var t = tok.ToLowerInvariant();
                expanded.Append(AbbreviationExpansions.TryGetValue(t, out var full) ? full : t);
            }
            var expandedForm = expanded.ToString();

            return expandedForm.Length > 0 ? expandedForm : raw;
        }

        // ─────────────────────────────────────────────────────────────────────
        // GOLD-SCHEMA EXCLUSION.
        //
        // A Gold-schema Snowflake view (e.g. "KPI_TOTAL_PROFIT") is a real,
        // legitimate entry in the Snowflake catalog — but it is NEVER left as
        // a queryable table in the actual Power BI semantic model after the
        // TOM rewrite. TabularModelService.AddGoldMeasuresTable consumes every
        // selected Gold view and converts its single KPI value into a DAX
        // measure on "_Measures"/"Measures_Table" instead. The Gold view
        // itself is not materialised as a table anywhere the report can bind
        // to.
        //
        // Before this fix, LocalBestMatch's "same column name in any table"
        // tier had no way to tell Gold views apart from ordinary Bronze/
        // Silver ones, so a visual that used to read a measure like
        // "Total Profit" got matched against KPI_TOTAL_PROFIT.TOTAL_PROFIT —
        // a real Snowflake catalog entry, so CATALOG VALIDATION below
        // confirmed it as legitimate — and every attempt to actually bind
        // the visual to it in Power BI failed with FailedToValidateDataRole,
        // because that table simply doesn't exist in the model. That's what
        // produced the endless "Nuclear pass" retries in the browser: the
        // backend kept handing the frontend a target guaranteed to fail.
        //
        // Filtering Gold views out of every candidate pool here means a
        // binding that used to point at a measure now honestly comes back
        // unmatched instead of confidently pointing at something that can
        // never work — which stops the retry loop. Getting it to actually
        // re-bind correctly requires the caller to pass in the model's real
        // current measure list (see ExistingMeasureMeta / existingMeasures
        // below); until that's wired up, these bindings show up as
        // legitimately unmatched rather than falsely "fixed."
        // ─────────────────────────────────────────────────────────────────────
        private static readonly HashSet<string> GoldLikeSchemas =
            new(StringComparer.OrdinalIgnoreCase) { "GOLD" };

        private static List<SnowflakeViewMeta> ExcludeGoldViews(List<SnowflakeViewMeta> snowflakeViews) =>
            (snowflakeViews ?? new List<SnowflakeViewMeta>())
                .Where(v => !GoldLikeSchemas.Contains(v.Schema))
                .ToList();

        // Small shared helpers for the measure-vs-column policy in
        // RebindVisualColumnsAsync (kept next to the other static helpers).
        private static bool Eq(string? a, string? b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static bool IsSingleValueType(string? visualType) =>
            (visualType ?? "").Contains("card", StringComparison.OrdinalIgnoreCase) ||
            (visualType ?? "").Contains("kpi", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(visualType, "gauge", StringComparison.OrdinalIgnoreCase);

        // A DAX measure already present in the (post-rewrite) Tabular model —
        // e.g. one preserved on a disconnected "Measures_Table"/"_Measures"
        // calculation table, or one created by AddGoldMeasuresTable from a
        // Gold-schema KPI view. Pass these in via RebindVisualColumnsAsync's
        // existingMeasures parameter once the caller has them available; a
        // binding matched here uses the EXACT table/measure name, never a
        // Snowflake view/column pair.
        public class ExistingMeasureMeta
        {
            public string TableName   { get; set; } = "";
            public string MeasureName { get; set; } = "";
        }

        private static (string TableName, string MeasureName, double Confidence)? LocalBestMeasureMatch(
            string oldColumn, List<ExistingMeasureMeta>? existingMeasures)
        {
            if (string.IsNullOrWhiteSpace(oldColumn) || existingMeasures == null || existingMeasures.Count == 0)
                return null;

            var normOldCol = NormalizeColumnName(oldColumn);

            foreach (var m in existingMeasures)
                if (NormalizeColumnName(m.MeasureName) == normOldCol)
                    return (m.TableName, m.MeasureName, 0.95);

            foreach (var m in existingMeasures)
            {
                var normMeasure = NormalizeColumnName(m.MeasureName);
                if (normMeasure.Length == 0 || normOldCol.Length == 0) continue;
                if (normMeasure.Contains(normOldCol) || normOldCol.Contains(normMeasure))
                    return (m.TableName, m.MeasureName, 0.7);
            }
            return null;
        }

        // Best local match for a single (table, column) against the
        // available Snowflake views. Returns null if nothing plausible.
        // Callers must pass an already Gold-filtered list (see
        // ExcludeGoldViews) — this stays a pure "search this catalogue"
        // primitive.

// Dynamic: measure-like table/view (no hardcoded KPI names)
private static bool IsMeasureLikeName(string? name)
{
    if (string.IsNullOrWhiteSpace(name)) return false;
    var n = NormalizeColumnName(name);
    return n.Contains("measure") || n.StartsWith("kpi");
}

private static (string ViewName, string Schema, string ColumnName, double Confidence)? LocalBestMatch(
    string oldTable, string oldColumn, List<SnowflakeViewMeta> snowflakeViews)
{
    if (string.IsNullOrWhiteSpace(oldColumn) || snowflakeViews == null || snowflakeViews.Count == 0)
        return null;

    var normOldCol = NormalizeColumnName(oldColumn);
    var normOldTable = NormalizeColumnName(oldTable);
    var oldIsMeasureLike = IsMeasureLikeName(oldTable);

    (string ViewName, string Schema, string ColumnName, double Confidence)? Search(
        IEnumerable<SnowflakeViewMeta> views, bool requireTableNameMatch)
    {
        // 1) Same table name + same column
        if (requireTableNameMatch || !string.IsNullOrEmpty(normOldTable))
        {
            foreach (var view in views)
            {
                if (NormalizeColumnName(view.ViewName) != normOldTable) continue;
                foreach (var col in view.Columns)
                {
                    if (NormalizeColumnName(col.Name) == normOldCol)
                        return (view.ViewName, view.Schema, col.Name, 0.97);
                }
            }
        }

        // 2) Same column name in any table of this set
        foreach (var view in views)
        {
            foreach (var col in view.Columns)
            {
                if (NormalizeColumnName(col.Name) == normOldCol)
                    return (view.ViewName, view.Schema, col.Name, 0.9);
            }
        }

        // 3) Partial / contains match on column
        foreach (var view in views)
        {
            foreach (var col in view.Columns)
            {
                var normNewCol = NormalizeColumnName(col.Name);
                if (normNewCol.Length == 0 || normOldCol.Length == 0) continue;
                if (normNewCol.Contains(normOldCol) || normOldCol.Contains(normNewCol))
                    return (view.ViewName, view.Schema, col.Name, 0.65);
            }
        }

        return null;
    }

    // A) Old measures table → search measure-like views first (_Measures, etc.)
    if (oldIsMeasureLike)
    {
        var measureViews = snowflakeViews.Where(v => IsMeasureLikeName(v.ViewName)).ToList();
        var hit = Search(measureViews, requireTableNameMatch: false);
        if (hit != null)
            return hit;
    }

    // B) Fallback: all views (fact/dim/etc.)
    var allHit = Search(snowflakeViews, requireTableNameMatch: true);
    if (allHit != null)
        return allHit;

    return Search(snowflakeViews, requireTableNameMatch: false);
}


// Resolves the LLM's claimed newTable/newColumn against the REAL Snowflake
// metadata / measures catalogue the LLM was actually given, case-insensitively,
// and returns the exact casing from that catalogue. Returns null if the LLM's
// claimed match doesn't verbatim-exist anywhere (case-insensitive) — meaning
// the LLM hallucinated a name that isn't real, and the caller should NOT trust it.
private static (string Table, string Column)? ResolveExactCasing(
    string claimedTable, string claimedColumn,
    List<SnowflakeViewMeta> bindableViews, List<ExistingMeasureMeta>? existingMeasures)
{
    if (string.IsNullOrWhiteSpace(claimedTable) || string.IsNullOrWhiteSpace(claimedColumn))
        return null;

    if (existingMeasures != null)
    {
        var mm = existingMeasures.FirstOrDefault(m =>
            string.Equals(m.TableName, claimedTable, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.MeasureName, claimedColumn, StringComparison.OrdinalIgnoreCase));
        if (mm != null) return (mm.TableName, mm.MeasureName);
    }

    var view = bindableViews.FirstOrDefault(v =>
        string.Equals(v.ViewName, claimedTable, StringComparison.OrdinalIgnoreCase));
    var col = view?.Columns.FirstOrDefault(c =>
        string.Equals(c.Name, claimedColumn, StringComparison.OrdinalIgnoreCase));

    return (view != null && col != null) ? (view.ViewName, col.Name) : null;
}

        // ── Shared low-level call: chat completion -> raw string content ──────
        private async Task<string> CallChatCompletionAsync(
            PowerBIConfig cfg, string systemPrompt, string userPrompt)
        {
            var payload = new
            {
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user",   content = userPrompt   }
                },
                max_tokens = 4096,
                temperature = 0.1
            };

            var endpoint =
                $"{cfg.AzureOpenAIEndpoint.TrimEnd('/')}/openai/deployments/" +
                $"{cfg.AzureOpenAIDeployment}/chat/completions?api-version=2024-02-15-preview";

            using var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.Add("api-key", cfg.AzureOpenAIKey);

            _log.LogDebug("[OpenAI] ▶ POST {endpoint}\nSYSTEM PROMPT:\n{sys}\nUSER PROMPT:\n{usr}",
                endpoint, systemPrompt, userPrompt);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            HttpResponseMessage resp;
            try
            {
                resp = await http.PostAsync(endpoint,
                    new StringContent(JsonSerializer.Serialize(payload),
                        Encoding.UTF8, "application/json"));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[OpenAI] ✘ request failed (network/endpoint) after {ms}ms", sw.ElapsedMilliseconds);
                throw new InvalidOperationException(
                    $"Azure OpenAI request failed (network/endpoint): {ex.Message}", ex);
            }

            var body = await resp.Content.ReadAsStringAsync();
            sw.Stop();
            _log.LogInformation("[OpenAI] ◀ {status} in {ms}ms — {bytes} bytes",
                (int)resp.StatusCode, sw.ElapsedMilliseconds, body.Length);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogError("[OpenAI] ✘ non-success response body: {body}", body);
                throw new InvalidOperationException(
                    $"Azure OpenAI returned {(int)resp.StatusCode}: {body}");
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.GetArrayLength() == 0)
            {
                throw new InvalidOperationException(
                    $"Azure OpenAI response had no choices[]: {body}");
            }

            var content = choices[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidOperationException("Azure OpenAI returned empty message content.");

            _log.LogInformation("[OpenAI] raw message content:\n{resp}", content);

            content = content.Trim();
            var hadFences = content.StartsWith("```");
            if (content.StartsWith("```"))
            {
                var firstNewline = content.IndexOf('\n');
                content = firstNewline >= 0 ? content[(firstNewline + 1)..] : content;
            }
            if (content.EndsWith("```"))
                content = content[..^3];
            if (hadFences)
                _log.LogDebug("[OpenAI] stripped markdown fences from response before JSON parse");

            return content.Trim();
        }

        // ─────────────────────────────────────────────────────────────────────
        // PER-VISUAL COLUMN REBIND
        // ─────────────────────────────────────────────────────────────────────
        public async Task<VisualRebindResult> RebindVisualColumnsAsync(
            PowerBIConfig cfg,
            VisualRebindTarget visual,
            List<SnowflakeViewMeta> snowflakeViews,
            List<ExistingMeasureMeta>? existingMeasures = null)
        {
            _log.LogInformation("[Rebind] ▶ visual '{v}' ({title}) — {n} current binding(s): {bindings}",
                visual.VisualName, visual.VisualTitle, visual.CurrentBindings?.Count ?? 0,
                JsonSerializer.Serialize(visual.CurrentBindings));

            var result = new VisualRebindResult
            {
                VisualName  = visual.VisualName,
                VisualTitle = visual.VisualTitle
            };

            if (visual.CurrentBindings == null || visual.CurrentBindings.Count == 0)
            {
                _log.LogInformation("[Rebind] visual '{v}' has zero current bindings — skipping LLM call", visual.VisualName);
                result.HasError    = false;
                result.ErrorReason = "Visual has no bound columns — nothing to rebind.";
                return result;
            }

            var bindableViews = ExcludeGoldViews(snowflakeViews);

            // ── MEASURE vs COLUMN POLICY ─────────────────────────────────────
            // An existing measure (KPI / Gold) is a valid target ONLY when the
            // ORIGINAL field was a measure, or the visual is single-value
            // (Card / KPI / Gauge — existing behaviour, unchanged). An original
            // column — with or without an aggregation — in a table / matrix /
            // chart must map to a COLUMN and keep its aggregation.
            var singleValue = IsSingleValueType(visual.VisualType);

            bool MeasureAllowedFor(VisualColumnBinding b) => singleValue || b.IsMeasure;

            List<ExistingMeasureMeta>? MeasuresFor(VisualColumnBinding b) =>
                MeasureAllowedFor(b) ? existingMeasures : null;

            VisualColumnBinding? OriginalOf(ColumnReplacement rep)
            {
                var cands = visual.CurrentBindings
                    .Where(b => Eq(b.Table, rep.OldTable) && Eq(b.Column, rep.OldColumn))
                    .ToList();
                return cands.FirstOrDefault(b => Eq(b.DataRole, rep.OldRole)) ?? cands.FirstOrDefault();
            }

            List<ExistingMeasureMeta>? MeasuresForReplacement(ColumnReplacement rep)
            {
                var orig = OriginalOf(rep);
                return orig != null && MeasureAllowedFor(orig) ? existingMeasures : null;
            }

            // Stamps aggregation / measure flags from the ORIGINAL binding (the
            // source of truth) — never from the LLM — so the frontend can bind
            // the target as a measure, a plain column, or column + aggregation.
            List<ColumnReplacement> Annotate(List<ColumnReplacement> list)
            {
                var used = new HashSet<VisualColumnBinding>();
                foreach (var rep in list)
                {
                    var cands = visual.CurrentBindings
                        .Where(b => Eq(b.Table, rep.OldTable) && Eq(b.Column, rep.OldColumn))
                        .ToList();
                    var orig = cands.FirstOrDefault(b => !used.Contains(b) && Eq(b.DataRole, rep.OldRole))
                            ?? cands.FirstOrDefault(b => !used.Contains(b))
                            ?? cands.FirstOrDefault();
                    if (orig != null) used.Add(orig);

                    rep.OldIsMeasure = orig?.IsMeasure ?? false;
                    rep.NewIsMeasure = rep.Matched
                        && !string.IsNullOrWhiteSpace(rep.NewColumn)
                        && (existingMeasures ?? new List<ExistingMeasureMeta>())
                            .Any(x => Eq(x.TableName, rep.NewTable) && Eq(x.MeasureName, rep.NewColumn));

                    // Re-apply the original aggregation only when the target is a
                    // real column. A measure carries its own aggregation, and an
                    // original measure never had one to preserve.
                    rep.Aggregation = (orig == null || orig.IsMeasure || rep.NewIsMeasure)
                        ? ""
                        : (orig.Aggregation ?? "");
                }
                return list;
            }

            var viewCatalogue = bindableViews.Select(v => new
            {
                tableName = v.ViewName,
                schema    = v.Schema,
                columns   = v.Columns.Select(c => new { name = c.Name, type = c.DataType }).ToList()
            }).ToList();

            // Only offer the measures list to the LLM when at least one binding
            // is actually allowed to use it (original measure, or a card/KPI/gauge).
            var measureCatalogue = (visual.CurrentBindings.Any(MeasureAllowedFor)
                    ? (existingMeasures ?? new List<ExistingMeasureMeta>())
                    : new List<ExistingMeasureMeta>())
                .Select(m => new { tableName = m.TableName, measureName = m.MeasureName })
                .ToList();

            var systemPrompt =
                "You are a Power BI dataset migration assistant.\n" +
                "A PBIX report has been republished against a NEW Snowflake dataset.\n" +
                "Your task: for each column binding currently in a visual, find the\n" +
                "BEST MATCHING column in the new Snowflake dataset and return a replacement plan.\n" +
                "\n" +
                "BE FORGIVING about surface-level naming differences. Snowflake views commonly\n" +
                "use SCREAMING_SNAKE_CASE while the original PBIX used PascalCase / spaced names,\n" +
                "so before ruling out a candidate column, normalise BOTH names by:\n" +
                "  - lower-casing\n" +
                "  - removing all spaces, underscores, and hyphens\n" +
                "  - treating common abbreviations as equivalent to their expansion, e.g.\n" +
                "    id=identifier, amt=amount, qty=quantity, num/no=number, desc=description,\n" +
                "    pct=percent, avg=average, min=minimum, max=maximum, dt=date, ts=timestamp,\n" +
                "    addr=address, qtd/mtd/ytd stay as-is, cust=customer, prod=product, cat=category\n" +
                "If the normalised names match (or match after abbreviation expansion), treat it as\n" +
                "an exact/near-exact match — do NOT reject a column just because of case, spacing,\n" +
                "or underscore differences.\n" +
                "\n" +
                "Matching rules (apply in priority order):\n" +
                "  1. Exact name match after the normalisation above → confidence 0.97-1.0\n" +
                "  2. Same semantic meaning, different wording → confidence 0.8-0.95\n" +
                "  3. Partial/abbreviation/substring match → confidence 0.6-0.8\n" +
                "  4. Genuinely no reasonable match → set matched=false, newColumn=\"\", confidence=0.0\n" +
                "\n" +
                "PRIORITY when the original table is measure-like (name contains 'Measure' or 'KPI'):\n" +
"  A. First try \"Available existing measures\" — use EXACT tableName + measureName from that list.\n" +
"  B. Else try a measure-like view in \"Available Snowflake views and columns\"\n" +
"     (view name contains 'Measure' or 'KPI', e.g. _Measures) with normalized column match\n" +
"     (Total Profit ≈ TOTAL_PROFIT).\n" +
"  C. Only if A and B fail, use a normal fact/dim column.\n" +
"Do NOT map an original measure-table field to a raw fact column (e.g. FACT_SALES.PROFIT)\n" +
"when a matching measure or measure-view column exists in the lists above.\n" +
"(This priority is subject to the STRICT MEASURE-vs-COLUMN RULE below.)\n" +
"\n" +
                "For the table (view) to assign: prefer the same table if it exists in the new\n" +
                "dataset; otherwise pick the Snowflake view whose name/purpose best matches the\n" +
                "original table name.\n" +
                "\n" +
                "Return ONLY a JSON array — no markdown, no commentary:\n" +
                "[\n" +
                "  {\n" +
                "    \"oldTable\": \"<exact original table>\",\n" +
                "    \"oldColumn\": \"<exact original column>\",\n" +
                "    \"oldRole\": \"<exact original data role>\",\n" +
                "    \"newTable\": \"<snowflake view name, or a measures table name from the measures list, or empty>\",\n" +
                "    \"newColumn\": \"<snowflake column name, or a measure name from the measures list, or empty>\",\n" +
                "    \"newRole\": \"<same role — do not change>\",\n" +
                "    \"matched\": true,\n" +
                "    \"confidence\": 0.95,\n" +
                "    \"matchNote\": \"<brief reason>\"\n" +
                "  }\n" +
                "]\n" +
                "Every input binding MUST appear in the output exactly once.\n" +
                "Do NOT omit any binding even if unmatched — set matched=false and leave newColumn empty.\n" +
                "\n" +
                "STRICT MEASURE-vs-COLUMN RULE (overrides every other instruction in this prompt):\n" +
                "  Every current binding carries IsMeasure and Aggregation (copied from the original report).\n" +
                "  - IsMeasure=true  → it may map to an existing measure.\n" +
                "  - IsMeasure=false (a plain column, optionally aggregated, e.g. Aggregation=\"Sum\") in a\n" +
                "    table / matrix / chart → you MUST map it to a COLUMN from\n" +
                "    \"Available Snowflake views and columns\" and NEVER to an existing measure.\n" +
                "    The aggregation is re-applied by the application; do not encode it in the column name.\n" +
                "  - Card / KPI / gauge visuals keep the existing behaviour (an existing measure is allowed).\n" +
                "\n" +
                "IMPORTANT — two separate candidate pools are provided below:\n" +
                "  1. \"Available Snowflake views and columns\" — ordinary queryable tables. A\n" +
                "     newTable/newColumn pair chosen from here must be an EXACT tableName/column\n" +
                "     name from that list.\n" +
                "  2. \"Available existing measures\" — DAX measures that ALREADY EXIST in the\n" +
                "     target model (e.g. business KPIs like Total Profit, Profit Margin %). If an\n" +
                "     original binding is a MEASURE (IsMeasure=true), or the visual is a card/KPI/gauge,\n" +
                "     and it matches\n" +
                "     one of these measures, you MUST return that measure's EXACT tableName as\n" +
                "     newTable and its EXACT measureName as newColumn.\n" +
                "  Only use names that appear verbatim in one of these two lists. Never construct or\n" +
                "  guess a table name yourself, even if it sounds plausible (e.g. \"kpi_total_profit\")\n" +
                "  — if it is not listed, it does not exist in the target model.";

            var userPrompt =
                $"Visual: \"{visual.VisualTitle}\" (type: {visual.VisualType})\n\n" +
                "Current column bindings to replace:\n" +
                JsonSerializer.Serialize(visual.CurrentBindings) +
                "\n\nAvailable Snowflake views and columns:\n" +
                JsonSerializer.Serialize(viewCatalogue) +
                "\n\nAvailable existing measures (bind newTable/newColumn to these EXACTLY when a " +
                "binding is semantically a KPI/aggregate — these are NOT Snowflake columns, they " +
                "are DAX measures already defined in the model):\n" +
                JsonSerializer.Serialize(measureCatalogue);

            List<ColumnReplacement> BuildLocalReplacements() => Annotate(visual.CurrentBindings.Select(b =>
            {
                var mm = LocalBestMeasureMatch(b.Column, MeasuresFor(b));
                if (mm != null)
                {
                    return new ColumnReplacement
                    {
                        OldTable   = b.Table,
                        OldColumn  = b.Column,
                        OldRole    = b.DataRole,
                        NewTable   = mm.Value.TableName,
                        NewColumn  = mm.Value.MeasureName,
                        NewRole    = b.DataRole,
                        Matched    = true,
                        Confidence = mm.Value.Confidence,
                    };
                }
                var m = LocalBestMatch(b.Table, b.Column, bindableViews);
                return new ColumnReplacement
                {
                    OldTable   = b.Table,
                    OldColumn  = b.Column,
                    OldRole    = b.DataRole,
                    NewTable   = m?.ViewName ?? "",
                    NewColumn  = m?.ColumnName ?? "",
                    NewRole    = b.DataRole,
                    Matched    = m != null,
                    Confidence = m?.Confidence ?? 0.0,
                };
            }).ToList());

            string content;
            try
            {
                content = await CallChatCompletionAsync(cfg, systemPrompt, userPrompt);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "LLM column rebind failed for visual '{v}' — falling back to local name matching", visual.VisualName);
                var local = BuildLocalReplacements();
                result.Replacements = local;
                result.HasError     = local.Any(r => !r.Matched);
                result.ErrorReason  = $"LLM call failed ({ex.Message}) — used local best-effort name matching instead " +
                    $"({local.Count(r => r.Matched)}/{local.Count} matched).";
                return result;
            }

            List<ColumnReplacement>? replacements;
            try
            {
                replacements = JsonSerializer.Deserialize<List<ColumnReplacement>>(content,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                _log.LogError(ex, "[Rebind] ✘ LLM returned non-JSON for visual '{v}': {c} — falling back to local name matching",
                    visual.VisualName, content[..Math.Min(300, content.Length)]);
                var local = BuildLocalReplacements();
                result.Replacements = local;
                result.HasError     = local.Any(r => !r.Matched);
                result.ErrorReason  = $"LLM returned non-JSON ({ex.Message}) — used local best-effort name matching instead " +
                    $"({local.Count(r => r.Matched)}/{local.Count} matched).";
                return result;
            }

            if (replacements == null || replacements.Count == 0)
            {
                _log.LogWarning("[Rebind] ✘ LLM returned an EMPTY replacement list for visual '{v}' — " +
                    "falling back to local best-effort name matching instead of giving up entirely.", visual.VisualName);
                var local = BuildLocalReplacements();
                result.Replacements = local;
                result.HasError     = local.Any(r => !r.Matched);
                result.ErrorReason  = $"LLM returned empty replacement list — used local best-effort name matching instead " +
                    $"({local.Count(r => r.Matched)}/{local.Count} matched).";
                return result;
            }

            var covered = new HashSet<string>(
                replacements.Select(r => $"{r.OldTable}\u0001{r.OldColumn}\u0001{r.OldRole}"),
                StringComparer.OrdinalIgnoreCase);

            foreach (var r in replacements)
            {
                if (r.Matched && !string.IsNullOrWhiteSpace(r.NewColumn)) continue;
                var mm = LocalBestMeasureMatch(r.OldColumn, MeasuresForReplacement(r));
                if (mm != null)
                {
                    r.NewTable   = mm.Value.TableName;
                    r.NewColumn  = mm.Value.MeasureName;
                    r.NewRole    = string.IsNullOrWhiteSpace(r.NewRole) ? r.OldRole : r.NewRole;
                    r.Matched    = true;
                    r.Confidence = mm.Value.Confidence;
                    _log.LogInformation("[Rebind] visual '{v}': local fallback upgraded '{c}' → measure {t}.{nc}",
                        visual.VisualName, r.OldColumn, r.NewTable, r.NewColumn);
                    continue;
                }
                var m = LocalBestMatch(r.OldTable, r.OldColumn, bindableViews);
                if (m == null) continue;
                r.NewTable   = m.Value.ViewName;
                r.NewColumn  = m.Value.ColumnName;
                r.NewRole    = string.IsNullOrWhiteSpace(r.NewRole) ? r.OldRole : r.NewRole;
                r.Matched    = true;
                r.Confidence = m.Value.Confidence;
                _log.LogInformation("[Rebind] visual '{v}': local fallback upgraded '{c}' → {t}.{nc}",
                    visual.VisualName, r.OldColumn, r.NewTable, r.NewColumn);
            }

            foreach (var b in visual.CurrentBindings)
            {
                var key = $"{b.Table}\u0001{b.Column}\u0001{b.DataRole}";
                if (covered.Contains(key)) continue;

                var mm = LocalBestMeasureMatch(b.Column, MeasuresFor(b));
                var m  = mm == null ? LocalBestMatch(b.Table, b.Column, bindableViews) : null;
                var matched = mm != null || m != null;
                replacements.Add(new ColumnReplacement
                {
                    OldTable   = b.Table,
                    OldColumn  = b.Column,
                    OldRole    = b.DataRole,
                    NewTable   = mm?.TableName ?? m?.ViewName ?? "",
                    NewColumn  = mm?.MeasureName ?? m?.ColumnName ?? "",
                    NewRole    = b.DataRole,
                    Matched    = matched,
                    Confidence = mm?.Confidence ?? m?.Confidence ?? 0.0,
                });
                _log.LogWarning("[Rebind] visual '{v}': LLM omitted binding '{t}.{c}' — filled via local fallback ({ok})",
                    visual.VisualName, b.Table, b.Column, matched ? "matched" : "unmatched");
            }

            var currentBindingKeys = new HashSet<string>(
                visual.CurrentBindings.Select(b => $"{b.Table}\u0001{b.Column}"),
                StringComparer.OrdinalIgnoreCase);

            var foreign = replacements.Where(r => !currentBindingKeys.Contains($"{r.OldTable}\u0001{r.OldColumn}")).ToList();
            if (foreign.Count > 0)
            {
                _log.LogError(
                    "[Rebind] 🚨 visual '{v}' ({type}): LLM returned {n} replacement(s) for column(s) NOT present " +
                    "in this visual's current bindings — dropping as hallucinated/templated entries: {list}.",
                    visual.VisualName, visual.VisualType, foreign.Count,
                    string.Join(", ", foreign.Select(f => $"{f.OldTable}.{f.OldColumn}")));
                replacements = replacements.Except(foreign).ToList();
            }

            bool isSingleValueVisual =
                (visual.VisualType ?? "").Contains("card", StringComparison.OrdinalIgnoreCase) ||
                (visual.VisualType ?? "").Contains("kpi", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(visual.VisualType, "gauge", StringComparison.OrdinalIgnoreCase);

            if (isSingleValueVisual && replacements.Count > 1)
            {
                var best = replacements
                    .OrderByDescending(r => r.Matched && !string.IsNullOrWhiteSpace(r.NewColumn))
                    .ThenByDescending(r => r.Confidence)
                    .First();
                var dropped = replacements.Where(r => r != best).ToList();
                _log.LogError(
                    "[Rebind] 🚨 visual '{v}' is single-value (type='{type}') but {n} replacement(s) remained after " +
                    "the hallucination filter — forcibly keeping only the best one ({keptTable}.{keptCol}) and dropping: {dropped}",
                    visual.VisualName, visual.VisualType, replacements.Count,
                    best.NewTable ?? best.OldTable, best.NewColumn ?? best.OldColumn,
                    string.Join(", ", dropped.Select(d => $"{d.OldTable}.{d.OldColumn}")));
                replacements = new List<ColumnReplacement> { best };
            }

            // ── CATALOG VALIDATION ───────────────────────────────────────────
            // Force every matched replacement to use EXACT table/column names
            // from the Gold-excluded Snowflake catalog, OR an exact
            // table/measure name from existingMeasures. A target that exists
            // ONLY under a Gold schema — real in Snowflake, never a table in
            // the actual model — is rejected here exactly as if it didn't
            // exist, since binding to it is guaranteed to fail Power BI's
            // data role validation.
            // A measure target is accepted here only if the measure policy
            // above allows it for THIS binding (original measure, or a
            // card/KPI/gauge); otherwise it is rejected and re-matched to a column.
            for (int i = 0; i < replacements.Count; i++)
            {
                var r = replacements[i];
                if (!r.Matched || string.IsNullOrWhiteSpace(r.NewColumn))
                    continue;

                var measureHit = (MeasuresForReplacement(r) ?? new List<ExistingMeasureMeta>())
                    .FirstOrDefault(m =>
                        string.Equals(m.TableName, r.NewTable, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(m.MeasureName, r.NewColumn, StringComparison.OrdinalIgnoreCase));
                if (measureHit != null)
                {
                    r.NewTable  = measureHit.TableName;
                    r.NewColumn = measureHit.MeasureName;
                    continue;
                }

                var view = bindableViews.FirstOrDefault(v =>
                    string.Equals(v.ViewName, r.NewTable, StringComparison.OrdinalIgnoreCase));
                var col = view?.Columns?.FirstOrDefault(c =>
                    string.Equals(c.Name, r.NewColumn, StringComparison.OrdinalIgnoreCase));

                if (view != null && col != null)
                {
                    r.NewTable = view.ViewName;
                    r.NewColumn = col.Name;
                    continue;
                }

                var wasGoldOnly = snowflakeViews.Any(v =>
                    GoldLikeSchemas.Contains(v.Schema) &&
                    string.Equals(v.ViewName, r.NewTable, StringComparison.OrdinalIgnoreCase));
                if (wasGoldOnly)
                {
                    _log.LogError(
                        "[Rebind] 🚨 visual '{v}': target '{t}.{c}' is a Gold-schema view — real in Snowflake, " +
                        "but never materialised as a table in the model (it becomes a measure instead). " +
                        "Rejecting rather than handing the frontend a guaranteed FailedToValidateDataRole.",
                        visual.VisualName, r.NewTable, r.NewColumn);
                }

                var mm = LocalBestMeasureMatch(r.OldColumn, MeasuresForReplacement(r));
                if (mm != null)
                {
                    _log.LogWarning(
                        "[Rebind] visual '{v}': target '{t}.{c}' rejected — corrected to measure '{nt}.{nc}'",
                        visual.VisualName, r.NewTable, r.NewColumn, mm.Value.TableName, mm.Value.MeasureName);
                    r.NewTable = mm.Value.TableName;
                    r.NewColumn = mm.Value.MeasureName;
                    r.Matched = true;
                    r.Confidence = mm.Value.Confidence;
                    continue;
                }

                var m = LocalBestMatch(r.OldTable, r.OldColumn, bindableViews);
                if (m != null)
                {
                    _log.LogWarning(
                        "[Rebind] visual '{v}': target '{t}.{c}' rejected — corrected to '{nt}.{nc}'",
                        visual.VisualName, r.NewTable, r.NewColumn, m.Value.ViewName, m.Value.ColumnName);
                    r.NewTable = m.Value.ViewName;
                    r.NewColumn = m.Value.ColumnName;
                    r.Matched = true;
                    r.Confidence = m.Value.Confidence;
                    continue;
                }

                _log.LogWarning(
                    "[Rebind] visual '{v}': no valid catalog/measure match for '{ot}.{oc}' (target was '{t}.{c}') — marking unmatched",
                    visual.VisualName, r.OldTable, r.OldColumn, r.NewTable, r.NewColumn);
                r.Matched = false;
                r.NewTable = "";
                r.NewColumn = "";
                r.Confidence = 0;
            }

            var vt = visual.VisualType ?? "";
            if (vt.Contains("card", StringComparison.OrdinalIgnoreCase) ||
                vt.Contains("kpi", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(vt, "gauge", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var r in replacements)
                    r.NewRole = "Values";
            }

            // Stamp Aggregation / OldIsMeasure / NewIsMeasure from the ORIGINAL bindings.
            replacements = Annotate(replacements);

            result.Replacements = replacements;
            _log.LogInformation("[Rebind] visual '{v}' — parsed {n} replacement(s) after guards: {list}",
                visual.VisualName, replacements.Count, JsonSerializer.Serialize(replacements));

            if (replacements.Count != visual.CurrentBindings.Count)
            {
                result.HasError    = true;
                result.ErrorReason = $"Expected {visual.CurrentBindings.Count} replacements " +
                    $"(one per current binding) but {replacements.Count} remained after hallucination/capacity guards.";
                _log.LogWarning("[Rebind] ✘ visual '{v}': binding count mismatch — {expected} expected, {actual} returned",
                    visual.VisualName, visual.CurrentBindings.Count, replacements.Count);
            }

            var unmatchedCount = replacements.Count(r => !r.Matched || string.IsNullOrWhiteSpace(r.NewColumn));
            if (unmatchedCount > 0)
            {
                result.HasError    = true;
                result.ErrorReason = (result.ErrorReason != null ? result.ErrorReason + " " : "") +
                    $"{unmatchedCount}/{replacements.Count} column(s) could not be matched to the new dataset.";
            }

            _log.LogInformation(
                "Visual '{v}': {total} columns → {matched} matched, {unmatched} unmatched",
                visual.VisualName, replacements.Count,
                replacements.Count - unmatchedCount, unmatchedCount);

            return result;
        }

        public async Task<VisualColumnRebindResponse> RebindAllVisualsAsync(
            PowerBIConfig cfg,
            List<VisualRebindTarget> visuals,
            List<SnowflakeViewMeta> snowflakeViews,
            List<ExistingMeasureMeta>? existingMeasures = null)
        {
            var results = new List<VisualRebindResult>();

            foreach (var visual in visuals)
            {
                VisualRebindResult r;
                try
                {
                    r = await RebindVisualColumnsAsync(cfg, visual, snowflakeViews, existingMeasures);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Unexpected error rebinding visual '{v}'", visual.VisualName);
                    r = new VisualRebindResult
                    {
                        VisualName  = visual.VisualName,
                        VisualTitle = visual.VisualTitle,
                        HasError    = true,
                        ErrorReason = $"Unexpected: {ex.Message}"
                    };
                }
                results.Add(r);
            }

            return new VisualColumnRebindResponse
            {
                Results       = results,
                TotalVisuals  = results.Count,
                MappedVisuals = results.Count(r => !r.HasError),
                ErrorVisuals  = results.Count(r => r.HasError)
            };
        }

        public async Task<List<LlmVisualBinding>> LlmBindVisualsAsync(
            PowerBIConfig cfg, string systemPrompt, string userPrompt)
        {
            string content;
            try
            {
                content = await CallChatCompletionAsync(cfg, systemPrompt, userPrompt);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "LlmBindVisuals call to Azure OpenAI failed");
                throw;
            }

            List<LlmVisualBinding>? result;
            try
            {
                result = JsonSerializer.Deserialize<List<LlmVisualBinding>>(content,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"LLM returned non-JSON for visual binding. " +
                    $"Raw (first 500): {content[..Math.Min(500, content.Length)]}", ex);
            }

            if (result == null || result.Count == 0)
            {
                _log.LogWarning("LlmBindVisuals returned zero bindings.");
                return new List<LlmVisualBinding>();
            }

            var matched   = result.Count(b => !string.IsNullOrWhiteSpace(b.SnowflakeView));
            var unmatched = result.Count - matched;
            _log.LogInformation(
                "LlmBindVisuals: {total} visuals → {matched} matched, {unmatched} unmatched",
                result.Count, matched, unmatched);

            return result;
        }

        public async Task<List<VisualFieldMap>> GenerateFieldMappingsAsync(
            PowerBIConfig cfg,
            List<(string Table, string Column)> pbixRefs,
            List<SnowflakeViewMeta> snowflakeMeta)
        {
            if (pbixRefs.Count == 0 || snowflakeMeta.Count == 0)
                return new List<VisualFieldMap>();

            var systemPrompt =
                "You are a Power BI to Snowflake migration assistant. " +
                "Given a list of Power BI field references and Snowflake view metadata, " +
                "return ONLY a JSON array of VisualFieldMap objects with this exact shape:\n" +
                "[\n" +
                "  {\n" +
                "    \"VisualName\": \"<descriptive name>\",\n" +
                "    \"VisualType\": \"table|chart|card|slicer|matrix|other\",\n" +
                "    \"Bindings\": [\n" +
                "      {\n" +
                "        \"OriginalTable\": \"<pbix table, EXACT string from input>\",\n" +
                "        \"OriginalColumn\": \"<pbix column, EXACT string from input>\",\n" +
                "        \"SnowflakeSchema\": \"<schema>\",\n" +
                "        \"SnowflakeView\": \"<view name>\",\n" +
                "        \"SnowflakeColumn\": \"<column name>\"\n" +
                "      }\n" +
                "    ]\n" +
                "  }\n" +
                "]\n" +
                "CRITICAL: OriginalTable and OriginalColumn must be copied verbatim. " +
                "Return ONLY valid JSON, no markdown fences, no commentary.";

            var userPrompt =
                "PBIX field references (map every single one):\n" +
                JsonSerializer.Serialize(pbixRefs.Select(r => new { Table = r.Table, Column = r.Column })) +
                "\n\nSnowflake views and columns:\n" +
                JsonSerializer.Serialize(snowflakeMeta);

            var content = await CallChatCompletionAsync(cfg, systemPrompt, userPrompt);

            List<VisualFieldMap>? result;
            try
            {
                result = JsonSerializer.Deserialize<List<VisualFieldMap>>(content,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Azure OpenAI returned non-JSON for field mapping. Raw: {content[..Math.Min(500, content.Length)]}", ex);
            }

            return result ?? new List<VisualFieldMap>();
        }
    }
}