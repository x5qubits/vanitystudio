using System.Text;
using VanityStudio.Infra;

namespace VanityStudio.Agent;

/// <summary>A persona: a markdown file with YAML frontmatter whose body replaces the agent's identity. Frontmatter:
/// <c>name</c>, <c>description</c>, <c>tools</c> (the only tools it may use), <c>skills</c> (loaded up front),
/// <c>max_turns</c>.</summary>
public sealed class PersonaDefinition
{
    public string   Name         { get; init; } = "";
    public string   Description  { get; init; } = "";
    public string[] Tools        { get; init; } = [];
    public string[] Skills       { get; init; } = [];
    public int      MaxTurns     { get; init; }
    public string   SystemPrompt { get; init; } = "";
    public string   Source       { get; init; } = "";
    public IReadOnlyDictionary<string, string> Extra { get; init; } = new Dictionary<string, string>();
}

/// <summary>A skill: a playbook the model can load on demand (<c>skill_view</c>), or that a persona pins. Frontmatter:
/// <c>name</c>, <c>description</c> (what the catalog shows), <c>always: true</c> to inline it on every turn.</summary>
public sealed class SkillDefinition
{
    public string Name        { get; init; } = "";
    public string Description { get; init; } = "";
    public string Playbook    { get; init; } = "";
    public bool   Always      { get; init; }
    public string Source      { get; init; } = "";
    public IReadOnlyDictionary<string, string> Extra { get; init; } = new Dictionary<string, string>();
}

/// <summary>Minimal YAML frontmatter: <c>key: value</c> lines between two <c>---</c> fences; arrays as
/// <c>[a, b]</c>, <c>a, b</c> or a block of <c>- item</c> lines.</summary>
public static class FrontmatterParser
{
    public static (Dictionary<string, string> Meta, string Body) Parse(string text)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        text = text.Replace("\r\n", "\n").TrimStart('﻿');
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) return (meta, text.Trim());
        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return (meta, text.Trim());
        var header = text[4..end];
        var body = text[(end + 4)..];
        if (body.StartsWith('\n')) body = body[1..];

        string? key = null; var block = new List<string>();
        void Flush() { if (key is not null && block.Count > 0) meta[key] = "[" + string.Join(", ", block) + "]"; block.Clear(); }
        foreach (var raw in header.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().StartsWith("- ") && key is not null && (meta.TryGetValue(key, out var cur) ? cur.Length == 0 : true))
            { block.Add(line.Trim()[2..].Trim().Trim('"', '\'')); continue; }
            Flush();
            var colon = line.IndexOf(':');
            if (colon <= 0 || line.TrimStart().StartsWith('#')) { key = null; continue; }
            key = line[..colon].Trim();
            meta[key] = line[(colon + 1)..].Trim().Trim('"', '\'');
        }
        Flush();
        return (meta, body.Trim());
    }

    public static string[] ParseArray(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var s = raw.Trim();
        if (s.StartsWith('[') && s.EndsWith(']')) s = s[1..^1];
        return s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.Trim('"', '\'')).Where(x => x.Length > 0).ToArray();
    }
}

/// <summary>The personas and skills available to a workspace: the global ones under the agent home and the project's
/// own under <c>&lt;workspace&gt;/.vanity-studio</c>, project files overriding global ones by name.</summary>
public sealed class PromptLibrary
{
    public const string ProjectFolder = ".vanity-studio";

    public IReadOnlyList<PersonaDefinition> Personas { get; }
    public IReadOnlyList<SkillDefinition>   Skills   { get; }
    public string Workspace { get; }

    private PromptLibrary(string workspace, List<PersonaDefinition> personas, List<SkillDefinition> skills)
    { Workspace = workspace; Personas = personas; Skills = skills; }

    public static string ProjectDir(string workspace) => Path.Combine(workspace, ProjectFolder);
    public static bool HasProjectDir(string workspace) => Directory.Exists(ProjectDir(workspace));

