# 4 枚の平面とサッカーボール(MoonTriple)

Omni の対数描画(Radial)で、4 枚の平面を y=0 に置き、4 つの角を原点(x=0, z=0)で接します。角に置いたサッカーボール(直径 0.22 m)の食い込みで、面の高さを比べます。近くで見ると平らに見えるので、「面」と呼びます。

## 4 枚の面

| 面(画面の呼び名・色) | 大きさ | 作り方 | 象限 |
|---|---|---|---|
| **VG の面**(緑) | 一辺 1000 km(10 m × 10^5) | `PlanarTerrainSource`。Cell に分けて頂点を実行時に作る。最大階層は、Cell が 10 m 以下になる最小の値(自動。1000 km なら 17、Cell 約 7.6 m) | −x, −z |
| **Standard100Km**(青) | 100 km(10 m × 10^4) | 10 m の標準 Plane の mesh を、Logical で N 倍に拡大した 1 つの物体(素の拡大) | −x, +z |
| **Standard1000Km**(灰) | 1000 km(10 m × 10^5) | 同上(倍率 10^5) | +x, +z |
| **手動分割 1000m**(紫) | 1000 m(10 m × 100×100 枚) | 10 m の標準 Plane の mesh を 10,000 枚、Logical で並べた面 | +x, −z |

- 角が原点で接し、各面は原点から外側へ広がります(PlaneSeam と同じ考え方)。
- 倍率 N は `MoonTripleLayout`(`ScaleStandard100Km`、`ScaleStandard1000Km`、`ScaleVg`)と、手動分割の枚数(`TilesPerSide`)で変えられます。大きさは `BaseMeters` × 倍率です。

## 描画の仕組み

- **VG の面**は `RadialMoonGrid.vgshader`(格子つきの VG の材質)。
- **Standard と手動分割**は `Omni/RadialScaledPlane`(PlaneSeam と同じ素の面の材質)。格子の原点 `_GridOriginOS` と倍率 `_ObjectScale` は、材質に書きます。Play で書き換えません。
- **ボール**は Omni のサッカーボール(`CosmicPlanetLit`)。赤い線が、ボールの中心の高さです。
- 光: 主光源は `_CosmicKeyLight`(Omni の面とボール)と `_PlanetKeyLight`(VG の面)に同じ値を渡します。ボールの点光源 `_CosmicBallLight` と `_PlanetBallLight` は、ボールの中心から計算します。
- **既知の差**: Standard と手動分割は `RadialScaledPlane` を使うので、VG の面とは違い、ボールの影と接触の暗がりを描きません(ボールの点光源は VG の面だけ)。

## 操作(キーとボタン)

画面の左のボタンと、同じ番号のキーは同じ動きです(Play のときだけ動きます)。「角」は、4 枚の面が集まる中央の点(原点)です。

| キー・ボタン | 動き |
|---|---|
| **1** ボールを角へ戻す | ドリブルを止めて、ボールを角へ置く |
| **4** ドリブル 開始 / 停止 | 下の「ドリブル」を参照 |
| ドリブルの速さ(ゲージ) | 0.05 倍〜1 倍。左ほどゆっくり(最高の 1 m は変わらず、時間の進みだけが遅くなる) |
| **3** 画面の中央にボールを置く | 下を向いて押す。視線の先の点へ、「操作する面」の高さで置く(ボールの半分が面に埋まる)。ドリブル中は使えない |
| **5** ボールを今の場所のまま、操作する面に半分埋める | ボールの中心を「操作する面」の高さにする。ドリブル中は使えない |
| **2** 操作する面を切り替える | VG → Standard100Km → Standard1000Km → 手動分割 → VG |
| **[ ]** / 下げる・上げる(10mm、1mm) | 「操作する面」だけを上下に動かす(ボールは動かない。Shift で 10 mm、Ctrl で 0.1 mm) |
| **7 8** カメラの近づける限界 | 論理ニアクリップのプリセット(通常 0.03 m、1 mm、1 µm、1 nm、10 pm)。下の説明を参照 |
| ↑ ↓ / ← → / PageUp PageDown | ボールを y / x / z へ(1 mm。Shift で 10 mm、Ctrl で 0.1 mm)。ドリブル中は使えない |
| W S A D Q E | camera を飛ばす(前後・左右・下上) |
| 右ドラッグ、ホイール、Home End | camera の向き、前後 |
| C | camera をボールに向け直す |
| **H** 操作パネルを隠す・出す | 左上のボタンと同じ。隠すと、ボールの高さとドリブルの状態の 1 行だけが残る(パネルがボールを覆うため) |
| R | 全部を最初に戻す(ボール、面の高さ、操作する面、近づける限界、ドリブルの速さ) |

HUD に、ボールの高さ、ドリブル中か、操作する面、4 面の大きさと作り方と高さ、camera の位置が出ます(0.1 秒ごとに更新)。

### カメラの近づける限界(論理ニアクリップ)

**カメラ(Omni の `RadialCamera`)が映す一番近い距離**です。小さくすると、1 mm や 10 pm のような極端に近いものまで映せます。camera の設定の Near(0.3 m)は変えず、Omni の論理 near だけを変えます。VG の面の Cell の細かさも、この値を下限にして決まります。飛ぶ速さとホイールの量は、この値に比例して小さくなります。

### ドリブル

ボールを Unity の**物理エンジン**で上下に跳ねさせます(`MoonTripleDribble`)。

