# 3 つの方式の月(単純な巨大化・VG・PlanetSurface)を 1 点で接触させ、サッカーボールで食い込みを見るデモ(MoonTriple)

Omni の対数描画(Radial)で、同じ半径 R = 1,737.4 km の 3 つの月を y 軸(上向き)に積み、隣どうしを 1 点で接触させます。
3 つの月は、**描き方(方式)が違います**。後で方式を入れ替えたときの差を比べるためです。

| 月 | 方式 | 作り方 |
|---|---|---|
| 普通の月(下) | 1. 単純な巨大化 | CosmicScale(Omni のデモ)の単位球のメッシュ `Meshes/CosmicSphere.asset`(直径 1、131,841 頂点、512×256)を、回転なしで 1 つの物体として直径 2R に拡大(分割しない)。メッシュの極(+y の頂点)が接触点 0 |
| VG の月 A(中) | 2. VG | `PlanetTerrainSource`(半径 R、高さ 0)。cube-sphere の Cell に分け、Cell の原点は Logical、頂点は Cell の原点からの相対位置 |
| PlanetSurface の月 B(上) | 3. PlanetSurface | Omni の分割した地面(`PlanetSurfaceLod`)。CosmicScale の月と同じ `CosmicPlanetSurface`(標高 0、パッチの材質は `CosmicPlanetPatch`)。カメラの位置から追従して cube-sphere を分割するパッチで、頂点はパッチの中心からの差で持つ。Omni のデモの PlanetSurface のランタイムを `Packages/com.virtualgeometry.omni/Runtime/OmniPlanetSurface/` に複製して使う |

接触点 0 = 普通の月(方式 1)と VG の月 A(方式 2)、接触点 1 = VG の月 A(方式 2)と PlanetSurface の月 B(方式 3)。サッカーボール(直径 0.22 m、Omni の CosmicSphere のメッシュ。通常の `MeshRenderer`)は接触点の近くに置き、camera はボールの中心を向いて周回します。

## 現在の状態

| 確認 | 結果 |
|---|---|
| 方式 3(PlanetSurface の月 B)の描画(Play Mode) | 細分化は、接触点 1 の 1,500 m の位置で、約 1,100 フレームで階層 20(要求どおり)に届く(`FastRefinement` 有効)。届いた状態は、縞模様も穴もなく、ボールの半分が月 B の面に埋まり、赤い線が面の切れ目に沿う。細分化の途中は、粗いパッチの縞模様と穴(ピンク)が出る |
| 掃引の撮影(Play スモーク。ボールの半分を埋め、y を −5〜+5 mm) | 方式 2(VG)と方式 3(PlanetSurface)は、赤い線の見える太さが、0 mm で全体の半分、±2 mm で 0 mm の値を中心に対称(誤差は 1 画素 ≒ 0.7 mm 以内)。方式 1(単純な巨大化)は面が描かれず、ずれを測れない。結果は `TestResults/Planet/MoonTripleBury/`(`Sweep_Sheet.png` が 1 枚のまとめ) |
| 細かい掃引(Play スモーク。画角 5°、1280×960、1 画素 ≒ 0.023 mm。ボールの +x 側の赤道の点を狙い、ボールを 0.5 mm ずつ動かして、赤い線の見える太さを直線に合わせる) | 方式 2(VG)は −0.055 mm(見下ろし 15°)、−0.040 mm(8°)、方式 3(PlanetSurface)は +0.055 mm(15°)、+0.040 mm(8°)(面の高さの理想との差。正 = 理想より高い)。**2 つの方式とも、理想の高さから 0.06 mm 以内。2 つの差はゼロ**。残る ±0.04〜0.055 mm は、2 つの方式で同じ値・同じ向き(ボールから遠い向き)で、赤い線の重みの閾値を変えても 0.003 mm 以下しか動かない(閾値が原因ではない)。見下ろす角度では 8° で 0.040、15° で 0.055 mm と少し変わる。面の誤差というより、ボール側の共通の効果の可能性が高い(未確認。第 3 の基準が無いので、切り分けられない)。誤差の上限は 0.06 mm。測る場所の注意: ボールの +z 側(接触点から離れる側)で測ると、球の面の傾き(1,500 m で L/R × 0.11 m ≒ 0.095 mm)が誤差に混ざる(実測で確認)。そのため +x 側で測る。角度は環境変数 `MOONTRIPLE_FINE_ANGLE`(既定 8)で変えられる |
| 方式 3 の描画(検証のバッチ = Edit Mode) | 細分化が階層 12 から進まない(葉 186 個)。Edit Mode の制約で、Play Mode とは違う。検証の画像は、細分化の途中の状態 |
| 方式 2(VG の月 A)の描画 | 解析と一致(不一致 0 画素。方式 3 の月 B にする前の構成での結果。この構成では全体の検証を未再実行) |
| ボールの描画 | 解析と一致(不一致 0 画素。同上) |
| 方式 1(普通の月)の描画 | **解析と一致しない**(約 26% しか描かれない。下の「既知の問題」) |
| 普通の月と VG の接触誤差 | **数値の計算で求めた**(描画は使わない)。下の「数値の結果」 |
| Play モードのスモーク `MoonTriplePlaySmoke` | PASSED(キー、テレポート、隙間、エラー無し。方式 3 の月 B でも) |
| 検証 `MoonTripleValidation`(全体) | 方式 3 の構成では**未再実行**。以前の構成(VG の月 B)では FAILED(普通の月の描画が解析と一致しない。遠景の撮影は時間切れ) |

