# 描画と URP 統合

VirtualGeometryRenderFeature は通常 opaque 後の `AfterRenderingOpaques` に compute 選択と procedural indirect draw を接続します。引数・可視 index・refinement state・debug 出力は instance × camera で分離し、immutable geometry と streaming residency は共有します。通常 opaque で作られた active depth から各 camera 専用 HZB を生成します。

pass は `ConfigureInput(Depth)` と `requiresIntermediateTexture` で sampling 可能な中間 depth を要求します。実 Scene View の自動 repaint では built-in depth に descriptor がなく、宣言を欠いた旧実装は CopyDepth で例外になりました。入力宣言を加えた lifecycle 再検証では RenderGraph 例外 0 です。pipeline asset の設定を書き換える処理ではありません。

URP 17.6 のローカル `UniversalRendererRenderGraph` と `CopyDepthPass.Render` を確認し、VG が書いた active depth を `cameraDepthTexture` へ再同期します。CopyDepth AfterOpaques / AfterTransparents で通常 mesh の前後と transparent depth consumer の一致を実測しました。ForcePrepass、Depth Priming、DepthNormals 要求は Proxy を使います。ユーザーの pipeline 設定は自動変更しません。

## Material と Virtual Geometry Shader

見た目は MeshRenderer と同じく Renderer の Materials(submesh 順)で決めます。専用描画はその Material の shader の fragment 処理・keyword・property をそのまま実行し、頂点入力だけを VG の可視 index / 頂点 buffer から組み立てます。

- Virtual Geometry Shader(`.vgshader`)は参照先 shader(.shader / Shader Graph)から、元の全 pass に `VirtualGeometryForward` pass を 1 つ加えた Shader を ScriptedImporter で生成します。追加 pass は元の forward pass(`UniversalForward` / `UniversalForwardOnly` / LightMode なし)の複製で、`#pragma vertex` だけを VG の頂点入力 adapter に差し替えます。adapter は元の vertex 関数の入力 struct を semantic で埋めます(POSITION・NORMAL・TEXCOORD0 は VG 頂点、TANGENT は三角形の位置と UV から作る面 tangent、COLOR は白、UV1 以降と instance id は 0)。参照先を編集すると再 import されます。
- 使い方: shader を右クリック →「Virtual Geometry Shaderを作成」で `.vgshader` を作り、Material の shader に `VirtualGeometry/<元の名前>` を選びます。この Material は通常 Mesh・Proxy・影にもそのまま使えます。 `.vgshader` の Inspector は生成 shader 名と import エラーの有無を示し、「このshaderを使うMaterialを作成」で Material を作れます。Renderer の Inspector は、対応 shader の無い Material に「Virtual Geometry Shaderを作成して切り替え」ボタンを出し、`.vgshader` の作成と Material の shader 切替を確認のうえ行います(Undo 可、property の値は保持)。
- URP 標準の Lit / Simple Lit / Complex Lit / Unlit 用の `.vgshader` は package(`Runtime/Resources/VirtualGeometryShaders`)に同梱しています。普通の URP Material を割り当てた場合は、`VirtualGeometry/<元 shader 名>` の shader へ property と keyword を写した内部 Material で描きます。元 Material の編集は、Editor では編集した時点、実行時は最大 30 frame 後に内部 Material へ写します。
- 描けない Material(半透明、対応する Virtual Geometry Shader が無い shader、変換できない shader)が 1 つでもあれば、instance は理由付きで通常 Proxy になります。変換できない shader は `.vgshader` の import エラーに理由が出ます(Built-in / HDRP、Deferred 専用、geometry / tessellation stage、vertex 関数の入力 struct を解決できない等)。

procedural draw には Unity の per-object light list と light probe が付かないため、URP shader が読む値を明示して渡します。matrix は `DrawProceduralIndirect` の引数で `unity_ObjectToWorld` に入ります。環境光は `RenderSettings.ambientProbe` の SH(`unity_SHA*` 等)、Forward の追加 light は visible light 順から主光源を除いた URP の追加 light 番号(先頭 8 個、`unity_LightData` / `unity_PackedLightIndices`)です。Forward+ は URP の cluster から light を読みます。環境反射は既定の反射 cubemap(`ReflectionProbe.defaultTexture`、skybox など)を `unity_SpecCube0` / `unity_SpecCube0_HDR` として渡します(未設定だと黒になり、通常 Renderer より暗く見えます)。負の scale は `unity_WorldTransformParams.w` と culling の反転で通常 Renderer と同じ面を残します。個別 light probe / reflection probe / APV / lightmap は対象外です。

debug view(Triangles / Clusters / LOD Level 等)は package の VG Unlit shader で描きます。depth、影受け、低密度 Proxy の影落としは別機能であり、本 geometry の直接 shadow rendering とは呼びません。対応外機能は SupportMatrix に明記します。
複数 material の indirect command はそれぞれ独立した可視 index 範囲を参照します。DX12 の draw-local SV_VertexID に Unity 同梱 UnityIndirect.cginc の GetIndirectVertexID_Base を適用し、GPU arguments の startVertex を解決します。VG pass・debug view と legacy 全常駐形式にも同じ補正を使います。

