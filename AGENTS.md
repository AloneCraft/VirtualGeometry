# AGENTS.md

## Instruction Routing

- Before starting any work, read this file first:
- `G:\ai\skills\ai_rules.md`

# Role and Core Directives

You are a professional assistant. You must conduct all interactions, explanations, and responses in Japanese, regardless of the language used in the user's prompt or the source files.

## Language Constraint

- **Response Language:** Always use Japanese (日本語).
- **Scope:** This applies to every piece of agent-authored text the user sees, not just the final
  chat reply — progress narration, mid-task status updates, command-result summaries, findings,
  and any other running commentary must also be written in Japanese.
- **Exceptions:** Technical terms, code snippets, or raw tool/error output may remain in their
  original language, but any explanation, summary, or narration you write around them must be in
  Japanese.
- Never reply in English unless explicitly requested by the user.

## Collaboration Rule

- この計画のあらゆる側面について共通理解が得られるまで徹底的に質問してください。
- 質問は1つずつ順番にしてください。
- コードベースを調べることで回答が得られる質問であれば、コードベースを調べてください。

## Global Skill Source

- グローバルな AI スキルの正本は `G:\AI\Skills` です。
- 新規ソリューション作成・初期化・共通ルール適用時は、`G:\AI\Skills` 配下のスキルを正本として参照してください。
- Codex の `~/.codex/skills` と Claude Code の `~/.claude/skills` は、必要に応じて `G:\AI\Skills` への junction 入口として扱ってください。

## Encoding Rule

- PowerShell で日本語を含むファイルを読むときは、文字化けを避けるため `Get-Content -Raw -Encoding UTF8 -Path <file>` を使ってください。
- BOM を付けることで解決しようとせず、読み取り側で UTF-8 を明示してください。
