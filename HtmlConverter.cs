using System.Net;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// One-time migration helper: converts legacy plain-text "Responsibility"
/// data into HTML suitable for storage/rendering in CKEditor.
///
/// Handles multiple real-world numbering styles found in source data:
///   "1. text"    (period + space)
///   "1.text"     (period, no space)
///   "1) text"    (parenthesis)
///   "1 Text"     (bare number, no punctuation)
///
/// Candidate markers are validated as a strictly ascending sequence
/// (1,2,3,4...) before being treated as real list delimiters, so a stray
/// digit inside normal prose does not cause a false split.
/// </summary>
public enum ConversionOutcome
{
    Numbered,
    Bullet,
    Paragraph,
    Skipped
}

public static class HtmlConverter
{
    // Matches a candidate list marker in any of the four styles above.
    // Preceded by start-of-string or whitespace to avoid matching mid-word digits.
    private static readonly Regex ListMarkerRegex = new Regex(
        @"(?:(?<=^)|(?<=\s))(\d{1,2})(?:\.\s*|\)\s*|\s+(?=[A-Z]))",
        RegexOptions.Compiled);

    private static readonly Regex BulletSplitRegex =
        new Regex(@"\s*[•·]\s*", RegexOptions.Compiled);

    public static string Convert(string raw, out ConversionOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            outcome = ConversionOutcome.Skipped;
            return string.Empty;
        }

        // Normalize whitespace/newlines to single spaces first
        var text = Regex.Replace(raw.Trim(), @"[ \t]+", " ");
        text = Regex.Replace(text, @"\s*\r?\n\s*", " ");

        var candidates = ListMarkerRegex.Matches(text);
        var markers = FindConsecutiveSequence(candidates);

        // Require at least 2 real markers to call this a numbered list.
        // A single stray match is almost certainly a false positive.
        if (markers.Count < 2)
        {
            outcome = text.Contains('•') || text.Contains('·') ? ConversionOutcome.Bullet : ConversionOutcome.Paragraph;
            return WrapAsBulletsOrParagraph(text);
        }

        outcome = ConversionOutcome.Numbered;

        var items = new List<string>();

        // Text before the first valid marker becomes item 1.
        // Covers both: (a) list missing its "1." label entirely, and
        // (b) a lead-in sentence before the numbering starts.
        var preamble = text.Substring(0, markers[0].Index).Trim();
        if (!string.IsNullOrWhiteSpace(preamble))
            items.Add(preamble);

        for (int i = 0; i < markers.Count; i++)
        {
            int start = markers[i].Index + markers[i].Length;
            int end = (i + 1 < markers.Count) ? markers[i + 1].Index : text.Length;
            var item = text.Substring(start, end - start).Trim();
            if (!string.IsNullOrWhiteSpace(item))
                items.Add(item);
        }

        var sb = new StringBuilder();
        sb.Append("<ol>");
        foreach (var item in items)
            sb.Append("<li>").Append(BuildItemBody(item)).Append("</li>");
        sb.Append("</ol>");

        return sb.ToString();
    }

    /// <summary>
    /// Walks the candidate matches and keeps only those forming a strictly
    /// ascending run (N, N+1, N+2, ...) starting at 1 or 2. Non-conforming
    /// matches (stray digits in normal prose) are skipped rather than
    /// breaking the whole sequence.
    /// </summary>
    private static List<Match> FindConsecutiveSequence(MatchCollection candidates)
    {
        var result = new List<Match>();
        int expected = -1;

        foreach (Match m in candidates)
        {
            int val = int.Parse(m.Groups[1].Value);

            if (result.Count == 0)
            {
                if (val == 1 || val == 2)
                {
                    result.Add(m);
                    expected = val + 1;
                }
                continue;
            }

            if (val == expected)
            {
                result.Add(m);
                expected++;
            }
            // else: doesn't fit the sequence — ignore, keep scanning
        }

        return result;
    }

    private static string BuildItemBody(string item)
    {
        if (item.Contains('•') || item.Contains('·'))
        {
            var parts = BulletSplitRegex.Split(item);
            var sb = new StringBuilder();
            sb.Append(Encode(parts[0].Trim()));

            if (parts.Length > 1)
            {
                sb.Append("<ul>");
                for (int i = 1; i < parts.Length; i++)
                {
                    var p = parts[i].Trim();
                    if (p.Length > 0)
                        sb.Append("<li>").Append(Encode(p)).Append("</li>");
                }
                sb.Append("</ul>");
            }
            return sb.ToString();
        }

        return Encode(item);
    }

    private static string WrapAsBulletsOrParagraph(string text)
    {
        if (text.Contains('•') || text.Contains('·'))
        {
            var parts = BulletSplitRegex.Split(text);
            var sb = new StringBuilder("<ul>");
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length > 0)
                    sb.Append("<li>").Append(Encode(t)).Append("</li>");
            }
            sb.Append("</ul>");
            return sb.ToString();
        }

        return "<p>" + Encode(text) + "</p>";
    }

    // Decode first in case the source already contains HTML entities
    // (e.g. "&amp;" stored literally), then encode once — avoids
    // double-encoding to "&amp;amp;" while still safely escaping raw text.
    private static string Encode(string s) => WebUtility.HtmlEncode(WebUtility.HtmlDecode(s));
}