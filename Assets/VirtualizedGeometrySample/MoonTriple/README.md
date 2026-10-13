# 3 つの方式の面(分割しない・VG・手動分割)を 1 点で接触させ、サッカーボールで食い込みを見るデモ(MoonTriple)

Omni の対数描画(Radial)で、同じ半径 R = 1,737.4 km の球の面を 3 つ y 軸(上向き)に積み、隣どうしを 1 点で接触させます。
3 つの面は、**描き方(方式)が違います**。後で方式を入れ替えたときの差を比べるためです。近くで見ると平らに見えるので、「月」ではなく「面」と呼びます(球と誤解されないように)。

| 面(画面の呼び名・色) | 方式 | 作り方 |
|---|---|---|
| **分割しない面**(下。**青**。旧: 普通の月・拡大した月) | 方式 1: 分割しない(単純な巨大化) | CosmicScale(Omni のデモ)の単位球のメッシュ `Meshes/CosmicSphere.asset`(直径 1、131,841 頂点、512×256)を、回転なしで 1 つの物体として直径 2R に拡大(分割しない)。メッシュの極(+y の頂点)が接触点 0 |
| **VG の面**(中。**緑**。旧: VG の月 A) | 方式 2: VG | `PlanetTerrainSource`(半径 R、高さ 0)。cube-sphere の Cell に分け、Cell の原点は Logical、頂点は Cell の原点からの相対位置 |
| **手動分割した面**(上。**紫**。旧: PlanetSurface の月 B) | 方式 3: PlanetSurface(手動分割) | Omni の分割した地面(`PlanetSurfaceLod`)。CosmicScale の月と同じ `CosmicPlanetSurface`(標高 0、パッチの材質は `CosmicPlanetPatch`)。カメラの位置から追従して cube-sphere を分割するパッチで、頂点はパッチの中心からの差で持つ。Omni のデモの PlanetSurface のランタイムを `Packages/com.virtualgeometry.omni/Runtime/OmniPlanetSurface/` に複製して使う |

**呼び名**: 画面(HUD・ボタン)では、方式の番号を出さず、**分割しない面 / VG の面 / 手動分割した面**と呼び、見出し・HUD の名前も同じ色(青・緑・紫)で書く(「手動分割」= Omni の PlanetSurface。カメラの距離で分割するパッチ)。以下の文書の「普通の月」「拡大した月」「VG の月」「VG の月 A」「手動分割した月」「PlanetSurface の月 B」「方式 1・2・3」は、同じ面を指す(旧称・方式の番号)。Hierarchy のオブジェクト名も日本語(方式と作り方つき)。
色は材質の `_BaseColor`(分割しない面と手動分割した面は `_BaseMap` を白にして色を直接指定、VG の面は `_BaseColor`)で、`MoonTripleDemo.ColorScaled / ColorVg / ColorSplit`。**赤系にしない**のは、ボールの赤い線の検出(赤さ = r − (g+b)/2)に面の色が混ざらないようにするため。

接触点 0 = 分割しない面と VG の面、接触点 1 = VG の面と手動分割した面。サッカーボール(直径 0.22 m、Omni の CosmicSphere のメッシュ。通常の `MeshRenderer`)は接触点の近くに置き、camera はボールの中心を向いて周回します。

