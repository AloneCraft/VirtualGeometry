# Space Elevator(space_elevator.fbx の質感・照明)

`Assets/VirtualizedGeometrySample/space_elevator.fbx` を、元になった参照画像「地上が、宇宙へのはじまり。」の質感・照明・色調で Virtual Geometry(VG)描画するためのサンプル。

- FBX の出所: `G:\Blender\AI\Claude\起動エレベーター地球宇宙ステーションMax\space_elevator.blend`(Blender 5.2.1、シーン `SE_GroundAnchor`)。
  - 書き出し方: シーンの Mesh を、非表示の素材用オブジェクトも含めて選択なしで書き出す。FBX の書き出し設定は既定のまま。
  - 外すもの: 遠景の `SE_GA_Terrain`(約 35 万三角形で、施設全体の約 3 倍)、`SE_GA_Lake`、`SE_GA_CloudLayer`。描画を軽くするためで、広場の外は skybox の地平になる。
  - 結果: 25 Mesh・419 部品・30 マテリアル。
- 参照画像: `G:\Blender\AI\OpenAI\起動エレベーター_地球駅・月駅\references\Ground_Station_Reference.png`(1448×1086)。Blender のカメラ `SE_GA_Camera` が同じ構図。repo には入れない。

## 使い方

1. `Tools/Virtualized Geometry/Space Elevator/Build Scene` を実行する。マテリアルの更新、専用パイプラインへの切替、`SpaceElevator.unity` の生成と環境光の焼き込みをまとめて行う。
2. `SpaceElevator.unity` を開く。Game view が参照画像と同じ構図。Play 中は W/S/A/D/Q/E で移動できる(Shift で速く)。

M0〜M5 の検証は既定の render pipeline を自分用に切り替える。その後でこの scene を見るときは、`Tools/Virtualized Geometry/Space Elevator/Use Space Elevator Pipeline` を実行する。

## 構成

| パス | 内容 |
|---|---|
| `Shaders/SpaceElevatorProcedural.shader` | 手続き質感。`_Recipe` で Panel / Steel / WetGround を切り替える |
| `Shaders/SpaceElevatorProcedural.vgshader` | 上の shader に VG 用 pass を加えた `VirtualGeometry/SpaceElevator/Procedural` を生成する。Material はこちらを直接使う |
| `Shaders/*.hlsl` | ノイズ(改良 Perlin + PCG3D)、質感レシピ、forward pass、Material property |
| `Materials/SE_GA_*.mat` | FBX の 30 マテリアルの置き換え。FBX の importer がここへ remap する |
| `Scripts/SpaceElevatorPlanarReflection.cs` | 濡れた広場の平面反射。Main Camera に付ける |
| `Rendering/` | 専用パイプライン・Renderer・Volume profile・skybox・Lighting Settings |
| `SpaceElevator.unity` | 参照画像の構図・照明で FBX を置いた scene |
| `Editor/` | マテリアル・scene の生成、座標規約、検証、EditMode テスト |

## 仕組み

### 質感

Blender のマテリアルは全部オブジェクト座標の手続きノード(UV なし)で、FBX は定数しか運べない。ほとんどのジオメトリに UV が無いので、テクスチャへの焼き込みもしない。

代わりに、Blender の生成スクリプト(`scripts/se_common.py` の `panel_pattern` / `mat_panel` / `mat_steel`、`se_ground_anchor.py` の `mat_wet_ground`)の式を HLSL の骨格として移植した。そのうえで参照画像に寄せる要素を足している。

- 雨だれ汚れ、縦リブ、コンクリートの粒、暖色と寒色のムラ
- 点灯窓の割合
- 水たまりの範囲

外部テクスチャは使わない。

単純な Principled(16 個)は URP Lit / Complex Lit(Clear Coat)、Emission(5 個)は URP Lit の黒ベース + HDR Emission にする。

値の正本は `Editor/SpaceElevatorMaterialBuilder.cs` のレシピ表。

