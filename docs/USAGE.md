# Vanity Studio CLI: the guide

- [1. Install and sign in](#1-install-and-sign-in)
- [2. Who does what: the roles](#2-who-does-what-the-roles)
- [3. A project folder](#3-a-project-folder)
- [4. Asking for videos](#4-asking-for-videos)
- [5. Commands](#5-commands)
- [6. The tools the model uses](#6-the-tools-the-model-uses)
- [7. The video script](#7-the-video-script)
- [8. Editing a video](#8-editing-a-video)
- [9. Jobs and what they produce](#9-jobs-and-what-they-produce)
- [10. Brand, instructions, personas, skills, memory](#10-brand-instructions-personas-skills-memory)
- [11. Settings and environment](#11-settings-and-environment)
- [12. Troubleshooting](#12-troubleshooting)

## 1. Install and sign in

```bash
git clone https://github.com/x5qubits/vanitystudio.git
cd vanitystudio
dotnet publish -c Release -o out          # needs the .NET 8 SDK
```

Run `out/vanity-studio` (Windows: `out\vanity-studio.exe`), or put `out/` on your PATH. You also need Google Chrome
or Microsoft Edge: the videos render in it. `ffprobe` (part of ffmpeg) is optional.

Sign in once:

```bash
vanity-studio --login antigravity    # Google (Antigravity): a browser window opens; also draws AI stills
vanity-studio --login openai         # ChatGPT subscription (device code); also draws AI stills
vanity-studio --login grok           # xAI subscription (device code)
vanity-studio --login anthropic      # paste an Anthropic API key or token
```

API keys are added inside the chat with `/key` (OpenAI, Anthropic, Gemini, Grok, DeepSeek, Mistral, Groq,
OpenRouter, Perplexity, Alibaba DashScope, Ollama, or any OpenAI-compatible URL). Running `vanity-studio` with no
profile at all starts a setup menu.

A profile can hold several keys of the same provider; each is tried in turn when one runs out of quota. Give them in
one line, separated by spaces or commas, with the model anywhere among them. Running it again adds keys to the profile
and keeps its role and model:

```
/key gemini AIza...first AIza...second gemini-2.5-flash-preview-tts
/key gemini AIza...third
/voice gemini
```

A key profile kept for the voice (or pictures, clips) while a login writes the scripts never writes them itself, not
even when the login is busy, so it is never billed for the chat.

Then check everything:

```bash
vanity-studio doctor
```

It reports the browser, whether the Studio's catalog is reachable, ffprobe, the profile that writes scripts, and the
profiles for voice, stills and clips.

## 2. Who does what: the roles

| Role | Used for | Works with | How to set it |
|---|---|---|---|
| **scripts** | writing scripts, editing, answering | the first usable profile (`/profiles`, `/use <name>`) | `--login` / `/key`, `/model` |
| **voice** | `"voice": true` in a script, `edit_video speak` | **API keys only**: Gemini (TTS models, free tier at aistudio.google.com), OpenAI (`gpt-4o-mini-tts`), Alibaba (`qwen3-tts-flash`) | `/voice <profile> [model]` |
| **stills** | `{"make": "still"}`, `edit_video make kind=still` | OpenAI key or ChatGPT login, Antigravity login, Alibaba key (Wan image) | automatic; `/images <profile>` to put one first |
| **clips** | `{"make": "clip"}`, `edit_video make kind=clip` | Alibaba DashScope key (Wan image-to-video), or `DASHSCOPE_API_KEY` | `/key alibaba`, `/clips <profile> [model]` |

A profile speaks only when you opt it in: it carries the `voice` role, pins a voice model, or lists a model whose
name contains `tts`. A provider is never chosen to speak by default, because the wrong voice for the language is
worse than none. Voices can be changed with the `GeminiVoice`, `OpenAiVoice` and `QwenVoice` settings.

`/roles` prints the current assignment. Without a voice profile, videos are made with `"voice": false` (music and
on-screen text); without image or clip profiles, they use your own pictures and clips.

## 3. A project folder

Run Vanity Studio in the folder the videos belong to (`-C <dir>` or `/cwd <dir>` to change it). `/init` creates
the setup files.

```
my-shop-videos/
  media/                     your pictures, clips, sounds, logo.png
    web/                     pictures downloaded from sites (web download)
    made/                    AI stills and clips made for jobs
  videos/
    7-windows-11-pro/        one finished video:
      windows-11-pro.mp4         the video
      windows-11-pro-sheet.jpg   contact sheet (the middle of every scene)
      windows-11-pro-banner.png  the last frame at full size (a static banner)
      windows-11-pro.vstudio.json the editable Studio project (media inside)
      report.json                scenes, timings, notes, checks
      script.json                the script it was made from (render it again with `vanity-studio render`)
  edits/
    my-cut/                  a Studio project being edited: doc.json, media/, looks/ (previews)
  .vanity-studio/
    brand.json               name, url, logo, colors, language
    brand.md                 what it sells, to whom, the offer, the voice
    instructions.md          rules for every video in this folder
    personas/  skills/       your own personas and playbooks
    jobs/                    job state (resumes after a restart)
    memory.json              notes kept between sessions
```

## 4. Asking for videos

Write what you want the way you would brief a video editor. Some examples:

- `make a facebook ad about https://fiveqb.com`. It reads the page (`web read`), downloads available pictures, and
  builds a 4:5 feed ad from the site's real text and media.
- `make a video about https://5qb.ro/`. It opens the site as a visitor (`make_video site`) and turns captured pages
  into a narrated walkthrough.
- `a 15 second reel for my bakery, use the photos on my desktop`. It runs `files list folder=desktop`, looks at the
  pictures, and builds a reel from them.
- `a logo reveal for the YouTube intro, two takes`
- `an ad to A/B test, 3 takes`. Each take renders with its own seed (look, music, entrances).
- `cut my clips from Videos/trip into a 30 s video with titles and music`. This is an `edit_video` doc.

Formats: a Facebook or Instagram feed ad or post is **portrait** (4:5, 1080x1350); a story, reel, TikTok or Shorts
video is **reel** (9:16, 1080x1920); a square post is **square** (1080x1080); YouTube or a website is
**landscape** (1920x1080).

The answer names the job number and one line per scene. The render runs in the background: progress lines appear
as `[video #7] ...`, and when it is done the report is printed and handed to the conversation, so you can follow up
with "make the music calmer".

A one-shot request (`vanity-studio "..."`) waits for its videos before it exits. `--no-wait` exits right away, and
the jobs resume the next time Vanity Studio runs in that folder.

### Prompts in a file

A long request (one scene per moment, the pages to walk, the pictures to use) is easier to keep in a Markdown or text
file than to retype. Write it in a `.md` or `.txt` file, then either name the file on the command line:

```
vanity-studio C:\Users\me\Desktop\promo.md
```

or ask for it in the chat: `run promo.md`, `make the video in brief.txt`. The model reads the file and makes what it
asks for. A file that explains its prompt keeps the request in a code block, as
[example_prompt.md](../example_prompt.md) does; that file ships next to the program, so `run example_prompt.md` works
in any folder.

## 5. Commands

### Command line

| Command | What it does |
|---|---|
| `vanity-studio` | the chat in the current folder |
| `vanity-studio "<request>"` | one request, then wait for its videos (`--no-wait` to skip) |
| `vanity-studio <prompt.md>` | the request a `.md` or `.txt` file holds, the same way |
| `vanity-studio render <file.json> [-o dir]` | render a script or a Studio project without a model, and wait |
| `vanity-studio read <url> [--click "Specs"]` | what a page says: title, prices, product data, text, pictures |
| `vanity-studio site <url> [--click "A > B"] [--format reel] [--logged-in]` | what a page offers to click, per screen |
| `vanity-studio blocks` | the Studio's scene blocks, params and limits |
| `vanity-studio docs` | the Studio's project document reference |
| `vanity-studio jobs` | the jobs of this folder |
| `vanity-studio doctor` | check the browser, the Studio, ffprobe and the roles |
| `vanity-studio tool <name> '<json>'` | run one tool directly (for scripts), e.g. `tool web '{"action":"read","url":"https://..."}'` |
| `--login <provider>` | sign in and exit |
| `--site-login <url>` | open the login browser to log in to a site once (tutorials behind a login) |
| `--usage` | what is left on each login and what this folder spent |

Options: `-C <dir>`, `-P <profile>`, `-m <model>`, `--persona <name>`, `--skill <name>`, `--max-turns <n>`,
`--no-memory`, `--sandbox`, `--deny <path>`, `-v`.

### In the chat

| Videos | |
|---|---|
| `/jobs`, `/job <n>` | the jobs; one job's status, files and report |
| `/cancel <n\|all>` | stop a job, or every job still being made |
| `/open <n>`, `/folder <n>` | play a finished video; show its files |
| `/edit <n>` | open a finished video's project in the Studio's editor in your browser |
| `/blocks`, `/brand`, `/media` | the blocks; the brand; the folder's pictures and clips |
| `/roles`, `/voice`, `/images`, `/clips` | see and set who speaks, draws and animates |
| `/site-login <url>` | log in to a site once for tutorial steps behind a login |
| `/doctor`, `/studio` | the checks; the Studio's address |

| Models and session | |
|---|---|
| `/login`, `/key`, `/profiles`, `/use`, `/model`, `/models`, `/remove` | profiles and models |
| `/tune` | temperature, top_p, max_tokens, thinking on/off, timeout, ctx |
| `/usage` | limits left and spend |
| `/reset`, `/cwd`, `/tools`, `/memory`, `/init`, `/project` | the session and the folder |
| `/personas`, `/persona <name\|off>`, `/skills`, `/skill <name>` | personas and skills |
| `/config`, `/set <name> [value]` | where config lives; machine settings |
| `/verbose on\|off`, `/sandbox on\|off`, `/quit` | |

Ctrl+C stops the current request. At the prompt it exits, and open jobs resume next time.

## 6. The tools the model uses

| Tool | Actions |
|---|---|
| `make_video` | `blocks` (the catalog), `look` (a picture with a grid, to read points and boxes), `site` (walk a website), `submit` (queue a script), `remix` (change a finished video), `status`, `cancel` |
| `edit_video` | `docs`, `list`, `open`, `show`, `save`, `patch`, `speak`, `make`, `validate`, `frame`, `sheet`, `render` |
| `web` | `read` (a page's facts, prices, product data, text, pictures), `download` (pictures into media/web/) |
| `files` | `list` (desktop, downloads, pictures, videos, documents, home, project, media or a path), `find`, `info`, `import` |
| `brand` | `read`, `save` |
| `read_file` | read any file: text, PDF briefs, pictures (shown to the model) |
| `memory` | `search`, `list`, `save`, `delete` |
| `skill_view` | load a playbook |

Everything a tool can do can be run without the model through `vanity-studio tool <name> '<json>'`.

## 7. The video script

The model writes scripts; you rarely need to. They are plain JSON you can keep, change and render with
`vanity-studio render`:

```json
{
  "script": 1,
  "title": "Five Quantum Bits",
  "format": "portrait",
  "language": "en",
  "voice": true,
  "treatment": "bold",
  "brand": { "name": "Five Quantum Bits", "url": "fiveqb.com", "logo": "media:logo" },
  "scenes": [
    { "block": "hook-statement", "line": "Make your next video by simply describing it." },
    { "block": "picture-hero", "line": "Turn your ideas, website, and media into polished videos.",
      "params": { "headline": "Videos made *by asking*" }, "files": { "picture": "media/fiveqb.png" } },
    { "block": "offer-poster", "line": "Create ads, reels, product demos, and tutorials.",
      "params": { "headline": "From brief to *video*", "figure": "AI video CLI", "button": "Learn more" } },
    { "block": "cta-close", "line": "Visit fiveqb.com", "params": { "button": "fiveqb.com" } }
  ]
}
```

- **Fields**: `title`, `format` (reel, square, portrait, landscape), `language`, `voice`, `treatment` (bold, calm,
  editorial, kinetic, cinematic, retro), `look` and `music` (auto, a mood or a bed), `brand`, `takes` (1-4),
  `seed`, `speed` (0.4-2.5), `textSoftness` (0-3), `quality` (low, medium, high, ultra) and `scenes`.
- **A scene**: `block`, `line` (spoken when voice is on), `params`, `files`, and optionally `look`, `sfxDrop`,
  `sfxAdd`, `textIn` and `textSoftness`.
- **The blocks** and every param's type and limit come from the live Studio: `vanity-studio blocks`. Submit refuses
  anything outside a limit and names the scene and field.
- **Files**:
  - a path in the project (`media/shop.jpg`);
  - a name (`shop.jpg`, searched in media/ and the project);
  - a full path anywhere (`C:/Users/me/Desktop/shop.jpg`, `~/Pictures/a.png`, `Desktop/a.png`);
  - `media:logo` (the brand's logo);
  - `{"make": "still", "prompt": "..."}`, an AI picture;
  - `{"make": "clip", "from": "<picture>", "prompt": "<how it moves>"}`, an AI clip.
- **Website steps**: `screen-demo` with `url`, `clicks`, `target` (and `login: true` behind a login), or
  `screen-flow` with `pages`. The page is captured at render time in the phone layout for reel and portrait, or on
  the desktop for the other formats.

The contract between the script and the Studio is the Studio's `docs/video-jobs.md`.

## 8. Editing a video

**Small changes to a video it made**: ask for them. The model uses `make_video remix` with patches like
`{"path": "music", "value": "calm"}`, `{"path": "scenes[2].line", "value": "..."}` or
`{"op": "replace", "from": "cheap", "to": "affordable"}`. Lines and AI pictures that did not change are reused, so a
remix costs nothing extra in AI calls.

**Anything a script cannot say**: the model uses `edit_video` on the Studio's project document. That covers moving
or restyling one text, retiming a clip, keyframes, a transition, layers the blocks do not have, captions, 3D, cutting
your own footage, or a project you made by hand in the Studio.

```
edit_video docs                                 the Studio's own reference (once)
edit_video open source=7                        a finished job → edits/<name>/doc.json (+ its media)
edit_video open source=C:/path/project.vstudio.json
edit_video patch name=... patches=[{"path": "scenes[1].layers[0].text", "value": "..."}]
edit_video speak name=... text="..."            a voice file for the doc's audio
edit_video make name=... kind=still prompt="..."
edit_video validate | frame t=4.2 | sheet       look before rendering
edit_video render name=...                      a job like any other → videos/
```

**By hand in the Studio**: every finished video ends with an edit link,
`https://photovideoeditor.com/app/#/video/new?importUrl=...`. Ctrl+click it (or copy it into your browser) and the
Studio imports the project and opens it in its editor. The link points at a small server on this computer
(127.0.0.1, port 47812), so it works while Vanity Studio is open:

- `/edit <n>` in the chat opens it for you.
- For an older video, `vanity-studio edit <n>` serves the link until you press Enter.
- The first time, Chrome asks whether photovideoeditor.com may reach apps on this device: allow it.
- Any time, the Studio's home page has **Import project**: choose the `.vstudio.json` from the video's folder.

## 9. Jobs and what they produce

A job goes through five stages:

- **prepare**: voice and AI media are made.
- **ship**: the files go to the renderer.
- **render**: the Studio renders in headless Chrome, two jobs at a time (`VANITY_VIDEO_PARALLEL`).
- **collect**: the outputs are copied to videos/.
- **report**: the result is printed and handed to the conversation.

Each stage is saved, so a job interrupted by closing the program continues the next time Vanity Studio starts in
that folder. A failed stage is retried twice before the job fails, and the report says why.

A make that fails (no picture drawn, no voice) does not fail the video: the scene uses the block's fallback, and the
report lists it under Notes. The report's Checks list what the Studio measured on the frames, such as text that did
not read on its picture and was fixed with a plate.

## 10. Brand, instructions, personas, skills, memory

- **brand.json and brand.md** (`.vanity-studio/`) hold the brand's name, site, logo and colours, and what it sells,
  to whom and in which voice. The model reads them before every script. Say "remember that our offer is ..." and it
  saves them, or edit the files.
- **instructions.md** (or `AGENTS.md` / `VANITY.md` in the folder) holds rules for every video, for example "always
  end on the site" or "never show prices".
- **Personas** are `personas/<name>.md` in `.vanity-studio/` or `~/.vanity-studio/`, with front matter
  (`name`, `description`, `tools`, `skills`, `max_turns`) and a body that leads the system prompt. The examples are
  `ads` and `tutorials`. Use `/persona ads` or `--persona ads`.
- **Skills** are `skills/<name>.md` playbooks the model loads when a request matches (front matter: `name`,
  `description`, `always`). The examples are `scroll-stopping-hooks`, `logo-reveal` and `footage-cut`. `/skill <name>`
  pins one into every turn.
- **Memory** holds notes kept between sessions of a folder. After each request, the steps that proved something are
  saved automatically, and each brand's video signature (its treatment and look) is kept so every video looks like
  the same brand. Use `/memory` to list, add or delete notes.

## 11. Settings and environment

Everything lives in `~/.vanity-studio/` (`VANITY_STUDIO_HOME` moves it): `config.json` (profiles, keys, logins,
settings), `logs/`, `studio-jobs/` (the renderer's working folders), `browser-login/` (the login browser's profile)
and `projects/` (state for folders without `.vanity-studio/`). See `config.example.json`.

| Setting (`/set`) or variable | Meaning |
|---|---|
| `StudioUrl` / `VANITY_STUDIO_URL` | another Studio (default https://photovideoeditor.com/app/) |
| `VideoParallel` / `VANITY_VIDEO_PARALLEL` | renders at the same time (default 2, at most 8) |
| `BrowserPath` / `CHROME_PATH` | the browser to render in |
| `GeminiVoice`, `OpenAiVoice`, `QwenVoice` | the voice names (Kore, alloy, Cherry) |
| `DASHSCOPE_API_KEY` | an Alibaba key for clips without a profile |
| `GoogleClientSecret` (+`Alt`, `GoogleAppClientSecret`) / `VANITY_STUDIO_GOOGLE_SECRET` | the Google installed-app secret the Antigravity login needs |
| `VANITY_STUDIO_PROMPT_LOG=0` | no local prompt log |

## 12. Troubleshooting

- **`client_secret is missing` at the Google login.** Google needs the Antigravity installed-app client secret. Store
  it once with `/set GoogleClientSecret` (or `VANITY_STUDIO_GOOGLE_SECRET`), then `--login antigravity` again.
- **"no AI profile can speak the lines".** Add a Gemini, OpenAI or Alibaba API-key profile with `/key`, then
  `/voice <profile>`. Logins cannot speak.
- **"The Studio at ... has no video catalog".** The machine cannot reach photovideoeditor.com; check the connection,
  or `VANITY_STUDIO_URL`.
- **"no Chrome or Edge".** Install Chrome, or `/set BrowserPath <path>`.
- **A tutorial step fails with "there is no ... to click".** The page does not have those words; the error lists
  what it has. The model fixes the step from `make_video site`.
- **"the login browser is not logged in to ..."** Run `vanity-studio --site-login <url>`, log in, and close the
  window.
- **A render is slow.** Software rendering is about 1 s of work per second of 1080p video, plus glow and motion
  blur. Two jobs render at once.
- **Logs**: `~/.vanity-studio/logs/vanity-studio.log`; `-v` echoes them, including the renderer's.