**ライティング**: 2 つのシェーダー(Omni の面 `CosmicPlanetSurface.hlsl`、VG の面 `RadialMoonGrid.shader`)が、**同じ式**で照らす(方式を入れ替えたときの差に、光の違いが混ざらないように)。
- 主光源(方向光。Omni の面は `_CosmicKeyLight`、VG の面は `_PlanetKeyLight`。`MoonTripleDemo` が同じ値を書く)の拡散は、月面の粗い面の反射(Lommel-Seeliger と Lambert の混合)。
- **ボールの影**: 面の点から主光源の向きへの光線が、ボール(半径 0.11 m の球)に当たれば影。解析的な球の影で、縁は画素幅で滑らかになる(くっきり出る。URP の影の地図は、物理座標の float 精度で使えないため使わない)。影の形で、ボールと面の隙間・食い込み・接触が見える。
- **接触の暗がり**: ボールに近く、ボールの方を向く面ほど、環境光が遮られる(球による環境遮蔽の解析式)。
- ボールの点光源(減衰 `1/(1+(d/0.4 m)²)`、強さ `MoonTripleDemo.BallLightIntensity`)は、影の中が真っ黒にならない弱い補助光。Omni の面は `_CosmicBallLight`(xyz = ボールの中心の、カメラからの相対位置。w = 強さ)、VG の面は `_PlanetBallLight`(xyz = ボールの中心 − 格子の基準点、w = 強さ)。面の点の位置は、Omni の面は `OmniRadialObjectToLogicalCameraRelative`(頂点で求めて補間)。
- VG の面は、ボールがあるとき(基準点がある所)、URP の光を使わず、上の式で照らす。**格子の線はエミッシブ**(光の計算の後で色を直接乗せ、影の中でも見える)。ボールが無いとき(他のデモ、原子の周り)は、従来どおり URP の光。

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

## Edit Mode(Play していない状態)の表示

Play を止めても、Game View に Play 開始と同じ絵(接触点 0、ボールと camera の既定の視点)が出る。

- シーン生成(`Tools > Virtualized Geometry > Moon Triple > Build Scene`)が、配置(月・ボール・原子の Logical の位置・倍率、camera の姿勢)を scene に焼き込む。
- `MoonTripleDemo` は `[ExecuteAlways]` で、Edit Mode では `EditorApplication.update` から、初期化(配置、`_CosmicKeyLight`、基準点)と、数 tick に 1 回の再描画の要求(VG の Cell の生成を進める)を行う。シーンの dirty は戻す(開いただけで「保存しますか」と聞かれない)。
- キー・ボタンの操作、テレポート、論理ニアクリップの切り替えは、Play のときだけ(Edit Mode では HUD の表示だけ)。
- VG の月・原子を Omni の配置に乗せるため、`OmniPlanetPlacement` にも `[ExecuteAlways]` を付けた。
- **Scene View**: Omni の論理ピボット(`ExploreFromSceneView`)が目になり、Game camera には追従しない。Scene View のオーバーレイ「Omni」で、「位置を同期」にチェックし、「向きを合わせる」を押すと、Game camera と同じ位置・向きになる。
- 確認: `MoonTripleEditModeCheck`(バッチ。シーンを開いて Play せず、Game camera を撮る。明るい画素の割合 0.289、ボールの赤い線の画素 4,643)。画像は `TestResults/Planet/MoonTripleEdit/EditMode.png`。実際に開いた Editor の Game View と Scene View は、未確認(バッチでは `QueuePlayerLoopUpdate` で Cell の生成を進めている。開いた Editor で進まない場合は、`VirtualGeometryClusterSource` の frame 判定の変更が要る)。

## 論理ニアクリップの切り替えと、原子サイズの VG の球

**目的**: 論理ニアクリップを近づけて、原子サイズ(半径 1e-10 m)の物と、巨大な VG(月 A、半径 1.7e6 m)を、**同じ処理**(同じ `PlanetTerrainSource` の Cell 選択と描画)で、精密に描く。

- 論理ニアクリップ(Omni の `RadialCamera.nearClip`)は、プリセット 5 つ(0.03 m / 1 mm / 1 µm / 1 nm / 10 pm)を、ボタンとキー 7・8 で切り替える。論理 near を `nearClip` に**直接代入**する(M = 0.3 ÷ 論理 near は Omni が求める。M は最大 3e10)。`SetViewScale(M)` は使わない: `SetViewScale` は `Camera.nearClipPlane` を読むが、Omni は描画中にこの値を写像後の値(log2(1+0.3) ≒ 0.378)へ書き換えて描画後に戻すので、描画の途中の値を読むと M がずれる。camera の設定の Near は 0.3 m のまま固定。HUD に、プリセット、Omni から読み戻した論理 near と log2 M、camera の設定の Near(`Camera.nearClipPlane` が 0.3 でないときは警告)、原子までの距離が出る。
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

