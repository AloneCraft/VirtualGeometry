# 要件対応と実証範囲

正本はリポジトリ `Requirements/VirtualizedGeometry.md`。設計・実装・実GPUで確認した範囲を分けます。配布状態は **0.1.0**、総合判定は **PASS_V0_1** です。M0〜M5とv0.1の必須範囲を実装・検証しました。GPU時間は公開APIで取得済みです。M6には着手していません。

## M0～M5

| 段階 | 実装 | 実GPU・自動検証 | 残る境界 |
|---|---|---|---|
| M0 | Computeから間接triangle、URP RenderGraph | M0/Results、前後の通常quadとのdepth | 基礎probe |
| M1 | cluster化、保存/CRC読込、legacy全常駐 | M1/Results、1536 triangles / 24 clusters / 1 dedicated draw、fallback=false、silhouette差0 | 全cluster比較mode |
| M2 | 境界固定した置換group階層、CPU参照/GPU選択、error/hysteresis | M2/Selection.csv、CPU/GPU node・index・args一致、近/混在/遠のLOD | errorはsimplifier尺度と量子化誤差の累積。全視点の厳密幾何上界とは主張しない |
| M3 | 現frame HZBとFrustum、camera毎transient | M3、Z両方向、opaque移動/teleport/resize/ortho/負scale/near | VG同士の遮蔽はHZB入力に含まない |
| M4 | v3 page、root pin、async range I/O、queue、固定slot、fence/generation、chunk upload | M4、unique bytes > pool、欠損/CRC/CPU拒否/cancel、depth欠落0、unsafe reuse0 | 小budget時は品質未達を維持して計測 |
| M5 | Lit/Proxy/共有instance/camera/UI/debug/Player/benchmark | M5の機能別Results、Geometry、PlayerBuild/PlayerRun、Benchmark{724,1224,2236} | benchmark CPU/GPU 6,480 samplesずつ、欠測0。第三者上流原本108 filesは意図的に保持し、Playerへの不要source/native収録は0 |

## 要件 §3～§15

| 要件 | 現在の設計・実装 | 実証と制限 |
|---|---|---|
| §3 静的opaque・instance・transform | asset/settings単位のgeometry/cacheを参照共有、選択はinstance×camera。移動/負/非一様TRSを処理、shear/非有限/ゼロは診断Proxy | Shared/Debug/Lifecycle/Player、TransformCompatibilityテスト。描画呼出しはmaterial×instance単位で、1 drawへまとめるhardware instanced batchingは未実装 |
| §4 独立UPM・data契約 | Runtime/Editor/Shaders/Tests/Samples~/Documentation~。version/size/offset/CRC、C#/HLSL stride検証 | Package/Results、codec/formatテスト。元meshを破壊しない |
| §5 変換・階層 | 不正index/NaN拒否、縮退/非多様体等診断、material/attribute境界保持、決定的group簡略化、global量子化。rootが1 cluster以下になるまで単一子の段を追加、全軸共通スケールのMorton順と子を包む境界球(builder 7) | Builder/Hierarchy/Nativeテスト(`Build_ContinuesLevelsUntilRootFitsOneCluster`、`Build_KeepsClustersCompactAndBoundsEnclosingOnThinSurfaces`を含む)、小階層の全選択組合せ。Geometry suiteでは6形状のleafおよびCPU粗cutと画像/depth/属性一致。球root512→64時のsourceとの差5961pxはLOD品質差として記録 |
| §6 pixel error | projection/FOV/height/ortho/near/scaleを使うCPU/GPU式、hysteresis、resident祖先へ原子的退避 | M2、M4、Culling tests。予算不足・未到着で目標を満たしたと報告しない |
| §7 GPU選択・compaction | GPU tree選択、cluster cull、material別visible index/indirect args。DX12 draw-local vertex IDへUnity公式startVertex補正 | M2/Geometry/Player。本番選択でCPU全cluster走査・同期draw数readbackはしない。diagnosticは明示別経路 |
| §8 HZB | opaque現frame depth、normal=max/reversed=min、危険投影はkeep | M3 synthetic mipと7画像のculling off/on比較。履歴共有なし。Normal Cone Cullingは未実装 |
| §9 streaming | metadata/root常駐、全child page residentでrefine、FileStream range I/O、priority/dedup/overflow/re-request、CPU/GPU/I/O/transfer個別予算。page優先度は要求元の親screen error、feedbackはinstance×camera単位 | M4/Player/benchmark。GPU completion fenceとgeneration、page tableを先にnonresident化。transfer中は未公開。root前はProxy。EditMode `FeedbackLoopKeepsSelectedCutStableUnderBudgetAndAsyncFeedback` が低予算・遅延feedback下のcut非振動と後段cameraの詳細化を検証(M5集約結果への再記録は未実施) |
| §10 URP/Lit/depth/shadow | Forward RenderGraph。専用Lit、main shadow受け。root ShadowsOnlyによるcast、Collider/picking。depth inputとintermediate依存、VG後camera depth同期 | Lit/Depth/Geometry。前後通常mesh・transparent・depth texture一致。prepass/normal/primingは診断Proxy。対応材質はSupportMatrixを正とし任意URP/Lit/ShaderGraph互換は主張しない |
| §11 UI/debug | UI Toolkit、snapshot→cancel可能worker→atomic commit、cache invalidation/Undo、各debug modeと実stats。debug操作はScene view overlay toolbar、camera別の選択状態は非同期Monitor window | EditorResults/DebugResults。OnValidateは再構築をUpdateへ延期。同期diagnostic/overlayを通常性能から除外 |
| §12 Omni | 標準Unity local vertices/object matrix/camera/projectionを分離。変換dataはcamera非依存 | 現projectにOmniなし。Omni adapter/対数depth/高精度論理座標は未実装・未実証、v0.1の外 |
| §13 正しさ・性能 | 34 EditMode tests、GPU regression、同一asset/軌道/material/1920/VSync0の通常/LOD/VG比較 | 1,048,352 / 2,996,352 / 9,999,392 trianglesを実変換・実描画。可視swapchain/queue深度1の公開FrameTimingでCPU/GPUを取得、各run cold/warm 360 samples、欠測0。CPU/FPS/memory/I/O/quality missを記録。性能優位と同等品質は示していない |
| §14 段階判定 | M0～M5とv0.1必須範囲を完了 | PASS_V0_1。下記の対応外・未実証範囲と性能制限を明示する。M6は後続 |
| §15 納品 | source/UPM/UI/runtime/sample導線/tests/docs/repro/licenses/results | Player収録検査でhigh source/native不要物なし、archive重複なし。meshoptimizer v1.1のlicense/version/build手順/Editor・Player必要物を記録。Native~の上流原本108 filesは再現性のため保持し、削除を納品条件に含めない |