## 論理ニアクリップの切り替えと、原子サイズの VG の球

**目的**: 論理ニアクリップを近づけて、原子サイズ(半径 1e-10 m)の物と、巨大な VG(月 A、半径 1.7e6 m)を、**同じ処理**(同じ `PlanetTerrainSource` の Cell 選択と描画)で、精密に描く。

- 論理ニアクリップ(Omni の `RadialCamera.nearClip`)は、プリセット 5 つ(0.03 m / 1 mm / 1 µm / 1 nm / 10 pm)を、ボタンとキー 7・8 で切り替える。camera の Near は 0.3 m のまま固定で、View Scale M = 0.3 ÷ 論理 near を `SetViewScale` に渡す(M は最大 3e10)。HUD に、プリセット、Omni から読み戻した論理 near と log2 M、camera の Near、原子までの距離が出る。
- VG の Cell の細分化は、論理 near を下限にして画面誤差を計算する(`OmniPlanetPlacement`)ので、原子の頂点間隔(~1e-12 m)まで細かくするには、論理 near を 1e-11 m 前後以下にする。
- 原子は、接触点 0 のすぐ近く(x = z = 0.5 m、y = 隙間の中央)に置く。座標が 1 m 前後なので、camera の倍精度の位置の刻み(1e-16 m)が原子の大きさ(1e-10 m)より十分小さい。キー 9 かボタンで、原子の中心から半径の 4 倍の距離へテレポートする(先読みを待つ)。
- 原子の近くでは、飛ぶ速さ・ホイールの量は、論理 near に比例して小さくなる(10 pm で通常の 3.3e-10 倍)。C は、原子の周りでは原子に向け直す。
- 原子の VG の材質は、基準点を設定しない(格子は周期が 1 m / 10 cm / 1 cm 固定で、原子では全面が格子の色になるため)。URP の光による陰影だけで見える。

**確認**(`MoonTriplePlaySmoke`、Play Mode)
- プリセットごとに、論理 near と log2 M の読み戻しが期待値と一致する(10 pm で log2 M ≒ 34.8)。
- 同じ見かけの大きさ(中心から半径の 4 倍の距離、見かけの半径 14.5°)で撮った、単色の円を、倍精度の光線で求めた円と比べた: 原子(R = 1e-10 m、論理 near 10 pm)も、VG の月 A(R = 1.7374e6 m、論理 near 0.03 m)も、1 画素の帯の外で違う画素は **0**(円の画素 55,472)。画像は `TestResults/Planet/MoonTripleScale/`。ただし、普通の月(方式 1)は、近距離で描画が壊れ、原子の前に黒い点が出るため、この比べでは非表示にしている。
- 原子へのテレポートで、先読みが 1 フレームで終わる(以前の先読みは、原子サイズの面で永久に false になっていた: `PlanetLayerSource.Prefetch` が `Vector3.normalized`(大きさ 1e-5 以下でゼロを返す)を使っていた。`float3` の `normalizesafe` に直した)。
- 原子の近くで、飛ぶ量がスケールに連動する。原子から戻ると、論理 near が通常に戻る。

**限界**: 原子サイズの球の法線(`PlanetTerrainGenerate.compute`)と照明用の `objectToWorld`(float)は、画像で破綻が見えなかった(円の一致)が、数値としての法線の精度は確かめていない。格子つきの原子は未対応(`RadialMoonGrid` に周期の倍率が無い)。

## 検証の画像の色