- 初期値は Blender の引数で、各行に元の行番号を書く。参照画像に合わせて変えた値には理由をコメントで残す。
- Material を Inspector で変えても、`Build Materials` / `Build Scene` を実行するとレシピ表の値へ戻る。値を変えるときはレシピ表を直す。

### 座標規約(`Editor/SpaceElevatorPatternSpace.cs`)

- FBX の頂点は Blender のオブジェクト座標(m、Z 上)。
- Unity Mesh 座標 = Blender 座標の (−x, y, z) × 0.01。shader は `_PatternScale` = 100 で m に戻して模様を計算する。
- prefab を原点・無回転に置くと、Unity world = Blender world の (−x, z, −y)。

### 照明と露出(`Editor/SpaceElevatorSceneBuilder.cs`)

- 値は Blender と同じ放射量で持つ。
  - 太陽: 9 W/m²。URP の拡散は 1/π を掛けないので intensity 9/π
  - 発光: 強さ 22〜30
- 太陽の向きと色は参照画像に合わせる。Blender(左手前から仰角約 29°、色 (1,0.9,0.78))より正面寄り・低め(仰角約 20°)・暖色(1,0.8,0.6)にして、正面の壁を夕方の光で照らす。
- 露出は Volume の Post Exposure で合わせる。Blender は −2 EV だが参照画像より暗くなるため −0.9 EV。Bloom は露出の前に掛かるので、threshold 1.0 が Blender の glare と同じ意味になる。
- 空からの環境光は 0.8 倍。1 倍では青い環境光が勝ち、参照画像より青くなる。
- 色調は次の Volume で参照画像に寄せる。Blender の AgX は URP に無い。
  - Tonemapping(ACES)、Color Adjustments(コントラスト)
  - White Balance(暖色寄り)、Shadows Midtones Highlights
  - Bloom、Vignette
- 空は Skybox/Procedural。環境光と既定の反射は焼いた LightingData から取る(VG はこの2つだけを使う)。

### 平面反射(`Scripts/SpaceElevatorPlanarReflection.cs`)

- 仕組み:
  - Main Camera を広場の面(y = 0)で鏡映した反射カメラを持つ。半解像度・mip 付き RT へ、Unity の camera 順序で Main Camera より先に描く。
  - 鏡映で巻き順が逆になるので、反射カメラの描画中だけ `GL.invertCulling` を立てる。
  - 反射カメラも Game 種別の Base camera なので、VG は専用描画する。
- 反射テクスチャは Main Camera の描画中だけ有効で、WetGround レシピが画面座標で読む。それ以外の camera(Scene view など)では空の反射に戻る。
- Game view の描画負荷はおよそ 2 倍になる。

## 検証

- EditMode(GPU 不要):
  - `SpaceElevatorPatternSpaceTests`: 座標規約
  - `SpaceElevatorMaterialBuilderTests`: 資産とレシピ表・FBX の同期、VG で描けること
  - `SpaceElevatorSceneBuilderTests`: パイプラインと scene の構成
- 実 GPU の検証は、開いている Editor を `Tools/Close-UnityEditor.ps1` で閉じてから次を実行する:

  ```text
  Unity.exe -batchmode -force-d3d12 -projectPath <project> -executeMethod VirtualizedGeometry.Samples.SpaceElevator.Editor.SpaceElevatorValidation.Run -logFile Logs/SpaceElevatorValidation.log [-referenceImage <参照画像>]
  ```

  - 出力先: `TestResults/SpaceElevator/after/`。参照画像との横並び `SE_GA_Camera_vs_Reference.png`、領域ごとの平均色、VG の描画三角形数を出す。
  - 確認すること:
    - 全部品が Main と反射カメラの両方で VG 描画される
    - 平面反射が広場に効いている
    - 正面ゲートを 30 m から見て、VG と元 Mesh の通常描画で模様が一致する

## 性能の計測

