# Example prompts

Paste a prompt at the `You:` prompt as **one line**: the console sends every line of a multi-line paste as its own
message. Run it in an empty folder, so the video's brand, pictures and project stay together.

## A 70-second promo tutorial with a voice-over

A landscape video for GitHub and YouTube about [Photo Video Editor](https://photovideoeditor.com/app/#/home) and
Vanity Studio CLI. It names a different Studio block for every scene, so no two slides move alike. Its screens are
real: the Studio's home page and the GitHub pages are captured in the browser, and the pictures come from the site.

```
Make an outstanding landscape 16:9 promo tutorial in English, about 70 seconds, one take, for GitHub and YouTube, about the free Photo Video Editor at https://photovideoeditor.com/app/#/home and the Vanity Studio CLI, made by Five Quantum Bits (fiveqb.com). Voice-over on every scene in a clear, friendly voice with short punchy lines, an upbeat tech music bed, kinetic treatment in the site's own colours, a brisk pace. Every scene a different block and a different text entrance (pop, rise, slide, typewriter, blur, scramble, drop), so no two slides move alike. Walk https://photovideoeditor.com/app/#/home, https://github.com/x5qubits/vanitystudio and https://github.com/x5qubits/vanitystudio/releases/latest for real screens, web read https://photovideoeditor.com/ for a picture of the editor, and web download https://photovideoeditor.com/upload/generated/Free-Online-Background-Remover_thumb_1100x619.webp. Scenes: 1) hook-statement: Edit photos and videos free, or make them just by asking; 2) screen-demo of the Studio home with the cursor on the Video Editor card: edit videos right in your browser; 3) steps, not numbered, the editor's promises: free with no watermark, no account needed to edit, your files never leave your device, works offline once installed, photos, logos, business cards and flyers too; 4) device-mockup: the editor in a browser window with the site's picture of the editor: timeline, layers, text and transitions; 5) offer-poster with the background remover picture as its picture: headline Remove backgrounds in one click, figure Free, sub clean edges, transparent PNG; 6) title-card: Vanity Studio CLI, kicker Make videos by asking, no label; 7) screen-flow of the GitHub repo and its latest release: download it free for Windows, one exe, nothing to install; 8) lower-third over that scene: Vanity Studio CLI, role github.com/x5qubits/vanitystudio; 9) picture-poster over an AI picture of a laptop on a desk showing a dark terminal window, soft daylight: title Vanity Studio CLI does the work, sub real-screen tutorials, product ads, your own clips, voice-over, note every video opens in the editor with one link, button Download; 10) numbered steps: open photovideoeditor.com, download from github.com/x5qubits/vanitystudio, ask for your video; 11) cta-close: Start creating free, button photovideoeditor.com; 12) end-card: Made by Five Quantum Bits, address fiveqb.com. Use only these facts, nothing invented.
```

What it needs:

- **A profile that writes the script:** any login or key (`--login antigravity`, `/key ...`).
- **A profile that can speak**, for the voice-over: a Gemini, OpenAI or Alibaba API key with the voice role.
  ```
  /key gemini <key> gemini-2.5-flash-preview-tts
  /voice gemini
  ```
  Several keys go in one line (`/key gemini <key1> <key2> ...`); each is tried in turn, on each of the voice models,
  when one runs out of free quota. If no line can be spoken, the job stops before the render and says why.

How the prompt is built, to write your own:

- **The format, the length and the takes come first:** "landscape 16:9", "about 70 seconds", "one take".
- **The style in one sentence:** the voice, the music, the treatment (`bold`, `calm`, `editorial`, `kinetic`,
  `cinematic`, `retro`), the pace.
- **The sources:** the pages to walk for screens (`walk <url>`), to read for facts and pictures (`web read <url>`), the
  pictures to download (`web download <url>`).
- **One numbered scene per moment,** each naming its block and its words. A list of points is a `steps` scene, at most
  five points; a page is a `screen-demo` (one page) or a `screen-flow` (two to five). `vanity-studio blocks` lists
  every block and its limits.
- **"Use only these facts, nothing invented"** keeps every claim on screen to what you wrote and what the pages say.