**1 ボタン = 1 機能**。見出しで区切り、キーの番号は `[n]` で、ボタンの先頭に付く。今の接触点・プリセットのボタンは緑になる。画面(HUD・ボタン)では、方式の番号を出さず、呼び名は **分割しない面 / VG の面 / 手動分割した面** の 3 つ(青・緑・紫の文字)。

| 見出し | ボタン | 動き |
|---|---|---|
| 接触点へテレポート(2 つの面が接する所へ) | `[1] 分割しない面 と VG の面 の接触点へ(ボールが 2 つの面に 1 点ずつ接する位置)` | 接触点 0 の、ボールが両側の面に接する距離へ(接触点から +z) |
| | `[2] VG の面 と 手動分割した面 の接触点へ(…)` | 接触点 1 の同じ距離へ |
| ボール(いまの接触点: 下の面 = …、上の面 = …) | `[3] ボールを、2 つの面に 1 点ずつ接する位置へ` | 今の接触点の、ボールが両側の面に接する距離へ(キー 3 と同じ) |
| | `[4] ボールの中心を接触点に置く(両方の面に食い込む)` | 接触点そのもの(ボールは両側の面に食い込む) |
| | `[5] ボールの半分を下の面(「…」)に埋める` | 下の面の理想の高さにボールの中心を置く。camera は 15° 見下ろし |
| | `[6] ボールの半分を上の面(「…」)に埋める` | 上の面の下面の高さに置く。camera は 15° 見上げ、光は下から |
| 論理ニアクリップ(キー 7・8) | `[通常][1 mm][1 µm][1 nm][10 pm]` | 論理 near のプリセットを切り替える |
| | `[9] 原子(VG の球、半径 1e-10 m)へテレポート` | 原子の周りへ(論理 near は 10 pm) |
| この接触点の隙間(キー `[` `]`) | `[-10mm][-1mm][+1mm][+10mm]` | 今の接触点の隙間(面どうしの距離)を変える |

- `[5]` `[6]` は、接触点の近くでは、上下の面の高さがほぼ同じで、埋められない(接触点ちょうどでは y が動かない)ので、**接触点から 1,500 m の位置へ移してから**埋める(隙間が約 1.3 m で、ボールと camera が入る)。移動先の VG の面の Cell が生成されるまで待ち、HUD に説明が 1 行出る。手動分割した面は、細分化が進むまで時間がかかる(HUD の「分割中...」)。
- テレポートは、移動先の VG の面の Cell が生成されるまで(先読み)今の位置のまま待ち、HUD に「テレポート中」と出る。
- HUD の「手動分割した面」の行に `[更新が止まっています]` が出るときは、分割処理が動いていない(Enter Play Mode Options で「シーンを再読み込みしない」設定の Play で、Edit Mode で作った隠れた分割処理は、Play で更新が回らない。`CosmicPlanetSurface.Begin` が、Play では、それを破棄して作り直すように直した)。
- HUD の `[警告: Camera.nearClipPlane が設定と違う値で読めました]` は、`Update`(描画の外)で見て、設定の 0.3 と違うときだけ出る(`OnGUI` で読むと、Omni が描画中に書き換えた値が読めて、誤警告になるため)。Console にも、初回だけ警告が出る。

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
| 5 / 6 | ボールの半分を、今の接触点の下の月 / 上の月に埋める(ボールの中心を理想の面の高さに置く。接触点の近くでは、接触点から 1,500 m へ移してから。赤い線 = ボールの中心の高さ。線と面の境界のずれが、面の高さの誤差) |
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