| 色 | 意味 |
|---|---|
| ピンク(マゼンタ) | 何も描かれていない場所(カメラの背景色)。普通の月の欠けや、月 B の細分化の途中の穴 |
| 緑 | VG の月 A(検証では、判定のため単色にしている。Play では実際の材質の格子) |
| 黒・暗い面 | PlanetSurface の月 B。下面が太陽光の反対側を向くと暗い(検証の probe では、上の月に埋める撮影で光を下から当てる) |
| 白・クリーム色 | 月の面が明るく光っている部分(普通の月の上面、光を下から当てた月 B の下面) |
| 灰色・茶色の縞 | 月 B のうち、まだ粗いパッチの部分(月の画像が引き伸ばされる) |
| 赤い線 | ボールの中心の高さ(赤道の線。`CosmicSoccerBallSurface.hlsl` に MoonTriple が足した。材質の `_CosmicP2`) |

## 数値の結果(`MoonTripleContactError`)

普通の月の表面を、三角形の平面で補間して求め、VG の月 A の下面との隙間を、横の距離 L の関数として計算します。ボール(直径 0.22 m)が両側に接する横の距離を求めます。

| 普通の月のメッシュ | 最大の弦の落ち込み | 618 m での表面の差(理想との差) | ボールが両側に接する距離 |
|---|---|---|---|
| 512×256(資産) | 29.1 m | −3.68 m | 35.8 m |
| 1024×512 | 7.3 m | −1.79 m | 71.2 m |
| 2048×1024 | 1.9 m | −0.84 m | 139.7 m |
| 理想の球(参考) | 0 | 0 | 618.2 m |

- 理想の球では、ボールが両側に接する距離は 618.246 m です(`MoonTripleLayout.TouchDistance`)。
- 三角形のメッシュでは、極(接触点)の周りの面が傾いています。極から離れると、表面は約 θ/2・L だけ下がります(θ は極から 1 段目の緯度の角度)。理想の球の下がりは L²/(2R) なので、L が大きいほど差が大きくなります。
- 分割を細かくすると差は減りますが、比例して減るだけで、618 m の位置では 2048×1024 でもまだ 0.84 m の差があります。
- 数値の計算は 1 次元の近似です(ボールの中心の横の位置で隙間の幅を測ります)。

## 既知の問題: 普通の月が描画で欠ける

`MoonTripleValidation` の結果では、普通の月を含む画像で、解析上の普通の月の画素(約 153,000)のうち、約 40,000 画素しか描画されません。残りの約 113,000 画素は背景のまま出ます。

- 三角形を細かくすると、描画される面積はむしろ減ります(512×256: 39,694、1024×512: 20,166、2048×1024: 10,240)。三角形の大きさが原因ではありません。
- 三角形の形(三角形の判定)と理想の球の判定は一致しています。形の誤差が原因ではありません。
- Omni 自身の CosmicScale は、地表付近では単一の球を描きません。月を分割した地面(PlanetSurfaceLod のパッチ)で描き、球の MeshRenderer は隠します(`CosmicPlanetSurface` の LOD 初期化)。つまり、単一の球を接触点の距離で描くことは、Omni の使い方の外です。
- 原因は未確認です。Omni の Radial 描画の、メッシュの頂点・索引の扱いを読めば確かめられる可能性があります。

## 開き方

1. メニュー `Tools > Virtualized Geometry > Moon And Ball > Use Moon And Ball Pipeline`(この demo の URP の Renderer と Pipeline を選ぶ)
2. `MoonTriple.unity` を開いて Play
3. 作り直すとき: メニュー `Tools > Virtualized Geometry > Moon Triple > Build Scene`(batchmode は `-executeMethod VirtualGeometry.Omni.Editor.MoonTripleSceneBuilder.Run`)

Omni(`com.omni.rendering`)が必要です。現在の参照先は `D:/Unity/6.6/OmniDist/wiring-fix2/Omni-0.1.0.tgz` です(署名付きの版には `ExternalDrawRequirements` が無く、コンパイルが通らないため使えません)。

## UI(Game View の左上のボタン)

- **テレポート: 普通の月(1) | VG の月 A(2)**: 接触点 0 の、ボールが両側の面に接する距離へ(接触点から +z)
- **テレポート: VG の月 A(2) | PlanetSurface の月 B(3)**: 接触点 1 の同じ距離へ
- **論理ニアクリップ [通常][1 mm][1 µm][1 nm][10 pm]**: 論理 near のプリセットを切り替える
- **テレポート: 原子(VG 半径 1e-10 m)**: 原子の周りへ(論理 near は 10 pm)
- **Move ball to the exact contact point**: 今の接触点そのもの(ボールは両側の面に食い込む)
- **Gap of this contact [-10mm][-1mm][+1mm][+10mm]**: 今の接触点の隙間(月どうしの距離)を変える