`Editor/SpaceElevatorPerf.cs` が、条件別(反射・影・VG・後処理・解像度・カメラ移動)に 1 frame あたりの CPU 時間、GPU の完了待ちを含む時間、管理ヒープの確保量、VG の項目数を測る。開いている Editor を `Tools/Close-UnityEditor.ps1` で閉じてから実行する:

```text
Unity.exe -batchmode -force-d3d12 -projectPath <project> -executeMethod VirtualizedGeometry.Samples.SpaceElevator.Editor.SpaceElevatorPerf.Run -perfLabel <名前> [-perfQuick] [-perfProfile] -logFile Logs/SpaceElevatorPerf.log
```

- 出力: `TestResults/SpaceElevator/perf_<名前>.json` と `.txt`。`-perfQuick` は基準と移動中の 2 条件だけ。`-perfProfile` はその 2 条件の Profiler marker 別の内訳(`VG.*`)も出す。
- Scene view は batchmode に無いため、Game 種別の Main Camera で測る(同じ pipeline・VG 描画経路)。Scene view では平面反射カメラは描かれない(反射は Main Camera の描画中だけ有効)ので、Scene view の負荷は「反射なし」の値に近い。

1448×1086・RTX 4060 Ti・DX12・静止カメラ・影あり(419 部品、描画する選択項目 238、draw 1118):

| 条件 | 最適化前 | 現在 |
|---|---|---|
| Main + 平面反射、CPU | 398 ms | 43 ms |
| Main + 平面反射、GPU 完了待ち込み | 480 ms | 77 ms |
| Main のみ(反射なし)、CPU | 182 ms | 22 ms |
| Main のみ(反射なし)、GPU 完了待ち込み | 223 ms | 36 ms |
| 管理ヒープの確保 | 1.48 MB/frame | 5 KB/frame |

GPU では、`Select` カーネルの index コピーが 1 thread で直列だったことが VG の GPU 時間の大半を占めていたため、64 lane で並列にコピーするようにした(A/B 比較で Select の GPU 時間は約半分以下、移動中のフレーム時間は約 14% 短縮)。

支配的だった原因(Profiler の marker で確認)は、(1) 部品 × camera ごとの GPU feedback の readback(D3D12 の staging resource 作成)、(2) 部品 × camera ごとの RenderGraph pass、(3) 部品 × slot × camera ごとの Material 解決(URP Lit 等は内部 Material へ全 property を写していた)。影・後処理・解像度は、この scene の主因ではなかった(影をなくしても時間はほぼ変わらない)。設計は package の `Documentation~/Rendering.md`「CPU 負荷の設計」を参照。

## 既知の差・制限

- 形状の細かさは FBX のまま。参照画像の配管や小部品の凹凸は、質感で補う範囲にとどまる。
- 参照画像の遠景にある山並み・湖・積雲は無い。軽さのため FBX から外したので、広場(半径 1.45 km)の外は skybox の地平と無地の空になる。
- 描画機能の制限:
  - GI と AO は無い。SSAO・SSR は VG を Proxy に落とすので使わない。
  - 反射は空と平面反射だけ。個別の Reflection Probe は VG が使わない。
  - 影は VG の仕様で、低密度の代替 Mesh から落ちる。小さい部品の影は欠ける。
- 模様のずれ:
  - VG の位置量子化(Mesh の bounds で 16 bit)で、模様は最大 ±2.2 cm ずれる(広場)。
  - 粗い LOD では模様がわずかに動く。
- Import.unity は URP の Lighting サンプルのスタジオ照明のまま。FBX の太陽とカメラを取り込まなくしたので、そこではエレベーターが暗く見える。確認は `SpaceElevator.unity` で行う。

## 実行して見る

`Import.unity` は、space_elevator を含む照明の sample scene で、Play すると、カメラがモデル全体の見える位置から始まる(`VirtualGeometrySampleCameraController` の frameOnStart)。W/S/A/D/Q/E で移動、Shift で加速、右ボタンを押している間はマウスで向きを変える。`M0.unity` は四角形 2 枚だけの検証用 scene で、複雑なモデルは無い。
