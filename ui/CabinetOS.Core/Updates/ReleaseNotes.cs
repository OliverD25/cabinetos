using System.Text;
using System.Text.RegularExpressions;

namespace CabinetOS.Core.Updates;

/// <summary>What a block of the release notes is.</summary>
public enum NoteBlockKind
{
    /// <summary>A heading; <see cref="NoteBlock.Level"/> is 1 to 6.</summary>
    Heading,

    /// <summary>A paragraph.</summary>
    Paragraph,

    /// <summary>A list item; <see cref="NoteBlock.Level"/> is 0 for the outer list, 1 under an item, and so on.</summary>
    ListItem,

    /// <summary>A fenced code block, its lines as one span.</summary>
    Code,
}

/// <summary>What a piece of a block's text is.</summary>
public enum NoteSpanKind
{
    Text,
    Bold,
    Italic,
    Code,
    Link,
}

/// <summary>A piece of a block's text; a link carries its address, always <c>https:</c> or <c>http:</c>.</summary>
public sealed record NoteSpan(NoteSpanKind Kind, string Text, string? Url = null);

/// <summary>One block of the release notes, with its spans; a list item's <see cref="Marker"/> is "•" or "3.".</summary>
public sealed record NoteBlock(NoteBlockKind Kind, IReadOnlyList<NoteSpan> Spans, int Level = 0, string Marker = "")
{
    /// <summary>The block's text without its markup.</summary>
    public string PlainText => string.Concat(Spans.Select(s => s.Text));
}

/// <summary>
/// Reads the release notes (a CHANGELOG.md section, docs/ui.md, "Updates") into blocks the window
/// renders natively, without WebView2 (ADR 0014): ATX headings, bullet and numbered lists with
/// nesting, paragraphs, fenced code, and in the text <c>**bold**</c>, <c>*italic*</c>,
/// <c>`code`</c>, <c>[links](…)</c> and <c>&lt;https://…&gt;</c>. Underscores never mark
/// emphasis, so a name such as update_status outside a code span stays as written. Link
/// reference definitions are left out; a link to anything but a web page shows as its text.
/// </summary>
public static partial class ReleaseNotes
{
    /// <summary>The public repository, where the changelog and the documents are.</summary>
    public const string Repository = "https://github.com/OliverD25/cabinetos";

    /// <summary>
    /// What a relative link in a version's notes points into: the repository at the version's
    /// tag (the release script tags every release <c>v&lt;version&gt;</c>), where CHANGELOG.md
    /// sits at the root, so <c>docs/release.md</c> is that version's document.
    /// </summary>
    public static string BaseFor(string version) => $"{Repository}/blob/v{version}/";

    /// <summary>The whole changelog as it stood at the version.</summary>
    public static string ChangelogUrl(string version) => BaseFor(version) + "CHANGELOG.md";

