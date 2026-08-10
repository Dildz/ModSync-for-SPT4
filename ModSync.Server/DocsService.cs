using System.Reflection;
using Markdig;
using SPTarkov.DI.Annotations;

namespace ModSync.Server;

/// <summary>
/// One documentation page: the id used in the URL, the title shown in the nav, and the
/// rendered HTML.
///
/// `record` again (see ModSyncMod.cs) — a plain immutable data carrier, so we get value
/// equality and a short declaration for free. The parameters in brackets are a "primary
/// constructor": they become public properties automatically.
/// </summary>
public record DocPage(string Id, string Title, string Html);

/// <summary>
/// Loads the documentation markdown that is embedded in this assembly and renders it to HTML.
///
/// The markdown lives in Docs/ and is compiled INTO the DLL as an embedded resource (see the
/// EmbeddedResource item in the csproj), rather than shipped as loose files. That means the
/// docs can never be missing, half-updated, or edited into a broken state on a live server —
/// they are the same bytes that passed CI.
///
/// Rendering happens once on first use and is then cached: the markdown cannot change while
/// the server is running, so re-parsing it per page view would be pure waste.
///
/// `[Injectable(InjectionType.Singleton)]` registers this with SPT's DI container as a single
/// shared instance, which is what makes the cache meaningful — a new instance per request
/// would re-render every time.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class DocsService
{
    // Nav order is explicit rather than alphabetical: this is the order the pages are meant
    // to be read in. Ids match the markdown filenames (without .md) and appear in the URL.
    private static readonly (string Id, string Title)[] Pages =
    [
        ("Configuration", "Configuration"),
        ("UsageExamples", "Usage Examples"),
        ("How-Sync-Works", "How Sync Works"),
        ("FAQ", "FAQ"),
    ];

    // Advanced extensions turn on the markdown features the existing docs already use:
    // tables, fenced code blocks, and GitHub-style alerts/callouts.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private readonly Lazy<IReadOnlyList<DocPage>> _pages = new(Render);

    public IReadOnlyList<DocPage> All => _pages.Value;

    public DocPage? Get(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<DocPage> Render()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var rendered = new List<DocPage>();

        foreach (var (id, title) in Pages)
        {
            // Embedded resource names are "<RootNamespace>.<folder>.<file>", so Docs/FAQ.md
            // becomes ModSync.Server.Docs.FAQ.md.
            var resource = $"ModSync.Server.Docs.{id}.md";
            using var stream = assembly.GetManifestResourceStream(resource);

            if (stream is null)
            {
                // A missing page is a packaging mistake, not a user problem. Surface it in the
                // UI rather than throwing, so one bad file cannot take the whole page down.
                rendered.Add(new DocPage(id, title, $"<p><em>Missing documentation resource: {resource}</em></p>"));
                continue;
            }

            using var reader = new StreamReader(stream);
            var markdown = reader.ReadToEnd();
            rendered.Add(new DocPage(id, title, Markdown.ToHtml(markdown, Pipeline)));
        }

        return rendered;
    }
}