    public static PromptLibrary Load(string workspace)
    {
        var personas = new Dictionary<string, PersonaDefinition>(StringComparer.OrdinalIgnoreCase);
        var skills   = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { AgentConfig.Dir, ProjectDir(workspace) })   // project last: it wins
        {
            foreach (var p in LoadPersonas(Path.Combine(root, "personas"))) personas[p.Name] = p;
            foreach (var s in LoadSkills(Path.Combine(root, "skills")))     skills[s.Name]   = s;
        }
        return new PromptLibrary(workspace,
            personas.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            skills.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public PersonaDefinition? Persona(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : Personas.FirstOrDefault(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    public SkillDefinition? Skill(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : Skills.FirstOrDefault(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<PersonaDefinition> LoadPersonas(string dir)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories).OrderBy(f => f))
        {
            PersonaDefinition? p = null;
            try
            {
                var (meta, body) = FrontmatterParser.Parse(File.ReadAllText(file));
                var name = meta.TryGetValue("name", out var n) && n.Length > 0 ? n : Path.GetFileNameWithoutExtension(file);
                p = new PersonaDefinition
                {
                    Name = name,
                    Description = meta.TryGetValue("description", out var d) ? d : "",
                    Tools = FrontmatterParser.ParseArray(meta.TryGetValue("tools", out var t) ? t : null),
                    Skills = FrontmatterParser.ParseArray(meta.TryGetValue("skills", out var s) ? s : null),
                    MaxTurns = meta.TryGetValue("max_turns", out var m) && int.TryParse(m, out var mt) ? mt
                             : meta.TryGetValue("max_iterations", out var m2) && int.TryParse(m2, out var mt2) ? mt2 : 0,
                    SystemPrompt = body,
                    Source = file,
                    Extra = meta,
                };
            }
            catch (Exception ex) { Log.Warn($"[personas] {file}: {ex.Message}"); }
            if (p is not null) yield return p;
        }
    }

    private static IEnumerable<SkillDefinition> LoadSkills(string dir)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories).OrderBy(f => f))
        {
            SkillDefinition? s = null;
            try
            {
                var (meta, body) = FrontmatterParser.Parse(File.ReadAllText(file));
                var name = meta.TryGetValue("name", out var n) && n.Length > 0 ? n : Path.GetFileNameWithoutExtension(file);
                s = new SkillDefinition
                {
                    Name = name,
                    Description = meta.TryGetValue("description", out var d) ? d : body.Split('\n')[0].TrimStart('#', ' '),
                    Playbook = body,
                    Always = meta.TryGetValue("always", out var a) && (a.Equals("true", StringComparison.OrdinalIgnoreCase) || a == "1"),
                    Source = file,
                    Extra = meta,
                };
            }
            catch (Exception ex) { Log.Warn($"[skills] {file}: {ex.Message}"); }
            if (s is not null) yield return s;
        }
    }

    // ── scaffolding ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Sets a folder up as a video project: <c>&lt;workspace&gt;/.vanity-studio</c> with the instructions file,
    /// the brand (brand.json, brand.md), example personas and skills, and an empty <c>media/</c> for the operator's
    /// pictures and clips. Existing files are left alone. Returns what was created.</summary>
    public static string Scaffold(string workspace)
    {
        var dir = ProjectDir(workspace);
        var created = new List<string>();
        void Put(string rel, string content)
        {
            var path = Path.Combine(dir, rel);
            if (File.Exists(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content.Replace("\r\n", "\n"), new UTF8Encoding(false));
            created.Add(rel.Replace('\\', '/'));
        }
        Put("instructions.md", InstructionsTemplate);
        Put("brand.json", BrandJsonTemplate);
        Put("brand.md", BrandMdTemplate);
        Put(Path.Combine("personas", "ads.md"), AdsPersona);
        Put(Path.Combine("personas", "tutorials.md"), TutorialsPersona);
        Put(Path.Combine("skills", "scroll-stopping-hooks.md"), HooksSkill);
        Put(Path.Combine("skills", "logo-reveal.md"), LogoRevealSkill);
        Put(Path.Combine("skills", "footage-cut.md"), FootageCutSkill);
        var media = Path.Combine(workspace, "media");
        if (!Directory.Exists(media)) { Directory.CreateDirectory(media); created.Add("../media/"); }
        return created.Count == 0
            ? $"{ProjectFolder}/ already has every example file; nothing created."
            : $"created in {ProjectFolder}/: " + string.Join(", ", created) + ". Fill in brand.json and brand.md (or tell Vanity about the brand), put your logo and pictures in media/.";
    }

    /// <summary>The example personas and skills under the studio home, so every folder has them.</summary>
    public static string ScaffoldGlobal()
    {
        var created = new List<string>();
        void Put(string rel, string content)
        {
            var path = Path.Combine(AgentConfig.Dir, rel);
            if (File.Exists(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content.Replace("\r\n", "\n"), new UTF8Encoding(false));
            created.Add(rel.Replace('\\', '/'));
        }
        Put(Path.Combine("personas", "ads.md"), AdsPersona);
        Put(Path.Combine("skills", "scroll-stopping-hooks.md"), HooksSkill);
        Put(Path.Combine("skills", "logo-reveal.md"), LogoRevealSkill);
        Put(Path.Combine("skills", "footage-cut.md"), FootageCutSkill);
        return created.Count == 0 ? "nothing created." : "created: " + string.Join(", ", created);
    }

    private const string InstructionsTemplate = """
        # Project instructions

        Read by Vanity Studio on every turn. Keep it short and factual: what these videos are for, where they are
        posted, what every video must or must not do.

        ## Videos here

        - for: (Instagram reels / YouTube / the website / ads)
        - format: (reel unless said otherwise)
        - language: (en)

        ## Always

        - end on the offer or the site
        - ...

        ## Never

        - ...
        """;

    private const string BrandJsonTemplate = """
        {
          "name": "",
          "url": "",
          "logo": "media/logo.png",
          "colors": [],
          "language": "en"
        }
        """;

    private const string BrandMdTemplate = """
        # The brand

        ## What it sells

        - (the product or service, in one line)
        - (prices and offers that may be named in a video)

        ## Who buys it

        - (who watches the videos: their situation, their pain, their wish)

        ## Voice

        - (how it speaks: plain / playful / premium / ...)
        - words to use:
        - words to avoid:
        """;

    private const string AdsPersona = """
        ---
        name: ads
        description: A direct-response ad maker - short reels that sell one offer, with A/B takes.
        skills: [scroll-stopping-hooks]
        max_turns: 60
        ---
        You make performance ads: 15 to 25 second reels that sell ONE offer to ONE kind of viewer. Before any script you
        name the viewer, their pain or wish, and the change the offer brings, from the brand. The first scene shows the
        viewer's own situation in at most five big words; every scene after it shows a picture of what it says; the last
        scene is the offer with its button. You render two or three takes when the operator will test them, and you
        never invent a price, a discount or a claim the brand does not state.
        """;

    private const string TutorialsPersona = """
        ---
        name: tutorials
        description: A tutorial maker - how-to videos of a website or an app, step by step, with real screens.
        max_turns: 80
        ---
        You make tutorials. You never guess a step: you open the site with make_video action=site, click through it
        exactly as a viewer would, and every step of the video is a screen-demo scene with the url, the clicks and the
        target words that site listed. One step per scene, the line says what to do in the operator's language, voice
        on. When the steps sit behind a login, the operator logs in once with /site-login and the steps use login=true.
        """;

    private const string HooksSkill = """
        ---
        name: scroll-stopping-hooks
        description: The first two seconds of a reel or an ad - openings that stop the scroll.
        ---
        # Scroll-stopping hooks

        The first scene decides whether anyone sees the second. Pick one:

        1. The viewer's situation, in their words: "Still *invoicing* by hand?" over a picture of exactly that.
        2. The result first: the finished thing (the clean room, the new site, the plated dish), then how.
        3. A number that surprises: "*3* minutes, not 3 days." The figure is the biggest thing on screen.
        4. A contrast: before and after in one frame (picture-split or compare blocks).
        5. A direct question the viewer answers "yes" to in their head.

        Rules: at most five words on screen, one *marked* word, a picture (never a plain card) under it, and no logo in
        the first scene; the logo belongs to the last.
        """;

    private const string LogoRevealSkill = """
        ---
        name: logo-reveal
        description: A 3 to 6 second logo reveal or intro / outro sting.
        ---
        # Logo reveal

        - One or two scenes, no voice, music on (a short bed or auto), format as asked (square for a profile, landscape
          for a YouTube intro, reel for stories).
        - The brand's logo is media:logo; check it exists with brand read first. Without a logo, the brand's name in
          the brand's colours is the reveal.
        - A line only when the request names a tagline; at most six words.
        - Offer two takes when the operator has not chosen a style.
        """;

    private const string FootageCutSkill = """
        ---
        name: footage-cut
        description: A video cut from the operator's own clips - trims, order, titles, music - with edit_video.
        ---
        # Cutting the operator's footage

        1. Find the clips: files list folder=videos (or desktop, downloads), or files find; files info gives each
           clip's length. Import the ones you use into media/ when they live elsewhere.
        2. Read edit_video action=docs once: it says how a clip names its file, where it starts in the source, how long
           it plays, its transition, the text layers and the music.
        3. Save a doc (edit_video action=save) with one clip per moment the operator wants, in order, titles as text
           layers, music when asked. Keep every text short.
        4. edit_video validate, then sheet to see the whole cut and frame at the moments that matter; fix and look again.
        5. edit_video render; report the job number. Later changes are patches to the same doc, then render again.
        """;
}