テレポートは、移動先の VG の月の Cell が生成されるまで(先読み)今の位置のまま待ち、HUD に「TELEPORTING」と出ます。

## 操作(キー)

| キー | 動き |
|---|---|
| ↑ ↓ | ボールを y 方向へ 1mm(Shift で 10mm、Ctrl で 0.1mm) |
| ← → | ボールを x 方向に 1mm |
| PageUp PageDown | ボールを z 方向に 1mm |
| A D / W S | camera の周回(yaw / pitch)。右ドラッグも |
| Home End / ホイール | camera の距離 |
| 1 / 2 | 接触点 0 / 1 へテレポート(ボールが両側の面に接する距離) |
| 3 / 4 | 今の接触点で、接する距離 / 接触点そのもの |
| 5 / 6 | ボールの中心を、今の接触点の下の月 / 上の月の理想の面の高さに置く(ボールの半分が月に埋まる。赤い線 = ボールの中心の高さ。線と面の境界のずれが、面の高さの誤差) |
| 7 / 8 | 論理ニアクリップのプリセットを、一段細かく / 粗く(通常 0.03 m、1 mm、1 µm、1 nm、10 pm) |
| 9 | 原子サイズの VG の球(半径 1e-10 m)へテレポート(論理 near は 10 pm になる)。戻るのは 1・2・R など、ボールの側へのテレポート(論理 near は通常に戻る) |
| [ ] | 今の接触点の隙間を 1mm 小さく / 大きく(Shift 10mm、Ctrl 0.1mm) |
| R | 初期化 |

## 検証

```
Unity.exe -batchmode -force-d3d12 -projectPath <project> -executeMethod VirtualGeometry.Omni.Editor.MoonTripleContactError.Run [-plainLongitude N -plainLatitude M]
Unity.exe -batchmode -force-d3d12 -projectPath <project> -executeMethod VirtualGeometry.Omni.Editor.MoonTripleValidation.Run
Unity.exe -batchmode -force-d3d12 -projectPath <project> -executeMethod VirtualGeometry.Omni.Editor.MoonTripleValidation.RunProbe [-plainLongitude N -plainLatitude M]
Unity.exe -batchmode -force-d3d12 -projectPath <project> -executeMethod VirtualGeometry.Omni.Editor.MoonTriplePlaySmoke.Run
```

- `MoonTripleContactError.Run`: 普通の月の接触誤差を数値で求めます(描画なし)。結果は `TestResults/Planet/MoonTripleContactError/Report.txt`。
- `MoonTripleValidation.Run`: 描画と解析を比べます(FAILED。上の「既知の問題」)。結果は `TestResults/Planet/MoonTriple`。時間がかかり、遠景の撮影で時間切れになります(約 1 時間)。
- `MoonTripleValidation.RunProbe`: 1 視点だけを撮って、描画と解析の画素の数を見ます(判定なし)。`-probeContact 0`(既定)は普通の月と VG の月 A(普通の月だけ表示)、`-probeContact 1` は VG の月 A と PlanetSurface の月 B(ボールを表示)。`-plainLongitude`、`-plainLatitude` で普通の月の分割を変えられます。結果は `TestResults/Planet/MoonTripleProbe`。
- `MoonTriplePlaySmoke.Run`: Play Mode で、キーごとの量と向き、テレポート、隙間、エラーが出ないことを確かめます。続けて、PlanetSurface の月 B の細分化(階層 20 に届くか)を確かめ、3 つの方式にボールの半分を埋めた画像(`TestResults/Planet/MoonTripleBury/<番号>_*.png`)と、y を動かした掃引(`Sweep_*.png`、まとめは `Sweep_Sheet.png`)、方式 2・3 の mm 未満の掃引(結果は `Report.txt` の `fine sweep` の行)を撮ります。約 4 分です。細かい掃引の見下ろす角度は、環境変数 `MOONTRIPLE_FINE_ANGLE`(度。既定 8。15 以上は赤道の線が視野から外れて使えない)で変えます。

開いている Editor の中では、Edit Mode の `Time.frameCount` が進まず、Cell の生成が 1 回しか進まないので、描画の検証は batchmode(Editor を閉じたプロジェクト)で走らせます。`Run-PlanetRegression.ps1` で実行するときも、同じです。
