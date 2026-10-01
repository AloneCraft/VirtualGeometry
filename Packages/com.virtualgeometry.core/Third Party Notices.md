# Third Party Notices

## meshoptimizer v1.1

- 正本: https://github.com/zeux/meshoptimizer/tree/v1.1
- MIT License、Copyright (c) 2016–2026 Arseny Kapoulkine。
- Editor 変換器は native ABI version `1010` を実行時に確認します。
- upstream の完全な notice は `Native~/meshoptimizer/LICENSE.md` に保存しています。
- C++ source は Editor 変換だけに使い、Player renderer は meshoptimizer を呼びません。
- wrapper は `Native~/VirtualGeometryMeshOptimizer.cpp`、出力は `Editor/Plugins/x86_64/VirtualGeometryMeshOptimizer.dll` です。
- Editor フォルダと `VirtualizedGeometry.Editor.asmdef` が native plugin / managed P/Invoke wrapper を Player から分離します。Windows Development Player の出力検査で native DLL / source の混入 0 を確認しています。

## Windows での再ビルド

Visual Studio 2026 の C++ tools と CMake を用意し、`Native~/Build-Windows.ps1 -Configuration Release` を実行します。script は `Library/VirtualGeometryNative` を configure し、x64 の Editor plugin を生成します。Debug / Release に対応します。Unity が DLL をロードしている間は再ビルドしません。

親 CMake は `MESHOPT_BUILD_DEMO`、`MESHOPT_BUILD_GLTFPACK`、`MESHOPT_BUILD_SHARED_LIBS`、`MESHOPT_INSTALL` を無効にします。library target は upstream CMake の明示的な `src/` 一覧を使います。demo、glTF tools、JavaScript bindings、upstream test/tool program は plugin へリンクしません。

Native~ は再現可能な meshoptimizer v1.1 上流原本 108 files を意図的に保持します。src-only CMake build に使用しない demo/tool 等の 13 top-level paths（85 files、1,563,139 B）も第三者原本に含まれ、削除は v0.1 の必須条件ではありません。Editor 用 DLL と上流 source は Player に含めません。Player build の収録検査、原本 SHA-256 一致、Release rebuild 成功を別々に記録しています。将来の配布容量整理は任意です。過去の削除計画・承認診断・backup は TestResults/M5/NativeCleanup に履歴として保持しています。

package 自身の code は `LICENSE.md` の条件で配布します。Unity / URP にはそれぞれの Unity license が適用され、この package が再ライセンスするものではありません。