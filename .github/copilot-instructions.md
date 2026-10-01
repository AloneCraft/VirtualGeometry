# GitHub Copilot Instructions

このリポジトリでは、ルートの `AGENTS.md` と `AI_SYNTAX_RULES.md` を優先して作業してください。

- ユーザーへの説明は、明示されない限り日本語で行ってください。
- C# は `.editorconfig` の compact standard style に従ってください。
- PowerShell で日本語を含むファイルを読むときは、`Get-Content -Raw -Encoding UTF8 -Path <file>` を使ってください。
- コード生成や Source Generator 周辺では、中央の orchestration ファイルに特殊処理を増やさず、機能単位の Fragment 側へ寄せてください。