## CPU 負荷の設計(部品が多い scene)

部品(VG の GameObject)が数百ある scene で、VG の CPU 時間を部品数に比例させない工夫です。space_elevator(419 部品・25 Mesh)の 1 camera あたりの CPU 時間は、398 ms/frame(Main + 平面反射)から 43 ms/frame になりました(計測は `SpaceElevator/README.md`)。

- **視錐台の外の部品は pass を作らない**: 描画 pass の記録前に、部品の world bounds を camera の視錐台で CPU 判定し、外なら選択・描画・feedback の項目を作りません。`SetCullingSettings` の frustum が false の部品は判定しません(比較基準モード)。そのため選択状態(instance × camera)は、その camera が描いた部品にだけ作られます。
- **pass は部品ごとでも全部でもなく、一定数ずつまとめる**: 選択・描画・feedback の項目を camera ごとにまとめて集め、`PassBatchSize`(12)件ずつ 1 pass にします。部品ごとに pass を作ると RenderGraph の記録・実行が部品数に比例して重く(419 部品で 124 ms)、全部を 1 pass にすると pass が巨大になり CPU も GPU も遅くなります(162 ms)。8〜16 が最小でした。
- **Material の解決結果は部品の slot ごとに覚える**: `.vgshader` を持たない shader(URP Lit 等)は、解決のたびに内部 Material へ全 property と keyword を写すため、毎 frame 繰り返すと 1 camera の記録の大半を占めました。結果は 30 frame 覚え、Editor では元 Material の dirty count が変わった時点で、Material の差し替えや Reload でも作り直します。
- **光源・環境光・反射の値は draw 間で使い回す**: 描画命令は MaterialPropertyBlock の値を記録時にコピーするため、1 pass の中で 1 つの block を使い回し、光源の layer や巻き順が変わったときだけ設定し直します。
- 毎 frame の確保をなくしています(部品ごとの項目・光源・imports を使い回し)。space_elevator の管理ヒープの確保は 1.48 MB/frame から 5 KB/frame になりました。

## GPU の選択カーネル

`VirtualGeometrySelect.compute` の `Select` は、material ごとに 1 group(64 lane)で動きます。階層の走査(DFS)・cull・統計・要求は lane 0 が直列に行い、採用した cluster を最大 32 件の塊として groupshared に溜め、塊ごとに 64 lane で可視 index を並列にコピーします。以前は 1 thread が全 index のコピーまで直列に行い、space_elevator では VG の GPU 時間の大半(約 26 ms)を占めていました。出力する可視 index の並びと args の値は変わりません(CPU 参照との一致は M2/M3 の検証で確認)。同じ条件の A/B 比較では、Select の GPU 時間は約半分以下になり、カメラ移動中のフレーム時間は約 14% 短くなりました。残りは lane 0 の直列部分(走査と cluster ごとの cull)です。

Profiler には `VG.OnCameraPreCull`、`VG.RecordRenderGraph`、`VG.Record.Gate` / `Select` / `Draw` / `Lights` / `Feedback`、`VG.SetCameraSupport`、`VG.ApplyDecision`、`VG.BeforeCamera`、`VG.Update`、`VG.TryGetGpuPage` の marker があります。

## GameObject Inspector の「Virtual Geometry」欄

VG で描く GameObject は通常の MeshFilter + MeshRenderer を持ち、VG とつなぐ部品は Inspector に表示しません。Material・影の有無は MeshRenderer で設定します。Inspector 上部の「Virtual Geometry」欄に次を表示します。

- 状態: LOD 段数・三角形数・形状データ量・GPU 使用量と、現在の表示(VG / 代替表示とその理由)。
- LOD の閾値が 0(常に最も細かい段)や最大値(常に最も粗い段)で距離による LOD 変化が起きないときの警告。
- 描けない Material の理由と、対応する Virtual Geometry Shader が無い Material の「作成して切り替え」ボタン。
- URP Renderer が専用描画できない設定(Virtual Geometry Renderer Feature が無い、Deferred、Depth Priming、Depth Prepass、MSAA)なら、必要な変更の一覧と「URP RendererをVirtual Geometry用に設定」ボタン(確認のうえ適用、Undo 可)。互換を宣言しない他の Renderer Feature は自動では外しません。
- 詳細: LOD の閾値、切替の余裕、読み込み方式、streaming 予算(GPU geometry、CPU cache、I/O 並列、frame 転送、要求上限、取消 frame)、変換データ(読み取り専用)。
- LOD 閾値・Material の変更は、GPU 資源を作り直さずに反映します。変換データと streaming 設定を変えたときだけ再読込します。
- 表示しない: 代替表示の強制、debug view、Frustum / Occlusion culling(Scene view toolbar で操作)、compute 非対応の模擬と I/O 遅延注入(検証 API 専用)。