## 画像・形状の対応

M1の波状平面、M2/M3/M4の境界付きterrainに加え、M5 Geometryは球、厚さ0.01の薄板、幅0.025の細柱、鋭いcube角、内半径1.15の穴、鏡像UV seamを持つ2material面を使います。legacy 2material面も比較します。leafは元mesh、粗LODはCPUが選んだ同じcutを基準とし、欠落・silhouette・raw depth・色を独立集計します。source→coarse差は品質差として別記録します。UV/normalはsource attributeがleaf dataへ残ることとテクスチャ画像の両方で確認します。

現在のSphereは512 triangles、薄板/細柱/鋭角は12 trianglesで、それらすべてが高密度入力の任意形状を代表するという主張はしません。Mixed LOD接続と遅延到着の地形検証を併用しています。

## 対応範囲・計測上の制限

- GPU時間は取得済み。可視swapchainと公開DX12 queue深度1を使い、全9 runで各phase 360正値を取得しました。旧Hidden/offscreenの未取得とsampler probeの失敗はGpuTimingInvestigationに履歴保存し、正式計測に混ぜていません。
- benchmark: 全経路を可視の通常camera/swapchain、VSync0、maxQueuedFrames1で測定。CPU frame msは待ちを含み、FPSはPlayer-loopのframe cadenceです。OS cacheは制御していません。benchmark軌道の数値depth比較は未実施で、機能depth検証は別suiteです。
- benchmark VGは8MiB budgetでwarm品質目標未達を継続。1M～10MでCPU時間が従来方式より長く、性能優位を主張しません。
- native原本保持: meshoptimizer v1.1の108 filesを意図的に保持します。src-only buildで未使用の13 top-level paths（85 files、1,563,139 B）も再現可能な第三者原本であり、v0.1のblockerではありません。backup/hash/Release build再現と過去の整理検討はNativeCleanup証跡に保存します。将来の任意整理とは別に、Editor DLLと不要source/nativeをPlayerへ収録しない境界を検証しています。
- 旧GPU Profiler有効probeの終了診断と個別samplerの空Count例外はGpuTimingInvestigationに保存しています。正式9 runではProfiler/GPU Profilerを無効にし、同例外とnative leak診断は0です。

最終の各実行ログ、件数、日時は `TestResults/M5/Results.json` と個別Resultsを確認してください。`TestResults/M5/Reproduce-M5.ps1` は各suiteを1プロセスずつ実行します。`TestResults/M5/Reproduce-Release01.ps1` は全EditMode・16機能scopeを正式集約と一致する出力先へ連続再実行します。
保存済み証跡の集約検証はリポジトリルートから `python TestResults/M5/Verify.py` で実行します。これは記録済みの最終 run の結果・上限・警告を再検査し、PackageManifest / ArtifactManifest の SHA-256 を再生成する処理です。Unity の各テスト自体を再実行する操作とは分けています。
