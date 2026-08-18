using System.Net;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// One-time migration helper: converts legacy plain-text "Responsibility"
/// data into HTML suitable for storage/rendering in CKEditor.
///
/// The source text is typed by humans, so the list marker style is not fixed.
/// Detection is adaptive and decides what is actually present in the block and
/// renders the matching HTML:
///   numbered (1. / 1 . / 1) / (1) / 01. / 9 Text)  -> <ol>
///   letter (a. / a) / A. / (A))                    -> <ol type="a|A">
///   roman (i. / ii. / I.)                          -> <ol type="i|I">
///   bullets (• · ▪ ◦ inline; - * – — at line start)-> <ul>
///   otherwise                                      -> <p>
///
/// Candidate markers are validated as a strictly ascending sequence
/// (1,2,3... / a,b,c... / i,ii,iii...) starting at 1/2 before being treated as
/// real list delimiters, so a stray digit/letter inside normal prose does not
/// cause a false split.
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
    private const string BulletGlyphs = "•·▪◦";
    private const string DashGlyphs = "-\u2013\u2014*";

    // Matches a candidate numbered list marker in any of the many human-typed
    // styles: "1." "1 ." "1)" "1 )" "(1)" "01." "10." "9 Text".
    private static readonly Regex NumberedMarkerRegex = new Regex(
        @"(?:(?<=^)|(?<=\s))\(?(\d{1,2})\)?\s*(?:[.)]\s*|(?=[A-Z]))",
        RegexOptions.Compiled);

    // Matches a candidate letter list marker: a. / a) / A. / (A).
    private static readonly Regex LetterMarkerRegex = new Regex(
        @"(?:(?<=^)|(?<=\s))\(?([a-zA-Z])\)?\s*(?:[.)]\s*|(?=[A-Z]))",
        RegexOptions.Compiled);

    // Matches a candidate roman-numeral marker: i. / ii. / I.
    private static readonly Regex RomanMarkerRegex = new Regex(
        @"(?:(?<=^)|(?<=\s))([ivxlcdmIVXLCDM]{1,5})[.)]\s*",
        RegexOptions.Compiled);

    // Inline unambiguous bullet glyphs (used for nesting and run-on bullets).
    private static readonly Regex BulletInlineRegex =
        new Regex(@"\s*[" + BulletGlyphs + @"]\s*", RegexOptions.Compiled);

    // A bullet at the START of a line (after optional leading space), followed by
    // content. Dashes/asterisks are only paid attention to at line start so that
    // mid-sentence hyphens ("well-known", "5-10") are never treated as bullets.
    private static readonly Regex BulletLineRegex = new Regex(
        string.Format(@"^\s*(?:[{0}]|[{1}])\s+", BulletGlyphs, DashGlyphs),
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Dictionary<char, int> RomanMap = new Dictionary<char, int>
    {
        { 'i', 1 }, { 'v', 5 }, { 'x', 10 }, { 'l', 50 }, { 'c', 100 }, { 'd', 500 }, { 'm', 1000 }
    };

    public static string Convert(string raw, out ConversionOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            outcome = ConversionOutcome.Skipped;
            return string.Empty;
        }

        var text = Normalize(raw);
        if (text.Length == 0)
        {
            outcome = ConversionOutcome.Skipped;
            return string.Empty;
        }

        // 1) Numbered list
        var numbered = ExtractSequence(NumberedMarkerRegex, text, m => int.Parse(m.Groups[1].Value));
        if (numbered.Count >= 2)
        {
            outcome = ConversionOutcome.Numbered;
            return RenderSequence(text, numbered, null);
        }

        // 2) Letter list
        var letters = ExtractSequence(LetterMarkerRegex, text,
            m => char.ToUpperInvariant(m.Groups[1].Value[0]) - 'A' + 1);
        if (letters.Count >= 2)
        {
            outcome = ConversionOutcome.Numbered;
            return RenderSequence(text, letters,
                char.IsUpper(letters[0].Token[0]) ? 'A' : 'a');
        }

        // 3) Roman list
        var romans = ExtractSequence(RomanMarkerRegex, text, m => RomanToInt(m.Groups[1].Value));
        if (romans.Count >= 2)
        {
            outcome = ConversionOutcome.Numbered;
            return RenderSequence(text, romans,
                char.IsUpper(romans[0].Token[0]) ? 'I' : 'i');
        }

        // 4) Bullets at the start of lines (dashes/asterisks/glyphs)
        var bulletHtml = TryRenderBulletLines(text);
        if (bulletHtml != null)
        {
            outcome = ConversionOutcome.Bullet;
            return bulletHtml;
        }

        // 5) Inline bullet glyphs (run-on single block)
        if (text.IndexOfAny(BulletGlyphs.ToCharArray()) >= 0)
        {
            outcome = ConversionOutcome.Bullet;
            return WrapAsInlineBulletsOrParagraph(text);
        }

        // 6) Plain paragraph
        outcome = ConversionOutcome.Paragraph;
        return "<p>" + Encode(text) + "</p>";
    }

    /// <summary>
    /// Unifies line endings, replaces non-breaking spaces with plain spaces,
    /// trims each line, drops blank lines, and collapses intra-block whitespace.
    /// Line structure is preserved (needed for line-start bullet detection).
    /// </summary>
    private static string Normalize(string raw)
    {
        var t = raw.Trim().Replace("\u00A0", " ");
        t = Regex.Replace(t, @"\r\n?", "\n");

        var lines = t.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Walks the marker matches and keeps only those forming a strictly ascending
    /// run (N, N+1, N+2, ...) starting at 1 or 2. Non-conforming matches (stray
    /// digits/letters in normal prose) are skipped rather than breaking the run.
    /// </summary>
    private static List<Marker> ExtractSequence(Regex regex, string text, Func<Match, int> valueOf)
    {
        var result = new List<Marker>();
        int expected = -1;

        foreach (Match m in regex.Matches(text))
        {
            int val = valueOf(m);

            if (result.Count == 0)
            {
                if (val == 1 || val == 2)
                {
                    result.Add(new Marker(m.Index, m.Length, val, m.Groups[1].Value));
                    expected = val + 1;
                }
                continue;
            }

            if (val == expected)
            {
                result.Add(new Marker(m.Index, m.Length, val, m.Groups[1].Value));
                expected++;
            }
        }

        return result;
    }

    /// <summary>
    /// Builds an <ol> (with optional "type") from validated markers. Text before
    /// the first marker becomes item 1 (covers a missing "1." label or a lead-in
    /// sentence before the numbering starts).
    /// </summary>
    private static string RenderSequence(string text, List<Marker> markers, char? typeChar)
    {
        var items = new List<string>();

        var preamble = Collapse(text.Substring(0, markers[0].Index));
        if (preamble.Length > 0)
            items.Add(preamble);

        for (int i = 0; i < markers.Count; i++)
        {
            int start = markers[i].Index + markers[i].Length;
            int end = (i + 1 < markers.Count) ? markers[i + 1].Index : text.Length;
            var item = Collapse(text.Substring(start, end - start));
            if (item.Length > 0)
                items.Add(item);
        }

        var sb = new StringBuilder();
        sb.Append(typeChar.HasValue ? $"<ol type=\"{typeChar}\">" : "<ol>");
        foreach (var item in items)
            sb.Append("<li>").Append(BuildItemBody(item)).Append("</li>");
        sb.Append("</ol>");

        return sb.ToString();
    }

    /// <summary>
    /// Attempts to render line-start bullets as a <ul>. Returns null when the
    /// dash/asterisk style is too ambiguous (a single lone dash line, for example)
    /// so it can fall through to paragraph handling.
    /// </summary>
    private static string? TryRenderBulletLines(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).ToArray();
        var flagged = new bool[lines.Length];
        int firstBullet = -1;
        bool anyGlyph = false, anyBullet = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var m = BulletLineRegex.Match(lines[i]);
            if (m.Success)
            {
                flagged[i] = true;
                anyBullet = true;
                firstBullet = firstBullet < 0 ? i : firstBullet;
                char c = lines[i][0];
                anyGlyph = anyGlyph || BulletGlyphs.Contains(c);
            }
        }

        if (!anyBullet)
            return null;

        // Dashes/asterisks alone need more than one to be a believable list.
        if (!anyGlyph && flagged.Count(f => f) < 2)
            return null;

        var sb = new StringBuilder();

        // Lead-in prose before the first bullet becomes its own paragraph.
        var leadLines = lines.Take(firstBullet)
            .Where((_, i) => !flagged[i])
            .Where(l => l.Length > 0)
            .ToArray();
        if (leadLines.Length > 0)
            sb.Append("<p>").Append(Encode(string.Join(" ", leadLines))).Append("</p>");

        sb.Append("<ul>");

        var items = new List<string>();
        var current = string.Empty;

        for (int i = 0; i < lines.Length; i++)
        {
            if (flagged[i])
            {
                if (current.Length > 0)
                    items.Add(current);
                current = lines[i].Substring(BulletLineRegex.Match(lines[i]).Length).Trim();
            }
            else if (i > firstBullet && current.Length > 0)
            {
                // Continuation of the previous item.
                current += " " + lines[i].Trim();
            }
        }
        if (current.Length > 0)
            items.Add(current);

        foreach (var item in items.Where(s => s.Length > 0))
            sb.Append("<li>").Append(BuildItemBody(item)).Append("</li>");

        sb.Append("</ul>");
        return sb.ToString();
    }

    private static string WrapAsInlineBulletsOrParagraph(string text)
    {
        var parts = BulletInlineRegex.Split(text);
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

    private static string BuildItemBody(string item)
    {
        if (item.IndexOfAny(BulletGlyphs.ToCharArray()) >= 0)
        {
            var parts = BulletInlineRegex.Split(item);
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

    // Collapses embedded newlines/spaces in a segment to single spaces.
    private static string Collapse(string s) => Regex.Replace(s.Trim(), @"\s+", " ");

    private static int RomanToInt(string s)
    {
        int total = 0, prev = 0;
        foreach (char c in s.ToLowerInvariant())
        {
            if (!RomanMap.TryGetValue(c, out int v) || v == 0)
                return int.MinValue;
            total += (prev < v && prev != 0) ? v - 2 * prev : v;
            prev = v;
        }
        return total;
    }

    // Decode first in case the source already contains HTML entities
    // (e.g. "&amp;" stored literally), then encode once — avoids
    // double-encoding to "&amp;amp;" while still safely escaping raw text.
    private static string Encode(string s) => WebUtility.HtmlEncode(WebUtility.HtmlDecode(s));

    private readonly struct Marker
    {
        public readonly int Index;
        public readonly int Length;
        public readonly int Value;
        public readonly string Token;

        public Marker(int index, int length, int value, string token)
        {
            Index = index;
            Length = length;
            Value = value;
            Token = token;
        }
    }
}