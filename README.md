# AI Video Generator CLI for Ads, Reels & Website Tutorials

**Vanity Studio is an AI-assisted video creation CLI for Windows, macOS, and Linux.** Describe an ad, reel, product demo, or website tutorial in plain language; it writes a script, gathers or creates media, and renders an editable MP4 on your machine through [Vanity Studio](https://photovideoeditor.com/app/).

It is built for creators and small teams who want to make promotional videos from product pages, websites, and local media without assembling every scene by hand. It is a .NET 8 command-line app and supports multiple AI providers for scripts, voice, images, and clips.

For example, ask it to make:

- "a 20 second Facebook ad for this product page"
- "a tutorial on how to order on my shop"
- "a reel from the photos on my desktop"

It reads the site, finds the pictures, writes the video's script, and renders a finished MP4 on your machine with
[Vanity Studio](https://photovideoeditor.com/app/) (photovideoeditor.com) in a headless browser.

Every video comes back with its contact sheet, a PNG banner of its last frame, a report of every scene, and the
**editable project**. From there you can change one line, swap the music, retime a clip, or open the project in the
Studio's editor in your browser.

```
You: make a facebook ad about https://topdirect.ro/windows-11-pro-retail-licenta-electronica

  → web  read https://topdirect.ro/windows-11-pro-retail-licenta-electronica
  → web  download (2 product pictures)
  → make_video  blocks
  → make_video  submit
Vanity: Video job #7 queued: 6 scenes, about 18 s, portrait 4:5 for the Facebook feed.
  [video #7] voice 6/6 spoken
  [video #7] render 60%
  ✓ The video "Windows 11 Pro - licență originală" is ready (job #7).
  The video: videos/7-windows-11-pro/windows-11-pro.mp4
```

Made by **Five Quantum Bits** ([x5qubits](https://github.com/x5qubits)).

## What it makes

- **Ads, reels, promos, logo reveals and presentation videos.** The model picks and fills ready-made Studio scenes:
  hooks, picture heroes, offers with a price and a button, stat count-ups, steps, logo stings, calls to action. The
  Studio times, animates, scores and renders them. One ad can come back as several takes for an A/B test.
- **Website tutorials with real screens.** It opens the site in a browser and clicks through it like a visitor. Every
  step of the video is a capture of the real page: the camera glides to the button, a cursor taps it, and the
  instruction is spoken. This works behind a login too.
- **Videos about a product page.** `web read` takes the name, price, offer, published product data, text and pictures
  from the page, so the facts and the shots in the video are the real ones.
- **Your own files, anywhere.** Pictures, clips and sounds from the project, your Desktop, Downloads, Pictures or
  Videos folder, by name or by full path.
- **AI media when you have none.** Voice-over (Gemini, OpenAI or Alibaba Qwen speech), AI stills in the video's shape
  (OpenAI, Antigravity/Gemini, Alibaba Wan), and AI clips made from a picture (Alibaba Wan image-to-video).
- **A real editor.**
  - `remix` changes a finished video (music, a line, a picture, the treatment, speed, quality) and reuses everything
    that did not change.
  - `edit_video` works on the Studio's own project document: clips, layers, keyframes, transitions, kinetic text,
    3D, captions and audio, with frame and contact-sheet previews before you render.
- **Background jobs.** Renders run two at a time while you keep talking, and unfinished jobs resume the next time you
  start it in that folder.

## Install

You need Windows, macOS or Linux, the [.NET 8 SDK](https://dotnet.microsoft.com/download), and Google Chrome or
Microsoft Edge. `ffprobe` (from ffmpeg) is optional: it measures voice lines exactly.

```bash
git clone https://github.com/x5qubits/vanitystudio.git
cd vanitystudio
dotnet publish -c Release -o out
./out/vanity-studio --help          # Windows: out\vanity-studio.exe
```

Put `out/` on your PATH to run `vanity-studio` from any folder.

## First run

```bash
vanity-studio --login antigravity   # or openai (ChatGPT), grok, anthropic; API keys: /key in the chat
vanity-studio doctor                # checks the browser, the Studio, ffprobe and which profile does what
```

The model you sign in with writes the scripts. Voice and AI clips need an **API key**, because subscription logins
cannot reach the speech and video endpoints.

| Role | Used for | Providers | Set up |
|---|---|---|---|
| scripts | writing and editing the videos | any profile: Antigravity, ChatGPT, Grok, Anthropic, Gemini, OpenAI, DeepSeek, Mistral, Ollama... | `--login`, or `/key` |
| voice | `"voice": true`, `edit_video speak` | Gemini key (free tier at aistudio.google.com), OpenAI key, Alibaba DashScope key | `/key gemini`, then `/voice gemini` |
| stills | `{"make": "still"}` | OpenAI key or ChatGPT login, Antigravity login, Alibaba key | automatic; `/images <profile>` to prefer one |
| clips | `{"make": "clip"}` | Alibaba DashScope key (Wan image-to-video) | `/key alibaba`, then `/clips alibaba` |

`/roles` shows which profile currently does what.

## Use it

```bash
cd my-shop-videos
vanity-studio                                          # the chat
vanity-studio "a 15 s reel for my bakery, use the photos on my desktop"
vanity-studio "make a video on how to order on https://topdirect.ro/"
```

In the chat:

```
You: make a facebook ad about https://example.com/product, 3 takes
You: swap the music on the last one for something calmer
You: make the headline of scene 2 bigger and add a fade between scenes 3 and 4
You: /jobs     /open 7     /edit 7     /folder 7
```

Without a model:

```bash
vanity-studio render script.json            # a video script, or a Studio project (.vstudio.json)
vanity-studio read https://example.com/p    # what a page says: title, prices, product data, text, pictures
vanity-studio site https://example.com --click "Products > Add to cart" --format reel
vanity-studio blocks                        # the Studio's scene blocks
vanity-studio docs                          # the Studio's project format reference
vanity-studio tool edit_video '{"action":"open","source":"7"}'
```

The full guide is **[docs/USAGE.md](docs/USAGE.md)**: every command, the project folder, the script format,
editing, personas and skills, settings, and troubleshooting.

## A project folder

```
my-shop-videos/
  media/                 your pictures, clips and logo (media/web/: downloaded from sites, media/made/: AI-made)
  videos/7-<title>/      each finished video: .mp4, -sheet.jpg, -banner.png, .vstudio.json, report.json, script.json
  edits/<name>/          Studio projects being edited (doc.json, media/, looks/)
  .vanity-studio/        brand.json, brand.md, instructions.md, personas/, skills/, jobs/, memory.json
```

`/init` sets a folder up. `brand.json` and `brand.md` tell every video who it speaks for.

## How it works

1. The model reads the request and the brand, reads any site with `web read` or `make_video site`, and lists the
   scenes.
2. It writes a **script**: ready-made Studio blocks with their lines, parameters and files. `make_video submit`
   checks it against the live block catalog (every text limit, file and parameter) and queues a job.
3. The job runner speaks the lines, makes the AI pictures and clips, and hands everything to the renderer.
4. The renderer starts a private headless Chrome on the live Studio's origin.
   - It serves the job page and the job's files there through request interception, so nothing is uploaded and the
     Studio itself is never changed.
   - The Studio's own director compiles the script into a Studio project and renders it.
   - Website steps are captured in the same browser first.
5. The MP4, the contact sheet, the banner, the project and the report land in `videos/`. The result is printed and
   handed to the conversation.

Edits go through the same renderer: `edit_video` validates, previews and renders the Studio's project document with
the Studio's own video API.

## Privacy

- Keys and logins stay in `~/.vanity-studio/config.json` on your machine.
- Your files go to an AI provider only when a job needs it: a line to speak, or a picture to start an AI clip from.
- Rendering happens on your machine.
- `VANITY_STUDIO_PROMPT_LOG=0` turns off the local prompt log.

## Frequently asked questions

### What is Vanity Studio CLI?

Vanity Studio CLI is an AI-assisted video maker that turns plain-language requests, product pages, websites, and local media into editable promotional videos and tutorials.

### What kinds of videos can it create?

It can create product ads, social reels, promos, product demos, logo reveals, presentations, and narrated website walkthroughs. Website tutorials use real browser captures of the site.

### Does it render videos locally?

Yes. The CLI runs a headless browser on your machine and saves the MP4 and editable project in your project folder. It uses the separate Vanity Studio web app as its renderer.

### Which operating systems and AI providers are supported?

The CLI supports Windows, macOS, and Linux with .NET 8 and Chrome or Edge. Script providers include OpenAI, Anthropic, Gemini, Grok, DeepSeek, Mistral, and Ollama; voice, image, and video providers depend on the configured API keys.

## License

No license file is included yet; add one before relying on this project. Vanity Studio (photovideoeditor.com) is a
separate service; this CLI drives its public web app.
