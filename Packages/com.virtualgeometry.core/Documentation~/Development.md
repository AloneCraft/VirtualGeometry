# Virtualized Geometry 開発状況

これは独立実装であり、Epicの公式製品や互換実装ではない。M0〜M5の必須範囲を実装・検証したv0.1（package 0.1.0）。
原要求はリポジトリの `Requirements/VirtualizedGeometry.md` に保存する。

## 実環境

- Unity 6000.6.0f1。Editor同梱のURPテンプレートの指定はURP/Core 17.6.0。
- Windows / NVIDIA GeForce RTX 4060 Ti / 16 GB（SystemInfo 16,107 MiB） / NVIDIA driver 610.74。
- 第一ターゲットはDX12 / URP Forward / Base Camera / MSAAなし。
- GPU機能、深度方向、APIは起動したUnityのSystemInfoで別途記録する。

## 構成と段階

UPMのRuntime、Editor、Shaders、Tests、Samples~、Documentation~を分離する。
Computeで選択・可視リスト・描画引数を生成し、RenderGraph Raster Passでvertex pullingする。
オフラインBuilderとページ形式、非同期PageStore、GPU常駐管理は描画から分離する。

| 段階 | 合格条件 | 状態 |
|---|---|---|
| M0 | Compute→間接描画→通常メッシュと深度整合、実GPU画像・計測 | PASS（TestResults/M0） |
| M1 | Mesh変換・保存・読み戻し・全クラスタ描画・元メッシュとの画像差分 | PASS（TestResults/M1） |
| M2 | 境界保持階層LOD、CPU/GPU選択一致、混在LODで領域被覆 | PASS（TestResults/M2） |
| M3 | Frustumと保守的HZB、移動・テレポートで欠落なし | PASS（TestResults/M3、詳細はCulling.md） |
| M4 | ユニークデータが予算を超える非同期ページストリーミング | PASS（TestResults/M4、詳細はStreaming.md） |
| M5 | Lit、Proxy、UI、複数カメラ、Player、ベンチマーク | Lit/depth/共有Proxy/UI/debug/camera/形状/lifecycle/sample/Player機能検証PASS。1M/3M/10M×3経路のCPU/GPUを各cold/warm 360 samplesで実測。可視swapchain・queue深度1。PASS_V0_1。第三者上流原本は意図的に保持し、Player収録境界を検証 |
| M6 | Visibility Bufferとソフトウェアラスタライズ | v0.2以降 |

最大リスクは混在LODの境界保持とページ置換の原子性。単純な独立クラスタ簡略化を採用しない。
境界固定・置換グループ単位の選択・常駐粗表現への退避を、実装証拠とテストで確立する。

## スキル既定からの選択

ユーザーのUPM要件を優先し、EditorはパッケージのEditorへ配置する。
ユーザーが指定した現在のワークスペースを検証プロジェクトのルートとし、重複する子プロジェクトを作らない。
依存を減らすため非同期I/Oには.NET Task/CancellationTokenを採用する。UniTaskを必須にしない。
Editor UIにはUI Toolkitを使用する。ゲームの入力・操作は追加しない。
ユーザーが自律実装と段階ごとの検証を明示しているため、ゲーム設計のヒアリングは行わない。

## API一次資料

- Unity 6000.6付属 `com.unity.render-pipelines.core/Runtime/CommandBuffers/RasterCommandBuffer.cs` のGraphicsBuffer版DrawProceduralIndirect。
- 同 `Runtime/RenderGraph/RenderGraph.cs` のComputeGraphContext.cmd/RasterGraphContext.cmd。
- 同URP `Tests/Editor/RenderPassCullingTests.cs` のStandardRequest。
- https://docs.unity3d.com/6000.6/Documentation/Manual/urp/render-graph-compute-shader-run.html
- https://docs.unity3d.com/6000.6/Documentation/ScriptReference/Rendering.CommandBuffer.DrawProceduralIndirect.html

公式資料にあるcontext.commandBufferという表記は、このインストール版のcontext.cmdと異なるため実コードを正とする。

最終検証の実行一覧と数値は TestResults/M5/Results.json、要件§3〜§15との対応と未実証項目は Traceability.md を参照する。全EditModeは34件。総合判定はPASS_V0_1、packageは0.1.0。benchmarkの品質目標未達と性能上の制限は計測事実として保持し、性能優位を主張しない。

リリース回帰はリポジトリルートで `powershell -NoProfile -File TestResults/M5/Reproduce-Release01.ps1` を実行する。全EditMode、M0〜M5の機能検証、package、Player build/runを専用PID一つずつで再実行し、Release01Launches.jsonへ終了結果を保存する。続いて `python TestResults/M5/Verify.py` で正式benchmark raw 6,480件を再計算し、31ログ、原本108 files、集約判定、SHA-256 manifestsを検査・生成する。benchmark自体の再計測はBenchmark.mdの手順を使う。

証跡の生成順は、集約 Results → PackageManifest → ArtifactManifest → FinalIntegrityAudit の一方向です。PackageManifest は package 内ファイルを列挙し、ArtifactManifest は証跡と PackageManifest を列挙します。ArtifactManifest 自身と FinalIntegrityAudit は両 manifest の対象外です。Verify.py は確定済み manifest をディスクから読み直して全対象の hash を独立照合し、最後に FinalIntegrityAudit を毎回再生成します。最終監査にはその実行で確定した両 manifest の SHA-256 を記録し、その後は参照先を書き換えません。時刻を更新するため再実行間の manifest hash が変わることは正常ですが、各実行後の最終監査との一致は必須です。

生成順の回帰確認は `python TestResults/ManifestIntegrityRegression.py` で行います。Verify.py を2回連続で実行し、各回の終了値、全対象ファイルの hash、最終監査の manifest hash と非循環性を独立に検査します。実行後に書く回帰報告は `TestResults/ManifestIntegrityRegressionResults.json` に置き、manifest の入力へ含めません。保存済み機能テストの再集約なので、この操作は Unity/Player を起動しません。
