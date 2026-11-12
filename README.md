# Prompty

[![build](https://github.com/WaifuShork/Prompty/actions/workflows/release.yml/badge.svg)](https://github.com/WaifuShork/Prompty/actions/workflows/release.yml)
[![release](https://img.shields.io/github/v/release/WaifuShork/Prompty)](https://github.com/WaifuShork/Prompty/releases/latest)

A desktop prompt editor for Claude Code. You write your next message to Claude in a real text editor. A
model, local or cloud, reads the live Claude conversation and your project, then suggests fills and rewrites or
writes the whole prompt for you. **Send** types it straight into the running Claude Code session.

WPF · .NET 8 · AvalonEdit · Ollama / cloud LLMs · MCP C# SDK

- [Setup](#setup)
- [Using Prompty](#using-prompty)
- [Models](#models)
- [Building from source](#building-from-source)
- [Releases & versioning](#releases--versioning)
- [How it works](#how-it-works)

---

## Setup

### Requirements

- Windows 10 or 11 (x64)
- [Claude Code](https://docs.claude.com/en/docs/claude-code) (the `claude` CLI)
- A model: either [Ollama](https://ollama.com) on your PC (free and private) or an account with a cloud provider

### 1. Install Prompty

Download **`Prompty.exe`** from the [latest release](https://github.com/WaifuShork/Prompty/releases/latest) and run it.
It is a single self-contained file: no installer, and no .NET needed. Put it wherever you like, for example
`%LOCALAPPDATA%\Programs\Prompty\`, and pin it to Start.

Windows SmartScreen may warn you the first time, because the exe isn't code-signed. Choose **More info → Run anyway**.
Each release lists a SHA-256 checksum you can verify with `Get-FileHash Prompty.exe`.

### 2. Set up a model

**Option A: local with Ollama (recommended; nothing leaves your PC)**

```powershell
winget install Ollama.Ollama
ollama pull qwen2.5:3b     # fast model: analysis, suggestions, rewrites (~2 GB)
ollama pull qwen3:8b       # prompt generator (~5 GB; needs ~8 GB of VRAM to be quick)
```

Prompty finds Ollama automatically. If Ollama isn't running yet, a setup card shows these commands and Prompty
reconnects once it's up.

**Option B: a cloud provider.** Open **⚙ Settings**, pick a ☁ provider, and either click **Sign in with OpenRouter**
or paste an API key (**Get a key ↗** opens the provider's key page). Then click **Test & load models** and choose
models from the dropdowns. See [Cloud models](#cloud-models).

### 3. Connect Claude Code

Register Prompty's MCP server once. It applies to all your projects:

```powershell
claude mcp add --transport http --scope user prompty http://127.0.0.1:47821/mcp
```

Or click **⚙ Settings → Register now**. Restart any Claude Code sessions that are already running so they pick it up.

### 4. Try it

1. Start Claude Code in a project, then open Prompty. The session appears under **CLAUDE CODE SESSIONS**.
2. Type a short request in the **GENERATE** box ("add a dark mode toggle to settings") and press Enter.
3. Edit the generated prompt, then press **Send to Claude** (Ctrl+Enter). It's typed into that session.

### Updating

Prompty checks GitHub for a new release when it starts. When one is out, it shows what's new and offers three choices:

- **Update now**: downloads the new `Prompty.exe` and checks its SHA-256 against the release's published checksum. It
  then replaces the exe in place and restarts into the new version, with your drafts and settings saved. If the
  download fails or doesn't match the checksum, nothing is changed.
- **Skip this version**: no prompt again until the next release.
- **Don't ask again until X.0**: no prompts until the next major version.

**⚙ Settings → Check for updates** checks at any time, and clears an earlier skip or pause. You can also turn off the
check at launch there. Updating in place needs write access to the folder Prompty.exe is in, so keep it somewhere
you own, such as `%LOCALAPPDATA%\Programs\Prompty`, rather than `Program Files`.

### Where Prompty keeps things

| Path | Contents |
|---|---|
| `%APPDATA%\Prompty\settings.json` | Provider, models, ports, preferences |
| `%APPDATA%\Prompty\credentials.dat` | API keys, encrypted for your Windows account (DPAPI) |
| `%APPDATA%\Prompty\drafts\` | One draft per Claude session (`<sessionId>.md`, `.request.txt`) |
| `%APPDATA%\Prompty\history\` | Every prompt you've sent |

To uninstall, delete `Prompty.exe` and the `%APPDATA%\Prompty` folder, and run `claude mcp remove prompty --scope user`.

---

## Using Prompty

| Action | How |
|---|---|
| Write a full prompt from a short request | **GENERATE** box → Enter or ➜ (■ stops it) |
| Analyze the draft for suggestions | F5, or automatically a few seconds after you stop typing |
| Fill or rewrite one section | Outline panel buttons |
| Fill every empty section | ✨ Fill empty |
| Fill `{{placeholders}}` from the conversation | `{{ }}` Placeholders |
| Rewrite the selection (or the section at the cursor) | Ctrl+R, or the Ask box with an instruction |
| Suggest the next sentence | Ctrl+Space |
| Expand or collapse tool output in the Claude pane | Click the line or the margin marker, Enter, Ctrl+O, or Expand all / Collapse all |
| Send to Claude | Ctrl+Enter |

Every Claude Code session has its own draft, undo history, GENERATE request and suggestion cards.

---

## Models

### Local

The analysis prompts are short and ask for strict JSON, so a 3–4B instruct model is enough. Prompt generation
benefits from a bigger model.

| Model | Notes |
|---|---|
| `qwen2.5:3b` (default) | Fast on CPU or a small GPU, reliable JSON. |
| `qwen3:8b` (default generator) | Much fuller prompts. Fits in 8 GB+ of VRAM. |
| `llama3.2:3b`, `phi4-mini`, `gemma3:4b`, `qwen3:4b` | Alternatives. Any `<think>` output is stripped. |
| `qwen3.8:27b` | Best quality locally, but an 18 GB download. It needs a 24 GB GPU to be fast; otherwise it partly runs on the CPU. |

For LM Studio, llama.cpp or vLLM, choose **⚙ → Local OpenAI-compatible server** and set its URL.

### Cloud models

| Provider | Connect with |
|---|---|
| **ChatGPT plan** | **Continue with ChatGPT**: sign in with your ChatGPT account through OpenAI's official [Sign in with ChatGPT](https://developers.openai.com/siwc), and Prompty uses your **Plus or Pro plan** instead of API billing. Calls count against your plan's limits. On Plus, the 5-hour limit is shared with ChatGPT and other apps. The toolbar shows *Using ChatGPT plan · Manage usage*. If you reach a limit, Prompty pauses rather than switching to paid billing. |
| **OpenRouter** | **Sign in with OpenRouter**: approve in the browser and a key is created for you (OAuth PKCE). Hundreds of models from every major lab. |
| Anthropic (Claude), OpenAI, Google Gemini, Mistral, Groq, DeepSeek, xAI (Grok), Together AI, Fireworks AI, Cerebras, Hugging Face | An API key |
| Azure OpenAI | An API key. Put your resource in the URL and use deployment names as the models. |
| Any other OpenAI-compatible API | Base URL plus an API key |

- Keys are encrypted with Windows DPAPI. If none is saved, Prompty uses the provider's usual environment variable (`OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `GEMINI_API_KEY`, …).
- **Test & load models** checks the key and loads the provider's real model list. Each provider remembers its own URL and models.
- If a model rejects an optional setting (temperature on reasoning models, JSON mode), Prompty drops it and retries.
- **Privacy:** with a cloud provider, the conversation digest, your draft and the project overview (for GENERATE) are sent to that service and billed to your account. Settings says so whenever a cloud provider is selected.

**Why there's no "sign in with Claude" or "sign in with Google":** Anthropic doesn't allow third-party apps to use
Claude Free/Pro/Max subscription logins; those are only for Claude Code and claude.ai. Google likewise prohibits reusing Gemini CLI's
Google-account login in other tools, and has suspended accounts that did. Prompty therefore connects to Claude and Gemini
with API keys, or through OpenRouter. ChatGPT is the one major platform with an official way for apps to use a
subscription.

---

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/WaifuShork/Prompty.git
cd Prompty
dotnet run --project src/Prompty          # run a debug build

.\build.ps1                               # → artifacts\publish\Prompty.exe (+ zip and SHA-256)
.\build.ps1 -Smoke                        # also launch it once as a smoke test
```

`build.ps1` publishes one self-contained `Prompty.exe` (~90 MB) containing:
- the .NET runtime, WPF and ASP.NET Core;
- every NuGet package;
- the native WPF libraries;
- the debug symbols.

It is compressed and ReadyToRun-compiled. It isn't trimmed, because WPF doesn't support trimming. The settings are in
`src/Prompty/Properties/PublishProfiles/SingleFile.pubxml`.

To test the updater against a local feed instead of GitHub, set `PROMPTY_UPDATE_FEED` to a URL that returns a
GitHub-style "latest release" JSON. A development build (`dotnet run` or `bin\Debug`) offers the download page
instead of replacing itself.

---

## Releases & versioning

Prompty follows [Semantic Versioning](https://semver.org). The current release is **1.0.0**. The full history is
in [CHANGELOG.md](CHANGELOG.md), and downloads are on the [Releases page](https://github.com/WaifuShork/Prompty/releases).

Commits use [Conventional Commits](https://www.conventionalcommits.org) so that versions can be worked out automatically:

| Commit | Example | Next version |
|---|---|---|
| `feat:` | `feat(generator): stream into the editor` | **minor**, 1.2.3 → 1.3.0 |
| `fix:`, `perf:`, `refactor:`, `docs:`, … | `fix(output): keep folds open on refresh` | **patch**, 1.2.3 → 1.2.4 |
| anything, with `-Major` | | **major**, 1.2.3 → 2.0.0 (only when the owner decides) |

To cut a release:

```powershell
.\release.ps1 -DryRun     # preview the version and notes
.\release.ps1             # bump version + CHANGELOG, commit, tag vX.Y.Z (does NOT push)
git push origin main --follow-tags   # publish: CI builds Prompty.exe and creates the GitHub Release
```

Major versions are never bumped automatically. Commits marked as breaking are released as a minor version
(with a warning) unless `-Major` is passed. Notes written under *Unreleased* in the changelog are added to the next release.

---

## How it works

| Piece | What it does |
|---|---|
| **Conversation context** | Tails Claude Code transcripts (`~/.claude/projects/*/*.jsonl`) and follows the most recently active session. It keeps user messages, Claude's replies and a one-line summary of each tool call, and skips tool output, system reminders, sidechains and slash-command noise. Claude can also push a summary with `share_context`. |
| **Analysis** | The draft is split into `## Markdown headings` or line-level `<xml_tags>`. The model returns up to 4 grounded suggestions (fill, rewrite or add), each tied to a section. Suggestions stream into editable cards; **Apply** replaces just that section's body, and Ctrl+Z undoes it. |
| **Prompt generator** | Expands a short request into Goal / Context / Requirements / Relevant files / Constraints / Done when. It uses the conversation, the project's `CLAUDE.md`/`README.md` and a shallow file tree. The result streams into the editor as one undo step. |
| **Claude output** | The middle pane shows the selected session laid out like the Claude Code terminal:<br>• Each tool call is a header (`● Update(file)`) plus a summary line (`⎿ Added 3 lines, removed 1 line`).<br>• Diffs (with line numbers) and full output are collapsed underneath.<br>• A status line shows what Claude is doing, for how long, and the output tokens.<br>• The transcript is read incrementally, so the view holds still. |
| **Send to Claude** | Types the prompt into the selected *running* session's console input, as a bracketed paste for multi-line text, then presses Enter.<br>• Running sessions come from Claude Code's registry (`~/.claude/sessions/<pid>.json`).<br>• The PID is double-checked against the process start time before typing.<br>• There's no focus change and no global keystrokes.<br>• Falls back to MCP (`wait_for_prompt`, `/mcp__prompty__send`) and the clipboard. |
| **MCP server** | Runs inside the app on `127.0.0.1:47821/mcp`. |

### MCP surface

| Name | Kind | Purpose |
|---|---|---|
| `send` | prompt | In Claude Code, run **`/mcp__prompty__send`** to insert the finished prompt as your message. |
| `wait_for_prompt` | tool | Claude blocks until you press *Send* in Prompty. Try: *"wait for my next prompt in Prompty"*. |
| `share_context` | tool | Claude pushes a conversation summary for better suggestions. |
| `get_draft` | tool | Claude reads the draft on screen. |
| `seed_draft` | tool | Claude proposes a draft: it fills an empty editor, or shows up as a card. |

### Security

- The MCP server listens only on loopback. It can only read the draft and hand prompts over; it never reads files or runs commands.
- Send writes keystrokes only to the one Claude Code console you selected.
- API keys never leave your machine except to the provider they belong to.