- ボールは Rigidbody + SphereCollider(半径 0.11 m、重力あり)。**X・Z・回転は固定**で、動くのは上下(Y)だけです。重力はプロジェクトの `Physics.gravity`(−9.81 m/s²)、反発係数は 1.0(`PhysicsMaterial.bounciness`)です。
- 地面は、「操作する面」の高さを表す kinematic の BoxCollider です。衝突面は**面の高さ − ボールの半径**に置くので、ボールの中心が面の高さに着いたところで衝突します。つまり、ボールの**半分が面に埋まった所**から跳ね返ります。
- 開始すると、ボールは面の高さから、最高 **1 m** まで上がる初速(√(2·g·1 m) ≒ 4.43 m/s)で跳ねます。1 往復は約 0.9 秒です。
- 反発係数 1 でも、物理の離散化の誤差で、跳ね返るたびにエネルギーが少し増えます(1/960 秒刻みで約 0.4%、1/60 秒刻みで約 7%)。そのため、物理は 1/960 秒前後の細かい刻みに分けて進め、跳ね返った直後の上向きの速さだけを、最高 1 m になる速さへ直します(重力・衝突・反発は物理エンジンの計算です)。
- 物理は、独立した物理シーンで動かします(Play 中は `MoonTripleDribble <番号>` という scene)。他のデモの物理には影響しません。自動では進まないので、`MoonTripleDemo` が毎フレーム進めます。
- ドリブルを始めると、カメラが、ボールの後ろ 0.625 m・地面から 0.6 m の高さに下がり、水平を向きます。ボールの前後左右は、カメラの前 0.625 m に付きます(カメラを動かして進みます)。近いので、カメラの高さを中心に上下 ±0.29 m の外(視野角 50°)は画面に入りません。1 m まで跳ねる全体を見たいときは、Q E でカメラの高さを変えるか、`MoonTripleDemo.DribbleDistance`(2.5 m なら全部入る)を変えます。
- 面の高さを動かすと、地面の衝突面も動きます。ボールが面の境界(x=0 または z=0)の上にあるときは、「操作する面」の高さで跳ねます。ボールが別の面の上を通ると、その面の高さで跳ねます。

## fps を上げるには

- **Game View を最大化する**(Game タブの「Maximize On Play」、またはタブを最大化)。Scene View が同時に表示されていると、同じ物をもう一度描くので、実測で fps が約 2 倍違います(ドリブル中、26 → 50 fps)。
- VG の面の細かさ `MoonTripleDemo.VgTargetPixels`(16)。面は平らなので、粗くしても形は変わらず、選ぶ Cell が減ります(Game View 最大化で、2 → 約 500 個、6 → 270 個で 57 fps、12 → 144 個で 76 fps、24 → 83 個で 88 fps)。
- HUD の文字は 0.1 秒ごとにだけ作り直します(毎フレームだと、文字の組み直しで fps が約 3 割下がりました)。

## 開き方

1. メニュー `Tools > Virtualized Geometry > Moon And Ball > Use Moon And Ball Pipeline`(この demo の URP の Renderer と Pipeline を選ぶ)
2. `MoonTriple.unity` を開いて Play
3. 作り直すとき: メニュー `Tools > Virtualized Geometry > Moon Triple > Build Scene`(batchmode は `-executeMethod VirtualGeometry.Omni.Editor.MoonTripleSceneBuilder.Run`)

Omni(`com.omni.rendering`)が必要です。現在の参照先は `D:/Unity/6.6/OmniDist/wiring-fix2/Omni-0.1.0.tgz` です(署名付きの版には `ExternalDrawRequirements` が無く、コンパイルが通らないため使えません)。

## 検証

```
Unity.exe -batchmode -force-d3d12 -projectPath <project> -executeMethod VirtualGeometry.Omni.Tests.MoonTripleLayoutTests   (EditMode テスト)
Unity.exe -batchmode -force-d3d12 -projectPath <project> -executeMethod VirtualGeometry.Omni.Editor.MoonTripleEditModeCheck.Run
Unity.exe -batchmode -force-d3d12 -projectPath <project> -executeMethod VirtualGeometry.Omni.Editor.MoonTriplePlaySmoke.Run
```

- `MoonTripleEditModeCheck.Run`: Play していない Edit Mode で、原点のボールと camera の絵が出るかを撮ります。結果は `TestResults/Planet/MoonTripleEdit`。
- `MoonTriplePlaySmoke.Run`: Play Mode で、キー(ボールの量、Shift、1、2、[ ]、5、4、7・8、R)、ドリブルの速さの範囲、HUD の呼び名を確かめます。結果は `TestResults/Planet/MoonTriplePlay`。
- 単体テスト `MoonTripleLayoutTests`: 大きさ、VG の最大階層、象限と境界での面の決め方(`FaceAt`)、象限の中心、角が原点に接すること、手動分割の枚数と位置。
- 単体テスト `MoonTripleDribbleTests`(物理エンジンを使う。Edit Mode ではプレビュー scene): 初速と周期、最高の高さが毎回 1 m(1 cm 以内、20 秒間)、地面の下に入らない、X・Z が動かない、フレーム間隔が変わっても同じ、面が上下したときの跳ね返り。

開いている Editor の中では、Edit Mode の `Time.frameCount` が進まず、VG の Cell の生成が進みにくいので、描画の検証は batchmode(Editor を閉じたプロジェクト)で走らせます。
