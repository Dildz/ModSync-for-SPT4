using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using SPTarkov.DI.Annotations;

namespace ModSync.Server;

/// <summary>
/// One documentation page: the id used in the URL, the title shown in the nav, the nav group it
/// belongs under, and the rendered HTML.
///
/// `record` again (see ModSyncMod.cs) - a plain immutable data carrier, so we get value
/// equality and a short declaration for free. The parameters in brackets are a "primary
/// constructor": they become public properties automatically.
/// </summary>
public record DocPage(string Id, string Title, string Group, string Html);

/// <summary>
/// Loads the documentation markdown that is embedded in this assembly and renders it to HTML.
///
/// The markdown lives in Docs/ and is compiled INTO the DLL as an embedded resource (see the
/// EmbeddedResource item in the csproj), rather than shipped as loose files. That means the
/// docs can never be missing, half-updated, or edited into a broken state on a live server -
/// they are the same bytes that passed CI.
///
/// **One source file can produce several pages.** Configuration.md is 5,000+ words, which is a
/// lot of scrolling to find one option. Rather than cutting the markdown into more files - which
/// would break the one-file-per-wiki-page arrangement that lets the wiki and this UI share a
/// single source - the file is split HERE, at its own `##` headings, into the pages declared in
/// <see cref="Specs"/>. The wiki keeps its long-form page; the UI gets short tabs.
///
/// Rendering happens once on first use and is then cached: the markdown cannot change while
/// the server is running, so re-parsing it per page view would be pure waste.
///
/// `[Injectable(InjectionType.Singleton)]` registers this with SPT's DI container as a single
/// shared instance, which is what makes the cache meaningful - a new instance per request
/// would re-render every time.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class DocsService
{
    /// <summary>
    /// A page carved out of a source document.
    ///
    /// <paramref name="StartHeading"/> is the slug of the `##` heading this page BEGINS at, and it
    /// runs until the next spec's start. Declaring only the boundaries (rather than listing every
    /// section) means a new heading added to the wiki automatically lands in whichever page it
    /// follows, instead of silently disappearing from the UI. A null start means "from the top of
    /// the file", which is how the intro above the first heading gets a home.
    /// </summary>
    private record PageSpec(string Id, string Title, string Group, string Source, string? StartHeading);

    // Nav order is explicit rather than alphabetical: this is the order the pages are meant to be
    // read in. Ids appear in the URL, so keep them stable - they are what deep links point at.
    private static readonly PageSpec[] Specs =
    [
        new("Configuration",       "Overview",                "Configuration",  "Configuration",  null),
        new("SyncPaths",           "syncPaths",               "Configuration",  "Configuration",  "syncpaths"),
        new("BaseFiles",           "Base-game files",         "Configuration",  "Configuration",  "mods-that-replace-base-game-files"),
        new("Exclusions",          "exclusions",              "Configuration",  "Configuration",  "exclusions"),
        new("Headless",            "Headless and Managed",    "Configuration",  "Configuration",  "headlessincludes"),
        new("ClientSettings",      "Client-side settings",    "Configuration",  "Configuration",  "bepinex-configuration-manager"),

        new("How-Sync-Works",      "A sync run",              "How sync works", "How-Sync-Works", null),
        new("Applying-Changes",    "Applying changes",        "How sync works", "How-Sync-Works", "downloading-updates"),
        new("Sync-Special-Cases",  "Special cases",           "How sync works", "How-Sync-Works", "enforced-sync-paths"),

        new("UsageExamples",       "Usage Examples",          "Examples",       "UsageExamples",  null),
        new("FAQ",                 "FAQ",                     "Reference",      "FAQ",           null),
    ];

    // Advanced extensions turn on the markdown features the existing docs already use:
    // tables, fenced code blocks, and GitHub-style alerts/callouts.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private readonly Lazy<IReadOnlyList<DocPage>> _pages = new(Render);

    public IReadOnlyList<DocPage> All => _pages.Value;

    /// <summary>Pages in nav order, grouped by their <see cref="DocPage.Group"/>, groups in first-seen order.</summary>
    public IEnumerable<IGrouping<string, DocPage>> ByGroup => All.GroupBy(p => p.Group);

    public DocPage? Get(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// GitHub's heading slug rules, near enough for our own documents: lowercase, drop anything
    /// that is not a letter, digit, space or dash, then spaces to dashes. Backticks in a heading
    /// like `` `syncPaths` `` simply fall away, which is why the spec above says "syncpaths".
    /// </summary>
    private static string Slug(string heading)
    {
        var sb = new StringBuilder(heading.Length);
        foreach (var c in heading.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c is ' ' or '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    /// <summary>
    /// Split a document at its `##` headings, in file order.
    ///
    /// Fenced code blocks are tracked and skipped: our configs are JSONC, and a `#` inside a fence
    /// is content, not a heading. Splitting on a naive line prefix would cut a page in half there.
    /// </summary>
    private static List<(string Slug, string Text)> SplitSections(string markdown)
    {
        var sections = new List<(string, string)>();
        var current = new StringBuilder();
        var slug = string.Empty;
        var inFence = false;

        foreach (var line in markdown.Split('\n'))
        {
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
                inFence = !inFence;

            if (!inFence && trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                sections.Add((slug, current.ToString()));
                current.Clear();
                slug = Slug(trimmed[3..]);
            }

            current.Append(line).Append('\n');
        }

        sections.Add((slug, current.ToString()));
        return sections;
    }

    private static IReadOnlyList<DocPage> Render()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var sources = new Dictionary<string, List<(string Slug, string Text)>>(StringComparer.OrdinalIgnoreCase);
        var rendered = new List<DocPage>();

        foreach (var spec in Specs)
        {
            if (!sources.TryGetValue(spec.Source, out var sections))
            {
                // Embedded resource names are "<RootNamespace>.<folder>.<file>", so Docs/FAQ.md
                // becomes ModSync.Server.Docs.FAQ.md.
                var resource = $"ModSync.Server.Docs.{spec.Source}.md";
                using var stream = assembly.GetManifestResourceStream(resource);

                if (stream is null)
                {
                    // A missing page is a packaging mistake, not a user problem. Surface it in the
                    // UI rather than throwing, so one bad file cannot take the whole page down.
                    rendered.Add(new DocPage(spec.Id, spec.Title, spec.Group,
                        $"<p><em>Missing documentation resource: {resource}</em></p>"));
                    continue;
                }

                using var reader = new StreamReader(stream);
                sections = SplitSections(reader.ReadToEnd());
                sources[spec.Source] = sections;
            }

            var markdown = SliceFor(spec, sections, out var missingHeading);

            if (missingHeading is not null)
            {
                // The wiki renamed or removed a heading this split is anchored to. Say so in the UI
                // rather than quietly serving a page that merged into its neighbour.
                markdown = $"> **This page could not be located in the source document.** It starts at the "
                         + $"`{missingHeading}` heading, which no longer exists in `{spec.Source}.md`. "
                         + $"The section list in `DocsService` needs updating.\n\n" + markdown;
            }

            rendered.Add(new DocPage(spec.Id, spec.Title, spec.Group, Markdown.ToHtml(markdown, Pipeline)));
        }

        return RewriteInternalLinks(rendered);
    }

    /// <summary>The markdown for one spec: from its start heading up to the next spec's start in the same source.</summary>
    private static string SliceFor(PageSpec spec, List<(string Slug, string Text)> sections, out string? missingHeading)
    {
        missingHeading = null;

        var start = spec.StartHeading is null
            ? 0
            : sections.FindIndex(s => s.Slug == spec.StartHeading);

        if (start < 0)
        {
            missingHeading = spec.StartHeading;
            return string.Empty;
        }

        // The next spec drawing from the same file marks where this page stops.
        var nextStart = Specs
            .Where(s => s.Source == spec.Source && s.StartHeading is not null)
            .Select(s => sections.FindIndex(sec => sec.Slug == s.StartHeading))
            .Where(i => i > start)
            .DefaultIfEmpty(sections.Count)
            .Min();

        var sb = new StringBuilder();
        for (var i = start; i < nextStart; i++) sb.Append(sections[i].Text);
        return sb.ToString();
    }

    /// <summary>
    /// Point the documents' own links at this UI's routes.
    ///
    /// Two kinds are broken without this, and splitting documents made the second kind worse:
    /// wiki-relative links (<c>href="UsageExamples"</c>) were written for GitHub's wiki and resolve
    /// to nothing here, and same-page anchors (<c>href="#syncpaths"</c>) may now live on a different
    /// page than the link that points at them. Both are rewritten to <c>/modsync/{id}</c>.
    /// </summary>
    private static IReadOnlyList<DocPage> RewriteInternalLinks(List<DocPage> pages)
    {
        // Which page did each heading slug end up on? Built from the rendered HTML so it reflects
        // the ids Markdig actually emitted, rather than a second guess at its slug rules.
        var pageForAnchor = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages)
            foreach (Match m in Regex.Matches(page.Html, "<h[1-6][^>]*\\bid=\"([^\"]+)\""))
                pageForAnchor.TryAdd(m.Groups[1].Value, page.Id);

        // A wiki link may name a source document ("How-Sync-Works") that is now several pages; it
        // resolves to the first page carved from that document.
        var pageForSource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in Specs) pageForSource.TryAdd(spec.Source, spec.Id);

        return pages.ConvertAll(page => page with
        {
            Html = Regex.Replace(page.Html, "href=\"([^\"]+)\"", match =>
            {
                var href = match.Groups[1].Value;

                if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || href.StartsWith("/modsync", StringComparison.OrdinalIgnoreCase))
                    return match.Value;

                if (href.StartsWith('#'))
                    return pageForAnchor.TryGetValue(href[1..], out var anchorPage)
                        ? $"href=\"/modsync/{anchorPage}{href}\""
                        : match.Value;

                // "./How-Sync-Works", "UsageExamples", "Configuration#syncpaths"
                var trimmed = href.TrimStart('.', '/');
                var hashAt = trimmed.IndexOf('#');
                var docName = hashAt < 0 ? trimmed : trimmed[..hashAt];
                var fragment = hashAt < 0 ? string.Empty : trimmed[hashAt..];

                if (fragment.Length > 1 && pageForAnchor.TryGetValue(fragment[1..], out var targetPage))
                    return $"href=\"/modsync/{targetPage}{fragment}\"";

                return pageForSource.TryGetValue(docName, out var docPage)
                    ? $"href=\"/modsync/{docPage}{fragment}\""
                    : match.Value;
            }),
        });
    }
}
