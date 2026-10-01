# 対応表

対象実機は Windows 11、RTX 4060 Ti 16 GB、Unity 6000.6.0f1、URP 17.6、DX12 です。package 0.1.0（PASS_V0_1）の対応範囲です。下表の実証はリポジトリ `TestResults/M5` の機能検証を指します。最終全回帰は別途 Development に記録します。

| 項目 | 現在の実装と境界 | 実証 |
|---|---|---|
| 変換の入口 | Unity 標準 ModelImporter の全形式(FBX・OBJ・DAE・3DS・DXF・.blend 等)で、モデルの「Virtual Geometry」チェックによりインポート時に変換。GameObject は MeshFilter(低密度の代替 Mesh)+ MeshRenderer のまま VG で描画。glTF・USD 等の独自インポーター、`.asset` の Mesh、SkinnedMeshRenderer は対象外 | OBJ(EditMode・全検証 scene)。FBX 等は同じ ModelImporter 経路だが実ファイルでは未実証 |
| 描画 | URP RenderGraph、Forward / Forward+、opaque、SM5、compute、indirect | Editor / Development Player |
| camera | 独立 Base Camera、perspective / orthographic、Game camera / Scene View | 2 Base + Scene View、順序変更 |
| transform | 移動、負 scale、非一様 scale。shear / ゼロ / 非有限は診断付き Proxy | 画像比較 + 境界テスト |
| HZB | 通常 opaque の現フレーム depth。カメラ毎の transient texture、履歴なし | M3 |
| streaming | v3、root pin、page-local geometry、非同期 I/O、固定 pool、chunk upload | M4 / Player |
| instancing | asset + 互換設定の geometry / metadata / page table / cache を共有、選択状態は instance × camera | 2 instances、資源 1 個 |
| Material / shader | Material の shader を Virtual Geometry Shader(`.vgshader`)経由で実行。URP 標準 Lit / Simple Lit / Complex Lit / Unlit は同梱、利用者の URP .shader / Shader Graph は `.vgshader` を作成。Emission・Alpha Clip・Detail・Clear Coat 等は元 shader の処理そのもの | M5 Lit(再検証待ち)、変換器 EditMode テスト |
| normal | 三角形の位置と UV から作る面 tangent(接線 stream は保持しない) | 平面、負 UV scale、負/非一様 object scale(再検証待ち) |
| lights | Forward: main light と先頭 8 個までの追加 light(per-object index を明示供給)。Forward+: URP cluster。light culling mask を適用 | main / point / spot(再検証待ち) |
| ambient | RenderSettings.ambientProbe の SH を明示供給。個別 light probe / APV は対象外 | URP Lit との差 0 |
| reflection | 既定の反射 cubemap(skybox 等)を明示供給。個別 reflection probe は対象外 | Proxy(通常 URP Lit)との色差 0(M5Depth) |
| fog | URP fog | 比較 |
| shadow receive | main light shadow map | 通常 caster → VG |
| shadow cast | MeshRenderer の影設定に従い、代替 Mesh の ShadowsOnly renderer で落とす | Proxy → 通常 receiver |
| physics / picking | 代替 Mesh の MeshCollider(利用者が付ける、または検証 API の `SetProxyOptions`)と Editor ray picking | 実 raycast / picking |
| depth | active depth へ書込み、VG 後に camera depth texture を再コピー | 前後通常 mesh、後続 transparent depth consumer |
| DepthOnly / DepthNormals / Priming | 先行 prepass を必要とする設定は理由付き通常 Proxy | 実 depth / normals 比較 |
| fallback | root 未準備・読込拒否・backend override・互換外設定は Proxy、同じ camera で排他 | Editor / Player |

専用 geometry の直接 ShadowCaster、lightmap / APV / 個別 light probe、skin/morph、transparent、UV1 以降・vertex color を使う表現、Built-in / HDRP の shader、Deferred、MSAA、XR、camera stack は対応表へ含めません。Material の vertex 関数は VG でも実行されますが、頂点を動かす shader では culling / LOD の bounds がその変位を含みません。互換を宣言しない追加 RendererFeature は安全側の通常 Proxy へ切り替えます。`IVirtualGeometryCompatibleRenderFeature` は先行 geometry の必要性を明示する契約です。

面 tangent は元 Mesh の authored tangent と一致する保証がありません。UV が特異な三角形では法線に直交する任意の向きを使います。高密度 detail と root Proxy の形状差は triangle 数と root geometric error を Inspector で確認します。