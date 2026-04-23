using System.Text;
using System.Text.RegularExpressions;
using NosCore.ParserInputGenerator.Extractor;

namespace NosCore.DeveloperTools.Services;

/// <summary>
/// Rewrites the NosMall base URL embedded in <c>conststring.dat</c> inside a
/// NosTale <c>NScliData_*.NOS</c> archive. Anchors on const-string ID 2553
/// and swaps only the scheme/host/path — the query string (with its
/// <c>%s</c> placeholders for sid/pid/user_id/m_szName/sas/c/shopType/
/// display_language) is preserved verbatim, so the client's string-format
/// call stays aligned regardless of which Gameforge URL shape we started
/// from.
/// </summary>
public static class NosMallUrlPatcher
{
    public sealed record PatchResult(bool Success, string Log, byte[]? Output);

    // Anchors on the conststring record for ID 2553 (NosMall URL). Records
    // are delimited by \r; within a record \v separates id and value.
    // Capture group 1 is the full URL — replacement preserves the query
    // string so the %s placeholders stay aligned with whatever the client
    // plugs in (sid / pid / user_id / m_szName / sas / c / shopType /
    // display_language).
    private const int NosMallConstStringId = 2553;
    private static readonly Regex NosmallUrlRegex = new(
        @"(\r" + NosMallConstStringId + @"\v)([^\r]+)",
        RegexOptions.Compiled);

    public static PatchResult Patch(byte[] nosBytes, string newBaseUrl)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Read {nosBytes.Length}-byte archive.");

        // Strip any query string the caller passed — we only want the base.
        var qIdx = newBaseUrl.IndexOf('?');
        if (qIdx >= 0) newBaseUrl = newBaseUrl[..qIdx];

        List<NosArchive.Entry> entries;
        try
        {
            entries = NosArchive.Read(nosBytes);
        }
        catch (Exception ex)
        {
            return new PatchResult(false, $"Read failed: {ex.Message}", null);
        }

        sb.AppendLine($"Parsed {entries.Count} entrie(s).");
        var rewritten = new List<NosArchive.Entry>(entries.Count);
        var totalReplacements = 0;

        foreach (var entry in entries)
        {
            if (!entry.Name.Contains("conststring", StringComparison.OrdinalIgnoreCase))
            {
                rewritten.Add(entry);
                continue;
            }

            var text = Encoding.UTF8.GetString(entry.Content);
            var replacements = 0;
            var patched = NosmallUrlRegex.Replace(text, m =>
            {
                var originalUrl = m.Groups[2].Value;
                var origQ = originalUrl.IndexOf('?');
                var preservedQuery = origQ >= 0 ? originalUrl[origQ..] : string.Empty;
                replacements++;
                return m.Groups[1].Value + newBaseUrl + preservedQuery;
            });
            if (replacements == 0)
            {
                sb.AppendLine($"  {entry.Name}: const-string id {NosMallConstStringId} (NosMall URL) not found.");
                rewritten.Add(entry);
                continue;
            }
            sb.AppendLine($"  {entry.Name}: replaced {replacements} URL(s) (base only, query preserved).");
            totalReplacements += replacements;
            rewritten.Add(entry with { Content = Encoding.UTF8.GetBytes(patched) });
        }

        if (totalReplacements == 0)
        {
            return new PatchResult(false, sb.AppendLine("No URLs replaced — nothing to write.").ToString(), null);
        }

        byte[] output;
        try
        {
            output = NosArchive.Write(rewritten);
        }
        catch (Exception ex)
        {
            return new PatchResult(false, $"Repack failed: {ex.Message}", null);
        }

        sb.AppendLine($"Repacked {output.Length}-byte archive ({totalReplacements} URL replacements).");
        return new PatchResult(true, sb.ToString(), output);
    }
}
