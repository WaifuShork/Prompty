# Changelog

Every notable change to Prompty is recorded here. Downloads for each version are on the
[Releases page](https://github.com/WaifuShork/Prompty/releases).

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and Prompty uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html):

- **Major** (`2.0.0`) changes only when the project owner decides.
- **Minor** (`1.1.0`) is a release with new features (`feat:` commits).
- **Patch** (`1.0.1`) is a release with fixes and other changes only.

`release.ps1` works out the version and writes each new section from the commit history. Notes added
under *Unreleased* are carried into the next release.

## [Unreleased]

## [1.0.0] - 2026-11-11

The first release.

### Added

- **Prompt editor** with Markdown and XML-tag highlighting, a section outline, templates, autosave and prompt history.
- **Claude Code integration**
  - Lists running and recent Claude Code sessions, live or ended.
  - Follows the most active session automatically.
  - Reads each session's transcript as context for the models.
- **Claude output pane** that mirrors the Claude Code terminal:
  - Each tool call shows a header and a summary line.
  - Diffs have line numbers and red/green highlighting, and tool output can be expanded or collapsed (click, Enter, Ctrl+O, or Expand all / Collapse all).
  - The view holds its place while the transcript updates live.
  - A status line shows what Claude is doing (for example "Sketching…") with the elapsed time and output tokens.
- **Send to Claude**
  - Types the prompt straight into the selected running session, as if you had typed it.
  - The target process is identified by its PID, double-checked against the process start time.
  - Falls back to MCP (`wait_for_prompt`, `/mcp__prompty__send`) and the clipboard.
- **AI assistance**
  - Automatic analysis suggests ways to fill, rewrite or add sections.
  - Fill or rewrite a single section, rewrite a selection with an instruction, continue at the cursor, and fill `{{placeholders}}`.
- **Prompt generator (GENERATE box)**
  - Expands a short request into a complete prompt with Goal, Context, Requirements, Relevant files, Constraints and Done when sections.
  - Works from the conversation and an overview of the project: README, CLAUDE.md and the file tree.
- **Per-session drafts**: every Claude Code session gets its own editor text, undo history, generator request and suggestion cards.
- **Model providers**
  - Local: Ollama and OpenAI-compatible local servers such as LM Studio, llama.cpp and vLLM.
  - Cloud, via API key: Anthropic, OpenAI, Google Gemini, Mistral, Groq, DeepSeek, xAI, Together AI, Fireworks AI, Cerebras, Hugging Face, Azure OpenAI and any OpenAI-compatible API.
  - Cloud, via browser sign-in: OpenRouter.
  - API keys are stored encrypted with Windows DPAPI.
- **MCP server** built into the app (`share_context`, `wait_for_prompt`, `get_draft`, `seed_draft`, and the `send` prompt), with one-click registration in Claude Code.
- **Single-file release build**: one self-contained `Prompty.exe` that runs without .NET installed, made by `build.ps1`, plus a GitHub Actions workflow that builds every push and publishes tagged releases.

[Unreleased]: https://github.com/WaifuShork/Prompty/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/WaifuShork/Prompty/releases/tag/v1.0.0