    /// <summary>The blocks of <paramref name="markdown"/>; relative links resolve against <paramref name="baseUrl"/>.</summary>
    public static IReadOnlyList<NoteBlock> Parse(string markdown, string baseUrl)
    {
        var blocks = new List<NoteBlock>();
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var open = new List<string>();
        var openKind = NoteBlockKind.Paragraph;
        var openLevel = 0;
        var openMarker = "";
        // The indent of each list level that is open, outermost first.
        var listIndents = new List<int>();

        void Flush()
        {
            if (open.Count > 0)
            {
                blocks.Add(new NoteBlock(openKind, Inlines(string.Join(' ', open), baseUrl), openLevel, openMarker));
                open.Clear();
            }
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var text = line.TrimStart();
            if (text.Length == 0)
            {
                Flush();
                continue;
            }
            if (text.StartsWith("```", StringComparison.Ordinal) || text.StartsWith("~~~", StringComparison.Ordinal))
            {
                Flush();
                listIndents.Clear();
                var fence = text[..3];
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal); i++)
                {
                    code.Add(lines[i].TrimEnd());
                }
                blocks.Add(new NoteBlock(NoteBlockKind.Code, [new NoteSpan(NoteSpanKind.Code, string.Join('\n', code))]));
                continue;
            }
            if (LinkDefinition().IsMatch(text) || Rule().IsMatch(text))
            {
                Flush();
                continue;
            }
            if (Heading().Match(text) is { Success: true } heading)
            {
                Flush();
                listIndents.Clear();
                blocks.Add(new NoteBlock(NoteBlockKind.Heading, Inlines(heading.Groups[2].Value, baseUrl), heading.Groups[1].Length));
                continue;
            }
            if (ListItem().Match(text) is { Success: true } item)
            {
                Flush();
                var indent = IndentOf(line);
                while (listIndents.Count > 0 && listIndents[^1] > indent)
                {
                    listIndents.RemoveAt(listIndents.Count - 1);
                }
                if (listIndents.Count == 0 || listIndents[^1] < indent)
                {
                    listIndents.Add(indent);
                }
                openKind = NoteBlockKind.ListItem;
                openLevel = listIndents.Count - 1;
                var marker = item.Groups[1].Value;
                openMarker = char.IsDigit(marker[0]) ? marker[..^1] + "." : "•";
                open.Add(item.Groups[2].Value);
                continue;
            }
            if (open.Count == 0)
            {
                // Text after a blank line or a heading starts a paragraph, and ends the lists.
                listIndents.Clear();
                openKind = NoteBlockKind.Paragraph;
                openLevel = 0;
                openMarker = "";
            }
            // Otherwise it continues the open paragraph or list item, indented or not.
            open.Add(text);
        }
        Flush();
        return blocks;
    }

    /// <summary>The spans of one block's text.</summary>
    public static IReadOnlyList<NoteSpan> Inlines(string text, string baseUrl)
    {
        var spans = new List<NoteSpan>();
        var plain = new StringBuilder();

        void Add(NoteSpan span)
        {
            if (plain.Length > 0)
            {
                spans.Add(new NoteSpan(NoteSpanKind.Text, plain.ToString()));
                plain.Clear();
            }
            spans.Add(span);
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '\\' && (char.IsPunctuation(next) || char.IsSymbol(next)))
            {
                plain.Append(next);
                i += 2;
                continue;
            }
            if (c == '`')
            {
                var run = 1;
                while (i + run < text.Length && text[i + run] == '`')
                {
                    run++;
                }
                var fence = new string('`', run);
                var close = text.IndexOf(fence, i + run, StringComparison.Ordinal);
                if (close > 0)
                {
                    var code = text[(i + run)..close];
                    if (code.Length > 2 && code[0] == ' ' && code[^1] == ' ')
                    {
                        code = code[1..^1];
                    }
                    Add(new NoteSpan(NoteSpanKind.Code, code));
                    i = close + run;
                    continue;
                }
                plain.Append(fence);
                i += run;
                continue;
            }
            if (c == '*' && next == '*')
            {
                var close = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    foreach (var inner in Inlines(text[(i + 2)..close], baseUrl))
                    {
                        Add(inner.Kind is NoteSpanKind.Text or NoteSpanKind.Italic ? inner with { Kind = NoteSpanKind.Bold } : inner);
                    }
                    i = close + 2;
                    continue;
                }
            }
            else if (c == '*' && next != '\0' && !char.IsWhiteSpace(next))
            {
                var close = text.IndexOf('*', i + 1);
                if (close > i + 1 && !char.IsWhiteSpace(text[close - 1]))
                {
                    foreach (var inner in Inlines(text[(i + 1)..close], baseUrl))
                    {
                        Add(inner.Kind == NoteSpanKind.Text ? inner with { Kind = NoteSpanKind.Italic } : inner);
                    }
                    i = close + 1;
                    continue;
                }
            }
            else if (c == '[' && LinkAt(text, i) is { } link)
            {
                var label = string.Concat(Inlines(link.Label, baseUrl).Select(s => s.Text));
                if (Resolve(link.Target, baseUrl) is { } url)
                {
                    Add(new NoteSpan(NoteSpanKind.Link, label, url));
                }
                else
                {
                    plain.Append(label);
                }
                i = link.End;
                continue;
            }
            else if (c == '<' && text.IndexOf('>', i + 1) is var end and > 0
                && text[(i + 1)..end] is var address
                && Uri.IsWellFormedUriString(address, UriKind.Absolute)
                && Resolve(address, baseUrl) is { } web)
            {
                Add(new NoteSpan(NoteSpanKind.Link, address, web));
                i = end + 1;
                continue;
            }
            plain.Append(c);
            i++;
        }
        if (plain.Length > 0)
        {
            spans.Add(new NoteSpan(NoteSpanKind.Text, plain.ToString()));
        }
        return spans;
    }

    /// <summary>
    /// The web address a link's target names: an absolute <c>https:</c> or <c>http:</c> address
    /// as it is, a relative one against <paramref name="baseUrl"/>; null for an anchor, another
    /// scheme (a click opens the address without asking), or no target at all.
    /// </summary>
    public static string? Resolve(string target, string baseUrl)
    {
        target = target.Trim();
        if (target.Length == 0 || target[0] == '#')
        {
            return null;
        }
        if (Uri.TryCreate(target, UriKind.Absolute, out var absolute) && !target.StartsWith('/'))
        {
            return IsWeb(absolute) ? absolute.AbsoluteUri : null;
        }
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var root)
            && Uri.TryCreate(root, target, out var relative)
            && IsWeb(relative)
                ? relative.AbsoluteUri
                : null;
    }

    private static bool IsWeb(Uri uri) => uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp;

    // A link [label](target "title") that starts at text[start]: the label may hold brackets in pairs, the target parentheses in pairs.
    private static (string Label, string Target, int End)? LinkAt(string text, int start)
    {
        var depth = 0;
        var close = -1;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == '[')
            {
                depth++;
            }
            else if (text[i] == ']' && --depth == 0)
            {
                close = i;
                break;
            }
        }
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(')
        {
            return null;
        }
        depth = 0;
        for (var i = close + 1; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')' && --depth == 0)
            {
                var target = text[(close + 2)..i].Trim();
                // A title after the address ("…" or '…') is not part of it.
                var space = target.IndexOfAny([' ', '\t']);
                if (space > 0)
                {
                    target = target[..space];
                }
                if (target.StartsWith('<') && target.EndsWith('>'))
                {
                    target = target[1..^1];
                }
                return (text[(start + 1)..close], target, i + 1);
            }
        }
        return null;
    }

    private static int IndentOf(string line)
    {
        var indent = 0;
        foreach (var c in line)
        {
            if (c == ' ')
            {
                indent++;
            }
            else if (c == '\t')
            {
                indent += 4;
            }
            else
            {
                break;
            }
        }
        return indent;
    }

    [GeneratedRegex(@"^(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^([-*+]|\d{1,9}[.)])[ \t]+(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\[[^\]]+\]:[ \t]*\S+")]
    private static partial Regex LinkDefinition();

    [GeneratedRegex(@"^([-*_])([ \t]*\1){2,}[ \t]*$")]
    private static partial Regex Rule();
}
