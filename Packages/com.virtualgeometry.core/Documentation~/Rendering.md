# 描画と URP 統合

VirtualGeometryRenderFeature は通常 opaque 後の `AfterRenderingOpaques` に compute 選択と procedural indirect draw を接続します。引数・可視 index・refinement state・debug 出力は instance × camera で分離し、immutable geometry と streaming residency は共有します。通常 opaque で作られた active depth から各 camera 専用 HZB を生成します。

pass は `ConfigureInput(Depth)` と `requiresIntermediateTexture` で sampling 可能な中間 depth を要求します。実 Scene View の自動 repaint では built-in depth に descriptor がなく、宣言を欠いた旧実装は CopyDepth で例外になりました。入力宣言を加えた lifecycle 再検証では RenderGraph 例外 0 です。pipeline asset の設定を書き換える処理ではありません。

URP 17.6 のローカル `UniversalRendererRenderGraph` と `CopyDepthPass.Render` を確認し、VG が書いた active depth を `cameraDepthTexture` へ再同期します。CopyDepth AfterOpaques / AfterTransparents で通常 mesh の前後と transparent depth consumer の一致を実測しました。ForcePrepass、Depth Priming、DepthNormals 要求は Proxy を使います。ユーザーの pipeline 設定は自動変更しません。

## Material と Virtual Geometry Shader

見た目は MeshRenderer と同じく Renderer の Materials(submesh 順)で決めます。専用描画はその Material の shader の fragment 処理・keyword・property をそのまま実行し、頂点入力だけを VG の可視 index / 頂点 buffer から組み立てます。

- Virtual Geometry Shader(`.vgshader`)は参照先 shader(.shader / Shader Graph)から、元の全 pass に `VirtualGeometryForward` pass を 1 つ加えた Shader を ScriptedImporter で生成します。追加 pass は元の forward pass(`UniversalForward` / `UniversalForwardOnly` / LightMode なし)の複製で、`#pragma vertex` だけを VG の頂点入力 adapter に差し替えます。adapter は元の vertex 関数の入力 struct を semantic で埋めます(POSITION・NORMAL・TEXCOORD0 は VG 頂点、TANGENT は三角形の位置と UV から作る面 tangent、COLOR は白、UV1 以降と instance id は 0)。参照先を編集すると再 import されます。
- 使い方: shader を右クリック →「Virtual Geometry Shaderを作成」で `.vgshader` を作り、Material の shader に `VirtualGeometry/<元の名前>` を選びます。この Material は通常 Mesh・Proxy・影にもそのまま使えます。 `.vgshader` の Inspector は生成 shader 名と import エラーの有無を示し、「このshaderを使うMaterialを作成」で Material を作れます。Renderer の Inspector は、対応 shader の無い Material に「Virtual Geometry Shaderを作成して切り替え」ボタンを出し、`.vgshader` の作成と Material の shader 切替を確認のうえ行います(Undo 可、property の値は保持)。
- URP 標準の Lit / Simple Lit / Complex Lit / Unlit 用の `.vgshader` は package(`Runtime/Resources/VirtualGeometryShaders`)に同梱しています。普通の URP Material を割り当てた場合は、`VirtualGeometry/<元 shader 名>` の shader へ property と keyword を写した内部 Material で描きます。
- 描けない Material(半透明、対応する Virtual Geometry Shader が無い shader、変換できない shader)が 1 つでもあれば、instance は理由付きで通常 Proxy になります。変換できない shader は `.vgshader` の import エラーに理由が出ます(Built-in / HDRP、Deferred 専用、geometry / tessellation stage、vertex 関数の入力 struct を解決できない等)。

procedural draw には Unity の per-object light list と light probe が付かないため、URP shader が読む値を明示して渡します。matrix は `DrawProceduralIndirect` の引数で `unity_ObjectToWorld` に入ります。環境光は `RenderSettings.ambientProbe` の SH(`unity_SHA*` 等)、Forward の追加 light は visible light 順から主光源を除いた URP の追加 light 番号(先頭 8 個、`unity_LightData` / `unity_PackedLightIndices`)です。Forward+ は URP の cluster から light を読みます。負の scale は `unity_WorldTransformParams.w` と culling の反転で通常 Renderer と同じ面を残します。個別 light probe / APV / lightmap は対象外です。

debug view(Triangles / Clusters / LOD Level 等)は package の VG Unlit shader で描きます。depth、影受け、低密度 Proxy の影落としは別機能であり、本 geometry の直接 shadow rendering とは呼びません。対応外機能は SupportMatrix に明記します。
複数 material の indirect command はそれぞれ独立した可視 index 範囲を参照します。DX12 の draw-local SV_VertexID に Unity 同梱 UnityIndirect.cginc の GetIndirectVertexID_Base を適用し、GPU arguments の startVertex を解決します。VG pass・debug view と legacy 全常駐形式にも同じ補正を使います。

## Renderer Inspector

普段触る項目だけを常に表示し、既定値で足りる項目は「詳細」に折りたたみます。各項目には Tooltip があります。

- 常に表示: Mesh Asset、Lod Error Threshold Pixels(許容する画面誤差。大きいほど早く粗い段へ切り替わる)、Materials(描けない slot があれば理由を表示)、状態欄。Materials は Mesh Asset の submesh 数まで自動で広げ、既存の割り当ては保ちます。
- URP Renderer が専用描画できない設定(Virtual Geometry Renderer Feature が無い、Deferred、Depth Priming、Depth Prepass、MSAA)なら、必要な変更を一覧し「URP RendererをVirtual Geometry用に設定」で確認のうえ適用します(Undo 可)。互換を宣言しない他の Renderer Feature は自動では外しません。
- Material・LOD 閾値・影 / 物理 Proxy の変更は、GPU 資源と Proxy を作り直さずに反映します。Mesh Asset と streaming 設定を変えたときだけ再読込します。
- 詳細: Lod Hysteresis、影 / 物理 Proxy、Streaming Mode、streaming 予算(GPU geometry、CPU cache、I/O 並列、frame 転送、要求上限、取消 frame)。
- 表示しない: Proxy 強制、debug view、Frustum / Occlusion culling(Scene view toolbar で操作)、compute 非対応の模擬と I/O 遅延注入(検証 API 専用)。値と API は残しています。
