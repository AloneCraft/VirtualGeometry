# 惑星規模レイヤー表現フレームワーク

Virtualized Geometry(以下 VG)のクラスター描画を土台にして、
宇宙から見た天体全体から地上の人の目線まで、
任意の場所・任意のスケールへ降りられる世界を描く。

座標は double で持たない。
Omni(`com.omni.rendering`)の Logical 固定小数点と Radial 対数写像を使う。

この文書は現在有効な仕様だけを書く。
否定された仮説や完了した作業ログは書かない。


## 1. 表現の原則

表現は置き換えずにレイヤーを重ねる。

陸、海、大気、天候は別の Layer として全距離で同時に存在させ、
陸から海へ切り替わる瞬間を作らない。

海岸線は、陸の面と海抜0の海面の深度による交線で作る。

Layer の中の表現の遷移は、同じ元データの帯域分割で行う。

帯の境界での見た目の差は 1px 未満にする。

2つの別物を混ぜるクロスフェードは原則として使わない。

誤差を理論上抑えられない所だけ、
降水量や高度に応じた強度の変化として見せる。


## 2. 構造

世界は次の4つの概念で表す。

- Domain:1つの Logical フレームと1つの分割方式を持つ参照フレーム。入れ子にできる(銀河 → 恒星系 → 惑星 → 地域 → 都市 → 建物内部)
- Cell tree:Domain の空間分割。惑星は cube-sphere の6面の四分木、宇宙は八分木、平地は四分木
- Field:どの段の解像度でも読めるデータ(高さ、地表分類、水深、天気)
- Layer:Cell ごとに表現を担当する単位

Layer は Cell ごとに次を宣言する。

- 有効帯:どの画面誤差の範囲で、どの表現を使うか
- 先読み・有効化・無効化のしきい値(ヒステリシス付き)
- 被覆契約:上乗せ型(他の Layer と共存)か置換型(下位の Layer の該当 Cell を抑制する)か
- 依存する Field

作り込み(特定の場所)は Override として、任意の Cell・任意の段に接続する。

置換型の Override が抑制する範囲は Cell の境界で決める。

Override の縁は、Field の高さに合わせて作る。

Field は供給元の積み重ねで表す。

- 基盤の供給元:実ラスタまたは手続き生成器
- 地域 Override:任意の範囲・任意の解像度の実データまたは手作業データ
- 増幅:データの解像度より細かい帯域だけを seed から決定論的に生成

各供給元は有効な解像度帯を宣言し、帯が重ならないように積む。

増幅はデータの Nyquist 周波数より上の帯域だけに足し、
データの1セルごとに平均0にする(実装は近似: §18 の増幅では、1 画素ごとの平均の二乗平均の平方根が全体の 0.34 倍で、厳密な 0 は保証しない)。

遠景(データそのもの)と近景が一致することを、これで保証する。


## 3. スケール範囲

半径 1m から 10²⁰ m までの天体を同じシーンに置けて、
カメラの近くでは 1mm の精度で操作・表示できるようにする。

最初の縦切りは半径 600km で進めるが、
型と計算式は最初から全範囲で破綻しない形にする。

根拠:Omni の CosmicScale で、サッカーボールと
単位球を拡大した月を並べたとき、
物体空間の float の分解能(約0.2m)で模様と SV_Depth が量子化され、
月の面がボールの縁を越えて手前に出た(Omni `Docs/CurrentWork.md` COSMIC-PRECISION-001)。
分割した地面で描くことで解決した。


## 4. 不変条件

位置の正本は Logical とする。

float にするのはカメラ相対へ正規化した後だけにする。

float に落としてから差を取らない。

Cell 内とクラスター内の float 量子化誤差は、
その Cell・クラスターの幾何誤差以下にする。

頂点の誤差(2次展開の打ち切り誤差、または局所 float の誤差)も
この予算内に収める。

ビルド済みアセットにも同じ規則を適用する。

GPU で、長さの2乗やスケール前の距離の積を float のまま計算しない。

|v| が 1.8×10¹⁹ m を超えると |v|² が inf になるため、
長さ、画面誤差、カリング、bounds は、
指数を分けた形か最大成分で正規化した形で計算する。

CPU の Logical でも R² は Scalar の範囲(±3.96×10²⁸)を超えうるので、
単位球の空間で正規化してから R を掛ける。

地平線判定の R/|viewer| のような 1 に近い比較を float でしない。

カメラの局所的な接平面か Logical で判定する。

親は子の leaf 領域を正確に覆い、親の誤差は子の誤差以上とする。

描画できるのは生成済み(ready)の Cell だけとする。

子4つがすべて ready になるまでは親を描き続ける。

描画中の Cell の祖先は evict しない。

恒等でも Radial でも、深度を書く全 Pass は同じ関数を通り、
ビット単位で同じ深度を書く。

外部の描画経路は独自の camera packet writer を作らず、
Omni の公開 API へ要件を登録する。

`CameraRelativeInstanceData` は要求しない。

Radial では Unity camera の far、平面、`camera.transform.position` を使わない。

Omni の ViewSnapshot の値を使い、使えないときは描く側へ倒す(fail-open)。

既存の VG 単体の挙動は、新しい keyword が off のとき変わらない。

対応しない camera や機能は誤った表示をせず、
描かないか Proxy へ切り替え、理由を診断に出す。


## 5. パッケージ構成

| 単位 | 責務 | 依存 |
|---|---|---|
| `com.virtualgeometry.core` | クラスター供給元の抽象、選択・カリング・描画、cluster ごとの変換、座標入力の抽象(恒等の実装)、外部互換型のレジストリ | URP |
| `com.virtualgeometry.omni`(asmdef `VirtualGeometry.Omni`) | 惑星を Omni の Logical 座標に置く `OmniPlanetPlacement`(ViewSnapshot によるカメラ入力、eye 相対の cull 空間、Cell ごとの Omni の物体の値)、Radial shader(`Shaders/RadialGlobalLit.vgshader`) | VG core、世界パッケージ、`com.omni.rendering` |
| 世界パッケージ(`com.virtualgeometry.world`) | Domain、Cell、Field、Layer、Override、惑星、海、降下カメラ。Cell の原点などの高精度の値は Omni の Logical(`Omni.Logical.Scalar` など)で求める | VG core、`com.omni.rendering`(Logical の型と演算だけ。描画の接続は adapter) |

VG core は Omni に依存しない。

世界パッケージは Omni の Logical の型と演算を直接使う(2026-10-11 に決定。10^20 m を 1mm で扱うには double でも足りず、
Omni の Q95.96 固定小数点を位置の正本にするため)。Omni の Radial 描画への接続は adapter(`com.virtualgeometry.omni`)が受け持つ。

Omni は tgz をリポジトリ外のパスから `file:` で参照する。

取得手順は `Documentation~/Development.md` に書く。


## 6. 最初の縦切り

手続き生成の惑星(半径はパラメータ、初期値 600km 前後)に、
地形と海の2つの Layer を載せる。

宇宙から地上の目線(1.7m)まで、ポップと継ぎ目なしで降りられることを確かめる。

地上は見るだけとし、当たり判定は作らない。

カメラは Field の高さより上に制限する。

最初の縦切りの対象外:

- 地形の影
- 当たり判定、Picking
- MotionVectors、TAA、Motion Blur(無効を前提とする)
- URP Forward(Forward+ に限定し、それ以外は Fail Closed)
- 追加ライト
- 大気、雲、天気、Scatter、Override の実装(型と契約だけを用意する)
- Reflection camera、Preview camera、MSAA、Camera Stack、XR


## 7. 段階と受入条件

| 段階 | 内容 | 受入条件 |
|---|---|---|
| S | Omni を変更しない spike | Omni 生成 Radial Lit の `.vgshader` 変換結果、Omni の physical world の定義、RendererFeature の両立性を記録する |
| P0a | Omni の外部描画経路の公開 API(`ExternalDrawPaths.Register`) | 要件の登録と解除、BRG 専用の要件を漏らさないこと、外部経路の宣言で Scene View の「Radial 対象なし」判定が解除されること |
| P1 | VG core の座標抽象 | M0〜M5 と SpaceElevator の結果が変更前と同一 |
| P3 | クラスター供給元の抽象と実行時生成グリッド(Omni なし) | 継ぎ目の画素 0、LOD 遷移の差 1px 未満、pool 上限内、生成予算超過でも穴なし |
| P2 | Omni adapter(Cell ごとの draw) | 半径 600km の各高度で継ぎ目 0、z-fighting 0。draw 行列の平行移動に画像が依存しない |
| P0b・P2b | Omni の external instance 経路と一括描画 | Cell ごとの draw と一括描画で color と depth がビット単位で一致 |
| P4 | 世界パッケージと惑星 | CPU と GPU の Field 評価が許容値内で一致。瞬間移動しても地面に潜る frame が 0 |
| P5 | 海の Layer | 海岸線の z-fighting 0、Nyquist を超える波長が displacement に入らない、高度帯の遷移の差 1px 未満 |
| P6 | スケール試験 | 半径 6,371km で P3〜P5 が成立。0.11m、1m、月、10²⁰ m の天体の同時配置で量子化・継ぎ目・z-fighting・inf/NaN が 0、1mm の移動が画面上で連続 |

コンパイル成功、batchmode でのロジックテスト成功、実画面での確認成功を区別して記録する。

実行できなかった確認は「未実施」と記録し、成功扱いしない。


## 8. Omni 接続

Omni 0.1.0(`com.omni.rendering`)の確認済みの事実と、そこから決めた接続方法を書く。

### 8.1 物体の変換と物理位置

Omni の precise な物体(DOTS 以外)は、
CPU が Logical 精度で求めた「物体 − warp 中心」を、
物体ごとの値 `_RadialGlobal_RadialTransform*` として受け取る。

GPU では巨大な値を足し引きしない(`RadialInject.hlsl` の `OmniRadialCenterRelative`)。

eye と view の回転も、precise な物体では物体ごとの値
`_RadialGlobal_EyeRelative`、`_RadialGlobal_ViewRow*` から読む。

camera packet `_OmniRadialCamera_*` から読むのは、
float で安全な通常の Renderer(shared Unity Transform)と DOTS だけである。

照明用の物理位置は、通常は Unity Proxy の行列(`TransformObjectToWorld`)から作る。

Logical camera が安全域外にあるときは、Unity native camera を原点に固定し、
物理位置を Logical のカメラ相対差から作る(`OmniRadialObjectToLogicalCameraRelative`)。

Unity Proxy の値は `Omni.Logical.Transform.SyncUnityTransform` だけが書く。

安全域外では精度を落とした値を書き、
Unity native の限界を超えるときは代理値を書く。

決定:VG は Omni が生成した Radial shader を `.vgshader` の元にし、
Omni が通常の precise な Renderer に書くのと同じ物体ごとの値を、
draw ごとの MaterialPropertyBlock で渡す。

この値は VG が自前で計算せず、Omni の既存の公開 API から受け取る。

- `RadialViewRegistry.TryGetCurrent`:描画中 camera の `ViewSnapshot`(Logical の `WarpCenter`、`GpuFrame`、Logical の錐台、near、far)
- `RenderPose.TryCreate`、`DrawTransform.TryCreate`:物体の Logical 姿勢と `WarpCenter` から物体ごとの変換を作る
- `RadialGlobalRenderSystem.WriteRadialTransformParameters`、`WriteGpuFrameParameters`:上の値を MaterialPropertyBlock へ書く

`ViewSnapshot` は Preview と Reflection 以外の camera で、Omni の Renderer の有無にかかわらず公開される。

Omni の Renderer が無いと、Scene View は「Radial 対象なし」と判定されて Radial が無効になる。

これを避けるため、VG は `ExternalDrawPaths.Register` で `SceneViewContent` を宣言する(P0a で追加した公開 API)。

照明と深度の契約(Physical Lighting Data、SV_Depth)は Omni の shader に任せる。

### 8.2 `.vgshader` による Omni shader の変換

Omni Core に同梱の `Omni/RadialGlobalLit`(`Runtime/Sharders/RadialGlobalLit.shader`)は、
`.vgshader` でエラーなく変換できる。

VG 用 pass は D3D 向けに vertex・fragment ともコンパイルできる
(Spike `Assets/Development/PlanetSpike/Editor/S1VgShaderSpike.cs`、結果 `TestResults/Planet/S1VgShader.txt`)。

VG 用 pass は Omni の vertex 関数へ VG の頂点を渡し、
fragment(`SV_Depth` の出力を含む)は Omni のものをそのまま使う。

この `.vgshader` は `Packages/com.virtualgeometry.omni/Shaders/RadialGlobalLit.vgshader` に置き、P2 の検証で実際に描画した(§11)。

URP 標準 Lit を Omni が変換した shader は、
Omni の変換が Renderer 上の material だけを集めるため、まだ VG の対象にできない。

それには明示指定で変換する Omni の公開 API が必要になるが、最初の縦切りでは追加しない。

最初の縦切りの地形と海の shader は、
Omni 同梱の `RadialGlobalLit` と同じ書き方(`RadialInject.hlsl` の物体ごとの経路)で VG 側に用意する。

### 8.3 RendererFeature と Renderer 設定

Omni Core は URP の RendererFeature を追加しない。

camera ごとの処理は `RenderPipelineManager.beginCameraRendering` で行う。

Omni Core は Depth Priming を要求しない。

このため、VG の互換ゲート(`VirtualGeometryRenderFeature.cs` の `PreCull`)とは、
SSAO や Screen Space Shadows などの URP feature を使わない限り両立する。

最初の縦切りの Renderer には、これらの feature を入れない。

### 8.4 導入時の影響

Omni は `com.unity.inputsystem` 1.20.0 に依存する。

VG のプロジェクトへ入れると、Input System は 1.19.0 から 1.20.0 へ上がる。

VFX Graph、Cinemachine、mathematics も依存として入る。

Omni を入れたプロジェクトで Editor を開くと、次のものが生成・更新される(2026-10-10 に確認)。

- `Assets/Radial/`:Omni が生成する BRG 用の Material など
- `ProjectSettings/OmniEditorSettings.asset`:Omni の Editor の状態
- `ProjectSettings/VFXManager.asset`:依存に入る VFX Graph が既定の Shader を書き込む

これらは生成物なので git で管理しない(`.gitignore` に入れる)。

導入は専用のブランチで行う。

Omni が検証済みなのは Unity 6000.6.4f1 で、VG のプロジェクトは 6000.6.0f1 である。

バージョンが違うと、Omni は取り込み時に次のエラーを出す。

```
[OmniConversion] ValidatedOnly family=UrpShaderLab rule=Urp.17_6 ... unity=6000.6.0f1 verified=6000.6.4f1
```

VG の検証 script は Unity のエラーを不合格として扱うので、
**Omni を入れたプロジェクトでは、M5Depth、M5Debug、M5Lifecycle が不合格になる**。

そのため、VG core の回帰は Omni を入れないプロジェクトで実行する(§9)。

### 8.5 Omni の tgz の取得

VG のプロジェクトは、リポジトリの外にある Omni の tgz を `file:` で参照する(`Packages/manifest.json`)。

現在の参照先は `D:/Unity/6.6/OmniDist/wiring-fix2/Omni-0.1.0.tgz` で、
Omni のリポジトリのブランチ `fix/package-verification-wiring`(コミット `fe7208c3`。
外部描画経路の公開 API `ExternalDrawPaths.Register` を含む)から作った。

```bash
python Packaging/build_packages.py core --version 0.1.0 --out <出力先>
```

sha256: `e4c507d7380035f68bf08a9afcd635a9094710e70734a1c1380717c309503233`

別の環境では、Omni のリポジトリから同じコミットで作り直し、`manifest.json` の参照先を合わせる。


## 9. 検証の実行

VG core の回帰(EditMode と M0〜M5)は、**Omni を入れないプロジェクト**で実行する。

- 理由:Omni を入れると、Unity のバージョンの違いで Omni がエラーを出し(§8.4)、VG の検証が不合格になる。VG core は Omni に依存しない
- Omni を入れたプロジェクトで実行する検証:P3a(`Assets/Development/PlanetSpike`)、P3b(世界パッケージ)、P2(`com.virtualgeometry.omni`)

新しい git worktree では、`core.autocrlf=true` のため、検証が生成する OBJ が CRLF で取り出される。

検証は LF で書き出すので、初回の実行だけ M4、M5Shared、M5Lit、M5Geometry が
`Build asset version error` で不合格になる。2 回目から合格する。

2026-10-10 の確認:Omni なし・P3a の変更あり(コミット前)で、EditMode 51/51 と M0〜M5 の 11 本が合格した。
M3 の cull の統計(Frustum 棄却、HZB 棄却、可視 cluster、画像の差分)は、変更前の記録と全シナリオで一致した。
Player のビルドと実行(`M5PlayerBuildValidation`)は、この変更では実行していない(惑星の Player は §17)。

2026-10-10 の確認:Omni なし・P2 の core の変更(draw ごとの cull 空間と描画、§11)ありで、
EditMode 51/51 と M0〜M5 の 11 本が合格し、M3 の `Metrics.csv` と `Results.txt` は変更前と一致した。

2026-10-11 の確認:Omni なし・P5a の core の変更(`RequestRegeneration`)ありで、同じく EditMode 51/51 と M0〜M5 の 11 本が合格し、M3 の結果は変更前と一致した。


## 10. 惑星の Cell の選択と生成(P3)

実装は `Packages/com.virtualgeometry.world`(`PlanetCellKey`、`CubeSphere`、`PlanetCellSelector`、`PlanetTerrainSource`、`PlanetTerrainGenerate.compute`)。

cube-sphere の面の軸は、外向き法線が cross(v,u) になる向きに置く。

面の座標から方向へは等角写像(tan)で写す。

同じ階層の隣の Cell は、面の境界をまたぐ場合も整数で求める(浮動小数の誤差を持ち込まない)。

Cell の bounds は、生成済みなら GPU が求めた cluster の bounds を非同期で読み戻したものを使い、無ければ解析的な bounds を使う。

解析的な bounds は惑星全体の高さの幅を含むため大きく、小さな Cell の選択には使えない。

2026-10-10 の確認:解析的な bounds だけでは、地上 10m で Cell が 947 個選ばれて pool(2048)が枯渇した。
実際の bounds に変えると 79 個になり、退避も枯渇も無くなった。

Cell の生成の精度は §12 を参照。

### 検証(`VirtualGeometry.World.Editor.P3bPlanetValidation`)

穴は「内側の球(半径 R−Hmax から、描いている最も粗い階層の弦の沈み込みを引いたもの)に、縁から 2 画素分より深く入る光線の画素が背景色であること」と定める
(P2・P4b の検証も同じ判定)。

縁では地形の輪郭と内側の球の輪郭が近いため、許容量が小さいと描画が正しくても穴と数える。
2026-10-10 に許容量なしで 32 画素を数えた。2026-10-11 には、地形の谷が R−0.56Hmax まで下がる高さの Field で、
高度 20km の縁の本物の切れ込みを 1 画素分の許容量で穴と数えた(縁で谷と内側の球の輪郭が約 1 画素しか離れない)。

継ぎ目の候補は「地形に囲まれた背景」(画像の縁の背景から 4 近傍でたどれない背景の画素)と定める。
峰と峰の間の空の切れ込みは空につながるので数えない(「左右または上下を地形に挟まれた細い背景」は、尖った山の地平線の切れ込みを数えた)。

選択の収束は撮影と同じ解像度で待つ。解像度で画面誤差が変わるので、別の解像度で収束させてから撮ると、
子の生成が間に合わない 1 frame を撮ることになり、結果が直前の pool の中身に依存する。

2026-10-10 の結果(半径 10km、Hmax 300m、目標 2px、640×480 で収束、経路は 320×240):固定の 4 視点で穴と継ぎ目の候補が 0、
目標を 1/4 にした描画との輪郭の差が 1px の帯の外で 0、宇宙から地上までの 121 frame で穴 0、
生成予算 1 での瞬間移動の 60 frame で穴 0。
使用 slot は最大 2048/2048 で、目標 1/4 の細かい描画(最大 1216 Cell)の後の経路では退避 1281 回、
pool が足りずに生成を待たせた要求が 3604 回あった(設定エラーと穴は無し)。

2026-10-11 の結果(高さの Field、§13 に変えた後):同じ項目がすべて合格した。目標 1/4 との輪郭の差の画素は 83〜154(尖った山の輪郭)で、1px の帯の外は 0。


## 11. Omni の Radial 接続(P2)

### core の draw の単位

実行時生成 cluster の供給元は、camera ごとに draw の一覧(`VirtualGeometryClusterDraws`)を組み立てる。

draw ごとに、描画行列(`unity_ObjectToWorld`)、cull 空間への行列、MaterialPropertyBlock(null なら共有)と、描く slot を持つ。

cull は供給元が決めた空間(`VirtualGeometryClusterCullSpace`。既定は world)で行う。

深度が投影行列と同じ写像でない空間(対数描画)では、HZB の遮蔽判定を使わない(`OcclusionAllowed=false`)。

既定の実装(`SelectSlots` を上書きする供給元)は、Transform の行列で 1 つの draw を作る。この場合の挙動は変更前と同じ(§9)。

### 惑星の配置

`PlanetTerrainSource` は Cell の頂点を、Cell の原点(Cell の中心の方向の地表の高さの点 = 半径 R の球面上の点を `HeightShift` だけ外向きに持ち上げた点、惑星のローカル座標。§19)からの相対位置で生成し、Cell ごとに draw する。

惑星を置く空間は `IPlanetPlacement` が決める。

`PlanetTerrainSource` が OnEnable の時に同じ GameObject にある実装を使い、無い、または無効なら Unity の world 空間に置く(`StandardPlanetPlacement`)。

`OmniPlanetPlacement`(`VirtualGeometry.Omni`)は、惑星を Omni の Logical 座標に置く。

- camera ごとに `RadialViewRegistry.TryGetCurrent` の `ViewSnapshot` を読み、無ければその camera では描かない(理由は `LastFailure`)
- 惑星の姿勢は `RenderPose.TryResolve` で Logical のまま解決する
- 惑星ローカルでのカメラ位置は、Logical で eye との差を取り、基底の逆で戻してから float にする
- cull は eye を原点とする world 軸の物理空間で行う。平面は Snapshot の錐台から作り、far は使わない
- `CanUseConeCulling` が false(Radial で eye が warp 中心に無いなど)なら、全面を通す(fail-open)
- Cell の Logical 原点 = 惑星 + 基底·Cell の原点。`DrawTransform.TryCreate(WarpCenter, …)` の値と `GpuFrame` を、Cell ごとの MaterialPropertyBlock へ書く
- 描画行列は惑星の Unity Proxy の行列 × Cell の原点への平行移動(照明の物理位置は Omni の契約どおり Proxy から作る)
- OnEnable で `ExternalDrawPaths.Register(SceneViewContent)` を宣言する

Radial の写像は warp 中心から見た方向を保つので、eye が warp 中心にあれば、頂点の画面位置と三角形の被覆は Unity の world 空間で描いたときと同じになる。

### 確認(camera の状態の解放と Play/Stop の往復、2026-10-12)

core の `VirtualGeometryClusterSource` は camera ごとに GPU buffer(cull の候補・描画記録・可視 index・引数)を持つ(月の source、`maximumDrawSlots` 4096 で 1 camera あたり約 490 MiB)。
camera が破棄されても Dictionary のキーが残り、source が無効になるまで解放されなかった(Scene View、撮影用の一時 camera、閉じた scene の camera)。
`OnBeginContextRendering` で破棄された camera の状態を解放する(`ReleaseDestroyedCameras`。派生の `PlanetLayerSource` は選択の履歴と draw の値も捨てる。`OnCameraReleased`)ようにした。

- `CameraStateReleaseValidation`(`TestResults/Planet/CameraStateRelease`): 月とボールの主 camera で収束後、主 camera の複製 2 台を描くと GPU の memory(`Profiler.GetAllocatedMemoryForGraphicsDriver`)が 973 MiB 増える。
  複製を破棄して 30 frame 進めると、修正前は 0 MiB しか戻らず(戻った割合 0.00、不合格)、修正後は 973 MiB 戻る(1.00)
- core の回帰（Omni なしの基準 worktree、変更した 1 ファイルをコピー）: EditMode 51 本、M0〜M4、M5 の Shared・Lit・Depth・Debug・Lifecycle・Geometry がすべて合格し、M3 の Metrics は変更前とバイト単位で同一
- `PlayStopLeakValidation`(`TestResults/Planet/PlayStopLeak`): 月とボールの scene を Play → 150 frame 描画 → Stop を 10 往復。条件 A(domain reload と scene reload を止める)、B(domain reload だけ止める)で、
  停止後の GPU の memory は 866 MiB のまま(増加 0 MiB)、描画中の最大も 1,354 MiB のまま。既定の domain reload ありは、static の状態ごと検証の状態が消えるので、この枠組みでは測れない
- Unity の object は 1 往復あたり 3 個増える(GameObject、Transform、`RadialSceneBatchDriver`)。増えるのは Omni の `Omni Scene BRG Driver`(`HideAndDontSave` の GameObject)だけで、VG の object は増えない。
  Omni の `RadialSceneBatchRendererService.Shutdown` が `s_Driver` を null にするだけで GameObject を破棄していないように見える(推測、未確認)。Omni への報告は未送信（宛先のセッションが見つからなかった）

### 確認(観察 camera と View Scale、`MoonBallValidation.RunObserver` と `RunViewScale`、2026-10-12)

月とボールの scene(半径は月、目標 2px、640×480、単色の分類)。

**観察 camera(eye が warp 中心にない camera)**: `RadialWarpSource.Observer(主 camera の RadialCamera)` で登録した 2 台目の camera を、主 camera から up 方向へ ε だけ離して描く。`ViewSnapshot` は全ての ε で `IsEyeAtWarpCenter=false`、`CanUseConeCulling=false`(fail-open の経路を通る)。主 camera は `true`/`true`。

- ε = 10^-6 m、10^-4 m(同じ向き): 主 camera の画像と、ボールの輪郭を含めて 1 画素の帯の外で一致(不一致 0、どの種類でもない画素 0)
- ε = 1 m(同じ向き): 月の画素の下(画面の下側)に背景 0 画素。ボールは視野の外(0 画素)
- ε = 1 m、10^2 m、10^4 m(真下を向く): 背景 0 画素(画面全体が月とボール)
- 判定にピンホールの幾何(`CountHoles`)は使えない。warp 中心から離れた eye では、対数圧縮で月の形が変わり(同じ向きで 1 m 上がるだけでボールは視野を外れ、地平線の位置も変わる)、同じ向きのまま 10^4 m 離れると月が 1 画素も描かれなかった(最初の測定)。そのため位相(月の下は月)で判定している。
  観察 camera の見た目が幾何として正しいか(Omni の写像の設計どおりか)は、この検証では確かめていない

**View Scale M**: `RadialCamera.SetViewScale(M)` は論理 Near Clip を Camera の Near ÷ M にする。論理 Near の平面より手前は描かれない。
接触の視点(ボールの面まで約 0.18 m)で論理 Near が約 0.03 m になるよう、Camera の Near を `clamp(0.03·M, 0.01, 0.3)` にして振った(デモの既定は M = 10、Near 0.3)。

- M = 1、10、10^3、10^6: ボール(隙間 0 と 1 mm)が、Logical の位置から光線で求めた解析の球と、1 画素の帯の外で一致(不一致 0、どの種類でもない画素 0、最下の行も一致)
- 全ての M(10^-3、0.1、1、10、10^3、10^6): 月全体の視点(高さ 2.5R)で、穴 0、月の輪郭が解析の円と 1 画素の帯の外で一致(0 画素)
- M = 10^-3、0.1: 接触の視点は比較していない(Camera の Near の下限 0.01 で、論理 Near が 10 m、0.1 m。ボールの面までの距離の半分を超える)
- Camera の Near を 0.3 のままにして M だけを振ると、M ≤ 1 で論理 Near(M = 1 で 0.3 m、0.1 で 3 m、10^-3 で 300 m)がボールの面(約 0.18 m)より遠くなり、ボールが描かれない(`SetViewScale` の仕様どおりで、実装の欠陥ではない)。初回の検証が M ≤ 1 で失敗したのは、この検証側の設定のため

### 確認(Scene View、`MoonBallValidation.RunSceneView`、2026-10-12)

非表示の `SceneView`(`ScriptableObject.CreateInstance<SceneView>()`。Omni の `OwnedHiddenSceneViewRenderRequestTests` と同じ方法)に Omni の状態(`OmniViewState`: Radial、基準は主 camera の RadialCamera)を付け、
`RenderPipeline.SubmitRenderRequest`(`UniversalRenderPipeline.SingleCameraRequest`)で RenderTexture へ描く。月とボールの scene、640×480、camera の画角・near・far は主 camera に揃える。
VG の `VirtualGeometryRenderFeature` は `CameraType.SceneView` を描き、`OmniPlanetPlacement` は `ExternalDrawPaths.Register(SceneViewContent)` を宣言している。

- **Scene View 自身が warp 中心**(`ExploreFromSceneView=true`): 月全体(高さ 2.5R)で、月の画素 73,942(View Scale の検証の月全体の画素数と同じ)、穴 0、月の輪郭が解析の円と 1 画素の帯の外で一致(違い 0)。
  `ViewSnapshot` は Radial 有効、eye は warp 中心、錐台が使える。接触の視点でも月(100,183 画素)とボール(161,171 画素)が描かれる
- **実カメラ基準**(`ExploreFromSceneView=false`。eye が warp 中心でない = fail-open): 月全体で月 77,652 画素、接触の視点で月 99,931 画素・ボール 168,323 画素。
  `ViewSnapshot` は Radial 有効、eye は warp 中心でなく、錐台は使えない。どちらもどの種類でもない画素 0、Unity のエラー 0
- 最初の試行(Scene View の camera の Unity の transform を動かさない)は、月が 0 画素で不合格だった。非表示の Scene View は Repaint されないので、`LookAtDirect` や `OmniSceneViewVerification.TryMoveLogicalCameraForAutomation`(論理ピボットを動かす)では camera の transform が動かず、
  eye が原点のままになる。そのため、検証は camera の transform を主 camera の位置・向きに直接置いている

確かめていないこと: **接触の視点でのボールの位置の精度**(Scene View の eye は Unity の transform の float で、月の面(1.7×10^6 m)では刻みが 0.125 m、ボールの半径 0.11 m より粗い。
この判定を外す前の試行では、接触の視点のボールは解析の球と 10,851 画素足りず・19,932 画素余分だった(現在の検証はボールが描かれることだけを見る)。この精度は Scene View では保証しない。mm の位置が要る確認は主 camera の Logical Transform で行う)、
開いた Editor の Repaint で動く仮想ビューポートの論理ピボット(Logical の目の位置)、Overlay、実際の入力。

### 検証(`VirtualGeometry.Omni.Editor.P2OmniPlanetValidation`)

半径 10km、Hmax 300m、目標 2px、640×480。camera は `Logical.Transform` と `RadialCamera`(`SetIdentityViewScale`、M=1)。

Logical の camera が安全域(±10km)の外にあると、Unity の camera は原点に固定される。

そのため Standard の基準は、Omni の部品を付けない別の camera を同じ姿勢に置いて撮る。

2026-10-10 の結果(Omni を入れたプロジェクト):

- 高度 10km、100m、1.7m(Hmax の球からの距離)で穴 0。Snapshot は Radial 有効、eye は warp 中心
- Standard との比較:輪郭の差 0 画素。色の差は最大 2/255(Omni が照明の物理位置と視線を画素ごとに作り直す丸め)
- 惑星と camera を Logical で (1e9, −2e9, 3e9) m ずらしても、3 視点とも画像がビット単位で一致
- 遠方に置いたまま、宇宙(30km)から 1.7m までの 121 frame で穴 0
- 惑星の有効・無効を 10 回繰り返した後も、エラー無しで描き直せる

未実施:

- 半径 600km は §12 で、穴 0、頂点の精度、Hmax の球から 1.7m〜10km の 3 視点で Standard との輪郭の差 0 を確認した。地面に近い視点での Standard との比較は、基準自体が float の量子化を含むので行っていない
- eye が warp 中心に無い camera の、見た目の幾何としての正しさ(下の「確認(観察 camera と View Scale)」は位相と、主 camera との連続性だけを見ている)
- M = 10^-3 と 0.1 の接触の視点(論理 Near が 10 m、0.1 m で、ボールの面までの距離の半分を超えるので比較していない)
- z-fighting と深度の分解能の測定は §21 で行った
- Play/Stop の往復と、camera の GPU buffer のリークは下の「確認(camera の状態の解放と Play/Stop の往復)」で行った
- 開いた Editor の実際の Scene View(Repaint、仮想ビューポートの論理ピボット、Overlay、マウスやキーの入力)。非表示の Scene View の camera の描画は下の「確認(Scene View)」で確かめた


## 12. Cell の精度の基盤(P4a)

### 方式

Cell の中心の値を CPU が Logical 精度で求め(`PlanetCellFrame`)、GPU は Cell の中心からの相対量だけを float で計算する。

- 中心の角度は π·n/2^(L+2)(n=2x+1−2^L)。n/2^(L+2) を固定小数点で正確に作るため、階層は 94 まで(`PlanetCellFrame.MaximumLevel`)
- 中心の正接 x0、y0 は、角度の sin と cos をテイラー展開(14 項、|角度|≤π/4)で求めて割る。半角の公式の繰り返しは、深い階層で角度の誤差が倍々に増えるので使わない。
  **最後の割り算は Omni の `Scalar.TryDivide` を使わず、BigInteger で割る**(`PlanetCellFrame.TryDivideExact`)。Omni の除算は、被除数の上位 2 語が除数の上位語の 2^32 倍以上になる(Knuth の D の推定商がクランプされる)とき、
  補正ループの比較 `(estimateRemainder << 32)` が溢れて商の桁を 1 つ小さくし、結果が最大で 2^-64 ほど小さくなる(再現: 被除数の raw = 22766045805726834331774088019、除数の raw = 75886816336951627176545601601 で、商が 4,484,077,516 raw 小さい)。
  深い階層では隣り合う Cell の正接の差が 2^-64 に近く、この条件が繰り返し当たるので、半径 10^19 m で隣の Cell が 0.5m、10^20 m で 5m ずれて、地面に穴と重なりができた
- 球面上の原点 `SphereOrigin` = R·p0/|p0|(p0=n+x0·u+y0·v)。R を掛けるのは正規化の後(R² を作らない)。
  Cell の原点 `Origin` は、これを地表の高さ `HeightShift` だけ外向きに持ち上げた点(§19。持ち上げない Cell は `Origin == SphereOrigin`)
- ノイズの格子は octave ごとに、原点の格子番号(2^32 で巡回)と端数を CPU が求め、GPU は端数 + 相対位置の格子だけを float で扱う
- GPU は、頂点の正接の差 tan(a0+δ)−tan(a0) を x0 と δ から差だけで求め(小さな δ は級数)、球面上の点の差 R·(p/|p|−p0/|p0|) を、大きな値どうしの引き算をしない形で求める
- 選択の解析的な bounds は、階層 14 以上では中心の方向を `PlanetCellFrame` で求める(`PlanetCellSelector.FrameBounds`)。
  中心から角・辺の中点までの角度は、面の角度の幅の最大 0.816 倍(面の角の Cell)なので、0.85 倍で見込む
- Omni の配置は、Cell の原点を float にせず Logical のまま惑星の姿勢と合成する

頂点の誤差は惑星の大きさではなく Cell の大きさに比例する。

同じ階層で隣り合う Cell の境界の頂点は、別の中心から求めるので相対誤差 1e-7 程度の差が残りうる(skirt が隠す)。

### 確認(`P2OmniPlanetValidation.RunLarge`、半径 600km、Hmax 3000m)

精度は、最も細かい階層の Cell の頂点を読み戻し、格子に沿った 2 階差分の最大で測る(なめらかな面なら quad² / R 程度)。

2026-10-11 の結果:

| 条件 | 修正前 | 修正後 |
|---|---|---|
| 高さの振幅 1cm の球、地上 1.7m | 階層 18 止まり、quad 0.056m に対し 2 階差分 0.18m(float の刻み約 6cm に量子化)、画像に凸凹と縞 | 階層 21(quad 6.9mm)、2 階差分 1.0e-7m、画像はなめらか |
| 実際の地形、地面から 1.7m(地面の高さは頂点の読み戻しから求めた) | (未測定) | 階層 21、2 階差分 0.9mm(地形の曲率)、穴 0 |

半径 10km の P2・P3b・P3a と、世界パッケージの EditMode テスト 18 本も合格した。

### 確認(半径 10^3〜10^20 m、地上 1.7m、`BodyStackValidation.RunCells`)

天体の半径を 10^3 m から 10^20 m まで変え、地表から 1.7m の目の高さ(水平から 10 度下)で立って描く(2026-10-11)。
どの半径でも、地面の画素数が同じ(半径 10^9 m 以上で 211,840 画素)で、背景・地面以外の画素が 0、隣り合う Cell(同じ面の +u、+v の隣、60m 以内、約 300 組)の共有辺の頂点の差は最大 9 µm。
選ぶ Cell の数は半径に対して対数的にしか増えない(半径 10^3 m で 207、10^6 m で 417、10^9 m で 635、10^12 m で 841、10^15 m で 1,053、10^18 m で 1,273、10^20 m で 1,348。画面誤差 2 画素以下。使う階層は半径 1000 倍ごとに約 5 段深くなり、半径 10^3 m で 7〜12、10^20 m で 37〜68)。
修正前は、半径 4×10^18 m を超えると、目の近くの地面(半径 10^19 m で前方 2〜6m)が欠け、同じ階層の隣の Cell の共有辺が 0.2m〜1m(半径に比例)ずれていた。
世界パッケージのテスト(`PlanetCellFrameTests.TryCreate`)は、半径 10^19 m の隣り合う Cell の原点の間隔が 1e-6 m 以内で一定であることを見る(修正前は 0.327〜1.348m で不合格)。
この修正の後に、P4b(`P4bDescentValidation.Run`)、P2 の `RunLarge`、半径 10^20 m の月とボール(`MoonBallValidation.RunGiant`)を再実行して合格した。

未実施・未解決:

- 地面から 1.7m では pool(2048)を使い切る(半径 600km で 586 Cell 描画、使用 2048)。退避と生成待ちは起きるが、穴と設定エラーは無い

(選択の Logical 化と高さの帯域制限は §13 で扱った。)


## 13. 高さの Field、選択の Logical 化、降下カメラ(P4b)

### 選択の Logical 化

`PlanetSelectionView` はカメラの位置を惑星のローカル座標の Logical で持つ。

Cell の bounds の中心は Cell の原点(`PlanetCellFrame.Origin`、持ち上げた原点。§19)からの相対位置で持ち、カメラとの差を Logical で取ってから float にする(`PlanetCellSelector.FromCamera`)。

地平線の判定に使うカメラの高さ(|カメラ|−R)も Logical で求める(`PlanetSelectionView.CameraAltitude`)。

cull 空間での位置は CameraCull + LocalToCull·(Cell − カメラ)。Omni の配置では CameraCull=0、LocalToCull は惑星の基底。

生成した頂点から読み戻す実際の bounds も、Cell の原点からの相対位置で持つ。

### 高さの Field

`PlanetHeightField`(C#)と `PlanetHeight.hlsl`(GPU)は同じ式で、片方を変えたらもう片方も変える。

- 高さ = Hmax·clamp(大陸 + 山 + 細部, −1, 1)。振幅は Hmax に対する割合(`PlanetHeightSettings`)
- 大陸: 最初の 4 octave の fBm を、海面のしきい値で陸と海に分ける(smoothstep)。海は −OceanDepth、陸は PlainsHeight
- 山: 陸の上だけに ridged noise((1−|n|)² − 0.4)を 6 octave
- 細部: 残りの octave の fBm(平均 0)
- octave の格子は「原点の格子番号 + 端数 + 相対位置」に分ける(§12)。octave は 32 まで
- 帯域制限: 波長が 2·quadLength より短い octave は足さない(CPU の地表の評価は quadLength=0 で全部足す)。
  振幅÷波長が octave によらず一定なので、落とす帯域の振幅は quad の数 % にとどまる

Override(地域の作り込み)と実データの基盤は、まだ実装していない(Field の差し替え口は未定義)。

### 降下カメラ(`PlanetDescentCamera`)

- Omni の Logical.Transform(親を持たない)を動かす。Unity の Transform は Omni の `SyncUnityTransform` だけが書く
- 速度は地表からの高さに比例する。地表から eyeHeight(既定 1.7m)より下へは行かない(CPU の高さの Field、`PlanetTerrainSource.TrySampleSurface`)。
  海(`PlanetOceanSource`)を設定すると、海面から波の高さの上限より下へも行かない
- 操作(Input System): WASD で前後左右(カメラの向き)、E/Q で上下(惑星の中心から外向き)、右ドラッグで向き、ホイールで速度の倍率
- 瞬間移動は 2 段階にする。`PrepareTeleport` で移動先の真下の Cell と周りの 3×3 を、quad が eyeHeight 以下になる階層まで先に生成させ
  (`PlanetTerrainSource.Prefetch`)、true になってから `Teleport` する

先に生成しないと、生成が追いつかない間に描く粗い Cell の面がカメラより上に来て、地面に潜って見える
(2026-10-11 に、粗い面がカメラより 6〜66m 上に描かれ、穴を数えた)。粗い面は細かい地形をならすので、谷では本当の地表より高い。

### 確認(`VirtualGeometry.Omni.Editor.P4bDescentValidation`)

宇宙(高さ 1.5R)から降下カメラで地表 1.7m まで降り、地上を 120 frame 歩き、4 か所へ瞬間移動(地表より 50m 下を指定し、押し戻される)して、毎 frame 次を確かめる。

- 穴 0
- CPU の地表からの高さが eyeHeight 以上
- 描いた面がカメラより上に無い(カメラを通る上方向の直線と、真下に描いている最も細かい Cell の三角形の交点で測る)
- CPU と GPU の頂点の差が、Cell の大きさの 1e-5 倍 + 1mm 以内(選択中の Cell を階層ごとに 2 個、格子の 9 点)

2026-10-11 の結果:

| 半径 | Hmax | 地表での最も細かい階層 | 降下 | CPU と GPU の差(地表) | 結果 |
|---|---|---|---|---|---|
| 600km | 3000m | 21 | 191 frame | 最大 5.0mm(許容の 0.26 倍) | 合格 |
| 6,371km | 8000m | 25 | 226 frame | 最大 24mm(許容の 0.55 倍) | 合格 |

半径 10km の P2・P3b・P3a、半径 600km の P2(RunLarge)、世界パッケージの EditMode テスト 19 本も合格した。

未実施:

- 人の操作(Input System の入力)による降下カメラの確認(検証は `Move` と `Teleport` を直接呼ぶ)
- Override と実データの基盤
- 10^20 m までの大きさ(P6)
- 地上での pool の不足(1.7m では 2048 を使い切り、退避が続く)の調整


## 14. 海の Layer の形(P5a)

### 構成

地形と海は、どちらも `PlanetLayerSource`(Cell の選択・生成・退避・配置・draw・先読みの共通部分)の派生で、同じ GameObject に置く。

- `PlanetTerrainSource`: 高さの Field の地形。生成は PlanetTerrainGenerate.compute
- `PlanetOceanSource`: 海面(半径 R の球)を Gerstner 波で動かしたもの。生成は PlanetOceanGenerate.compute。地形(`Terrain`)を参照する

GPU の Cell の格子の部品(面の表、正接の差、Cell の中心からの球面の点)は PlanetCell.hlsl、CPU は `PlanetCellGeometry` で、同じ式。

### 波

- 成分は Pierson–Moskowitz スペクトル(風速 10m/s が既定)を、山の波長の 2 倍から最短波長(既定 0.5m)まで対数で等間隔に 24 個へ分けたもの
- 方向は 3 次元の単位ベクトル(seed で決める)。位相 = 2π·(Cell の原点での端数 + d·(相対位置)/λ) − ω·t。原点での端数は CPU が Logical で求める(`PlanetCellFrame.TryWavePhase`)
- 時刻 `WaveTime` は倍精度(秒)。位相 ω·t は、倍精度で 2π に巻いてから float にして渡す(`PlanetOceanSource.TimePhase`。ω は float の値そのもの)。
  float の時刻だと、5×10^5 秒(約 6 日)を超えると frame ごとの加算(約 0.016 秒)が丸めで消えて波が止まり、位相の積(ω は最大 11 rad/s)も 10^5 秒で 0.05 rad 以上狂う
- 帯域制限: 波長が 2·quadLength より短い成分は変位に入れない(P5b で法線と粗さへ移す)
- 波の入る階層の Cell は、描く frame ごとに作り直す(core の `RequestRegeneration`: 描ける状態のまま作り直しを予約する)。粗い Cell は静止した球面
- 選択の bounds には、波の変位の上界(Σ振幅·(1+尖り))を含める
- 水深(海面からの地形の深さ)を UV の x に入れる(P5b の shader で使う)

海岸線は地形と海の深度の交わりで決まる。

### 確認(`VirtualGeometry.Omni.Editor.P5aOceanValidation`、半径 600km、Hmax 3000m)

2026-10-11 の結果:

- 海岸の 4 視点(陸の地上 1.7m、海岸の上 100m・1km・10km)で穴 0
- CPU と GPU の海面の差は最大 0.2〜4mm、水深の差は最大 1.2mm
- 帯域制限: 波を落とす Cell で、GPU の頂点は全成分の評価と 0.54〜1.43m 違い(落とした波の分)、帯域制限した CPU の評価とは mm 以下で一致した
- 時刻を 1.5 秒進めると、海の画素の大半(92,829 画素)が変わった
- 長時間後の波の位相(2026-10-12): 時刻 10^6 秒と 10^7 秒(約 12 日、116 日)で、GPU の頂点が、位相を倍精度で巻いた参照(`PlanetOceanSource.TryEvaluateVertexInDouble`)と最大 0.19 mm・0.15 mm で一致する(波の振幅は 4.3 m)。
  GPU と CPU の float の式は同じ誤りをするので、比べる相手は倍精度の参照にした。位相を float の積で巻く旧式に戻すと、7 cm(10^6 秒)・1.32 m(10^7 秒)ずれて不合格になる。
  単体テスト(`PlanetOceanSourceTests`)は、位相が 10^3〜6×10^8 秒で倍精度の参照と 1e-5 rad 以内、旧式の float の積が 10^5 秒以降で 0.05 rad 以上狂うこと、
  毎 frame 1/60 秒を 10^8 回足した倍精度の時刻が 1e-3 秒以内で、float の加算が止まることを確かめる
- z-fighting: camera を 0.1mm ずらして撮り直すと、海と陸の境から 1px の帯の外で判定が入れ替わる画素は 0。
  帯の中(境の線そのもの)では 0〜2 画素が入れ替わった(0.1mm のずれで境の線が画素の中心を横切るため。深度の精度の問題とは区別できていない)
- 高度帯の遷移(目標 1/4 との比較): P5a の時点(材質は単色の Lit、海岸線は深度の交わり)では、1.7m と 10km では海と陸の境が 1px の帯の外で 0、
  **100m と 1km では 2 画素ずつ帯の外で違い、不合格だった**。違ったのは 1〜2 画素の小さな陸の点(水面からわずかに出た岩の先)で、
  細かい描画で入る短い波と細かい地形で水面との上下が入れ替わる。傾きのゆるい海岸ではわずかな上下の差が大きな水平のずれになる。
  P5b の shader が海岸線を Field の水深で決めるようにして解消した(§15)

### 陸だけの Cell の海を描かない最適化(`PlanetOceanSource.SkipLandCells`、2026-10-12)

海の Cell は惑星全体を覆う四分木なので、陸の上の Cell も海の Cell として選ばれ、fragment の `clip(depth)` で全画素を捨てるだけになる。
Cell の全頂点の水深(UV の x)が負なら、補間した水深も全画素で負なので、その Cell は描いても描かなくても画像が同じ(厳密)。そういう Cell は draw にしない。

- 生成の直後に、`ReduceDepth`(`PlanetOceanGenerate.compute`、1 group)が slot の全頂点のうち水深が 0 以上(または NaN)のものを数え、`AsyncGPUReadback` で数 frame 後に読み戻す。0 なら陸だけ。
  水深は時刻に依らないので、Cell を新しく割り当てたときだけ縮約する(作り直しでは求め直さない)。slot を別の Cell に使い直すと世代(`SlotVersion`)が変わり、古い結果は捨てる。結果が届くまで、または NaN があれば、描く(安全側)
- `PlanetLayerSource.SkipDraw(slot)`(派生クラスが上書き。飛ばした Cell は `OnCellDrawn` も呼ばない)、`LastDrawnCells`・`LastSkippedCells`
- 確認(`P5aOceanValidation.CheckSkipLand`。海岸の 4 視点、時刻は固定): 飛ばす設定と飛ばさない設定で、単色と見た目の画像が **全画素で一致**(違う画素 0)。
  描いた Cell + 飛ばした Cell が、飛ばさない設定の Cell 数と一致。飛ばした Cell は draw の 19〜63%(447 → 360、341 → 280、201 → 140、140 → 52)
- CPU の水深(全 65×65 格子点)との照合: 飛ばした 40 Cell は全格子点が陸、飛ばしていない 40 Cell は全格子点が陸ではない。その中に陸と海の格子点が混在する Cell(海岸を含む)が 9 あり、いずれも飛ばしていない
- 負の対照(飛ばす条件を、結果が届いた全 Cell に広げた): draw が 0 になり、画像が 87,571〜304,109 画素変わって FAILED
- 時間の節約は測っていない(CPU の提出が Cell 数に比例する §17 から、飛ばした Cell の数が目安。GPU の時間は §17 のとおり未測定)

未実施・未解決(P5b で扱ったものは §15):

- 深度の分解能の実測は §21 で行った


## 15. 海の shader(P5b)

`Packages/com.virtualgeometry.omni/Shaders/RadialOcean.shader`(`.vgshader` が VG 用 pass へ複製する。名前は `VirtualGeometry/Omni/RadialOcean`)。
Omni の `RadialGlobalLit` の forward pass の頂点部分(Radial の物体ごとの経路)をそのまま使い、fragment を海にした。
Radial のプロパティは Properties に宣言しないと MaterialPropertyBlock の上書きが黙って無視されるので、元と同じ宣言を持つ。

### fragment がすること

1. **海岸線**: 頂点の UV.x(水深。地形の高さの符号を変えたもの)が 0 以下の画素は描かない。海岸線は地形の高さの Field の 0 の等高線で、深度の交わりに頼らない
2. **法線**: 帯域制限で頂点の変位から落とした波のうち、波長が画素の大きさ(`ddx/ddy` した位置の変化量)の 2 倍以上のものを、高さの勾配 A·k·cosθ·d_t として法線に足す
3. **粗さ**: 画素より短い波の傾きの分散 ½(A·k)²|d_t|² を、粗さの 2 乗に足す。波長が画素の 1〜2 倍の間は、法線から粗さへなめらかに移す(`resolved` の重み)
4. **色と泡**: 水深で浅瀬の色と深い所の色を混ぜ(exp(−水深·吸収))、水深の浅い所に泡を足す。泡の模様は 2 つの波の位相の積(位相は Cell の原点での端数を含むので Cell の境で途切れない。Cell の原点からの相対位置だけで作ると境で途切れた)

波の位相の計算には、変位する前の点(波の「札」)が要る。fragment には変位後の位置しか届かないので、
`position = sphere + offset(sphere)` を 3 回の不動点反復で解く(`OceanLabel`。反復ごとに誤差が kA 倍に縮む)。
波の式は PlanetOcean.hlsl に置き、頂点の生成(compute)と shader で共有する。

Cell ごとの値(波の位相の端数、Cell の向きと quad の大きさ)は、Cell ごとの MaterialPropertyBlock で渡す(`PlanetLayerSource.WriteCellProperties`)。
位相の端数は slot ごとに cache し、Cell が変わったときだけ Logical で求め直す。
波の配列(方向・形・時刻の位相)も同じ block に書く(MaterialPropertyBlock の配列が shader へ届くことを、波が動くことで確認した)。

### 確認(`P5aOceanValidation`、半径 600km、Hmax 3000m)

海岸線と LOD の検証は `_FlatColor=1`(照明・泡なしで海を単色にする)で、見た目は `_FlatColor=0` で撮る。

2026-10-11 の結果:

- 海岸線の LOD の遷移(目標 1/4 との比較): 4 視点すべてで、海と陸の境が 1px の帯の外で **0**(P5a では 100m と 1km で 2 画素ずつ違った)
- z-fighting(camera を 0.1mm ずらす): 境から 1px の帯の外で判定が入れ替わる画素は 0。境の線の上では 0〜2 画素
- 見た目の遷移(目標 1/4 との比較。海の 8×8 の block の平均の明るさの差): 平均 0.0013(1.7m)、0.0043(100m)、0.0076(1km)、0.0004(10km)。
  最大は 0.029、0.172、0.276、0.007(波の反射の粒の位置が、細かい描画では頂点の変位で、粗い描画では法線で、わずかにずれる)。合格は平均が 0.03 以内
- 時刻を進めると海の画素が約 88,000 画素変わる
- 地形・海の CPU と GPU の一致、帯域制限の確認、穴 0 は P5a と同じ

### 確認(波の法線と不動点反復の精度、`OceanProbeValidation`、2026-10-12)

`PlanetOcean.hlsl` の `OceanLabel`(札を求める不動点反復)と `OceanShortWaves`(帯域制限で落とした波の傾きと分散)を、compute(`PlanetOceanProbe.compute`。shader と同じ include を使う)で点ごとに評価して読み戻し、倍精度の C# の参照と比べた。
点は、Cell の格子の中の乱数の位置で、頂点の生成と同じ式の変位(帯域制限で残る波)を倍精度で足した、変位した海面の上の点(半径 600 km、風速 10 m/s、24 成分、階層 8・10・12・14・16、時刻 0・12.3・1000 秒、画素の大きさ 0.01・0.1・1・10 m、14,400 点)。
反復の回数は `RadialOcean.shader` の `OCEAN_LABEL_ITERATIONS`(3)を読む。

- **実装の一致**: GPU の札は、同じ式(同じ反復回数)の倍精度の評価と最大 8.9×10^-5 m、傾きは最大 2.1×10^-4、分散は相対 2.6×10^-7 で一致(許容は 5×10^-4 m、10^-3、10^-3)
- **不動点反復**: 札の誤差は反復ごとに約 1/4.7 に縮む(1 回 0.244 m、2 回 0.051 m、3 回 0.0109 m、6 回 0.00016 m。収束は 200 回で |L201−L200| = 2.3×10^-13 m)。
  3 回の札の誤差が、解像される最短の波(波長 0.5〜1.8 m)の位相に与える誤差は最大 0.064 rad(許容 0.15)。傾きの誤差は 3 回で最大 0.014
- **傾きの式の一次近似**(Gerstner の水平成分=尖りを、法線へ足すときは無視している): 正確な札で、帯域制限で落とした波の変位した面の法線(尖りを含む、中心差分)から求めた傾きと比べて最大 0.024
- **合計**(GPU の傾き − 正確な傾き。傾きの差 ≈ 法線の角度の差 rad): 最大 0.023(約 1.3°)、平均 0.0017、rms 0.0029。傾き自体の rms は 0.07(誤差の平均は約 2.5%、最大は約 33%)。許容は最大 0.05、(階層・画素の大きさごとの)平均 0.008
- 反復を 2 回にしても合計の誤差は 3〜6 回と同じ 0.0224 で、傾きの式の一次近似が支配的になる。1 回だと最大 0.08 で不合格(負の対照: `-oceanProbeIterations 1` で、合計の最大が 0.08 > 0.05、位相の誤差が 2.76 rad > 0.15 で FAILED)。3 回は余裕をもって足りる

この検証が見ないもの: 頂点の間の補間(三角形の chord)で、補間した位置が変位した面から離れる分(点は変位した面の上に取った)。波は水深に依らないので、外洋と海岸の区別はしない。画素ごとの法線と頂点の法線の合成(`OmniRadialObjectToLogicalNormal`)は shader の変換で、この検証の外。

未実施・未解決:

- 空の反射・大気・水中の見え方は未実装


## 16. 月とサッカーボール(大きさの違う物体の精密な配置、P6 の一部)

デモ scene は `Assets/VirtualizedGeometrySample/MoonAndBall/`(`MoonAndBall.unity`、`GiantAndBall.unity`、操作は README.md)。

### 課題と、Cell に分ける方法

Omni の CosmicScale のデモでは、月を 1 つの物体(単位球を直径 3,475 km に拡大したもの)として描くと、
サッカーボールの近くで月の模様・法線・SV_Depth が物体空間の float の分解能(約 0.2 m)で量子化され、
月の面がボールの縁を越えて手前に出た(COSMIC-PRECISION-001。`D:\Unity\6.6\Omni\Docs\CurrentWork.md`)。
Omni は月を四分木のパッチに分け、頂点をパッチの中心からの差で持って解決した。

本システムは、同じ考え方を cube-sphere の Cell(`PlanetLayerSource`)として一般にしてある。

- Cell の頂点は Cell の中心(原点)からの相対位置で持つ。誤差は惑星の大きさではなく Cell の大きさに比例する(§12)
- Cell の原点と物体の位置は Logical で持つ。camera との差を Logical で取ってから float にする(§13)
- Cell は画面の誤差で選ぶ。ボールから 0.28 m の視点では、月の Cell は 1 quad が約 0.6 mm(階層 26)まで細かくなる

### 構成

- 月(半径 1,737.4 km、高さ ±300 m)とボール(半径 0.11 m、高さなし)は、どちらも `PlanetTerrainSource`
- 月の shader(`RadialMoonGrid`)は、基準点(接点)を原点とする格子(1m、10cm、1cm)を描く。格子の座標は Cell の原点 − 基準点を Logical で引いた値(`_PlanetCellRef`、`PlanetLayerSource.SetReferencePoint`)から作る
- ボールの shader(`RadialBall`)は、切頂二十面体の模様(黒い五角形と白い六角形)をボールの中心からの方向で決める。ボールの回転に付いて回り、Cell の境で途切れない
- camera は Omni の `Logical.Transform` と `RadialCamera`(論理 near = 0.3 m ÷ 10 = 0.03 m、画角 50°。Omni の CosmicScale と同じ)
- `MoonBallDemo` が接点とボールの位置を Logical のまま計算する(接点 = 月の中心 + 単位ベクトル·(R + 高さ)。ボールの中心 = 接点 + up·(r + 隙間) + u·x + v·z。1 単位 = 0.1mm の整数で持つ)。
  接点の方向の単位ベクトルは Logical で正規化する(float のままだと、半径 10^20 m では球から外れる)

### 確認(`MoonBallValidation`、`MoonBallPlaySmoke`)

camera は Omni の COSMIC-PRECISION-001 と同じ配置(月の面から 0.17 m、ボールの中心から 0.28 m)。
月は灰色、ボールは赤の単色で撮り、背景はマゼンタにして分類する。

「描かれたボール」と「Logical の位置から光線で求めた球(解析)」を画素ごとに比べる。
解析の球は、camera とボールの Logical の位置の差(Logical で引いてから float)と、camera の向き・画角から作るので、描画とは独立している。
一致は、解析の球の輪郭から 1 画素の帯の内で見る。Omni の現象(月の面がボールの縁を越える)は、この比較の「余分」と「足りない」に出る。

2026-10-11 の結果(半径 1,737.4 km の月 / 半径 10^20 m の天体。どちらも同じ結果):

- 接触: ボールと月の、描いている頂点の間の隙間。月は最下の頂点で 5.9 µm、最小 0.45 µm。10^20 m では最下の頂点で −0.44 µm、最小 −0.59 µm
  (負は 0.6 µm のめり込み。float の丸め。mm よりずっと小さい)。ボールの接点付近の Cell は、真横の視点では地平線の向こうとして選ばれないので、接点が見える近接の視点(面から 1 cm、接点を見る)で測った
- 1mm 単位の動き(47 枚): 隙間 0〜12mm(1mm ごと)、転がす 1〜8mm、手前 1〜8mm、0.1mm ごと 0〜0.9mm、近接の視点の隙間 0〜6mm。
  全部で、描かれたボールが解析の球と 1 画素の帯の外で違う画素は 0、背景・ボール・月のどれでもない画素も 0。
  ボールの最下の行は、1mm ごとにちょうど 2 行動く(解析も同じ。画角と距離から 1mm = 約 1.8 画素)
- ズーム: 月全体(高さ 2.5R)から接触の視点(0.17 m)まで高さを 1/20 ずつ縮める 7 段(10^20 m では 18 段)で、穴 0。月全体の段では、月の輪郭が解析の円と 1 画素の帯の内で一致した
  (10^20 m では、Cell の階層が 71 まで細かくなる)
- Play Mode(batchmode、`MoonBallPlaySmoke`): scene を開いて Play すると、MoonBallDemo が自分で初期化し、自動の動きでボールが 26mm 転がり、エラーなし。
  キーの割り当てと量(上記)も合格

### 確認(起伏のある地面、`MoonBallValidation.RunRealMoon` と `RunSyntheticMoon`、2026-10-12)

月に実データ(LOLA 全球 + SLDEM の局所。§18)または合成の高起伏(−7,300 m〜+9,000 m、勾配は最大約 0.3)を載せ(`MoonBallSettings.Raster`。手続きの高さは 0)、4 地点にボールを接して置く。接点の方向は `MoonBallDemo.ContactDirection` で選ぶ。
実データの地点は、SLDEM の中心(−718 m。原点を持ち上げない)、(−172°, −70°)(−7,304 m)、(−154.3°, −39.3°)(−7,051 m)、(−158.3°, 3.3°)(+8,415 m。正の持ち上げ)。合成は、格子の最大・最小の位置、経度・緯度 0、最大の近くの勾配が大きい所。各地点で次を確かめる。

- 接触の視点: 隙間 0〜3 mm の 4 段で、描かれたボールが解析の球と 1 画素の帯の外で違う画素 0、背景・ボール・月のどれでもない画素 0
- 近接の視点(隙間 0): (1) 接点の真下の月のメッシュの高さが、CPU の地表の高さ(接点)と一致する。(2) 解析の球(半径 r)と月のメッシュの隙間(up 方向)の最小が、傾き s の面から決まる値 −r(√(1+s²)−1) と一致する(傾きは接点の ±20 mm の中心差分)。(3) ボールのメッシュの頂点が球面にある
- 隙間 1〜3 mm: (4) 隙間 0 と共通の Cell の頂点が、up 方向へちょうど k mm、接平面の方向には 0 動く(ボールの Logical の位置が惑星の中心から 1.7×10^6 m でも 1 mm のまま)

結果(RTX 4060 Ti、batchmode)は、実データ・合成とも PASSED:

- 実データ: (2) の差 0.01〜2.2 µm(傾き 0.0013〜0.067。原点を持ち上げない SLDEM の中心も 2.2 µm)、(1) の差 0〜2.5 µm、(3) 0.015 µm、(4) 0.03 µm 以下
- 合成: (2) の差 −7.5〜+7 µm(原点を持ち上げない 406 m の地点は −20.7 µm)、(1) の差 0〜6.8 µm。傾きは最大 0.124(食い込みは 0.8 mm)。画像の不一致は全段で 0
- 持ち上げなし(`-heightShiftThreshold 1e9`)は FAILED(期待どおり): (1) の差 0.14〜0.52 mm、(2) の差 0.2〜0.67 mm(月の頂点の float の刻み。|高さ| 7〜8 km)

置き方の幾何: `MoonBallDemo` はボールの中心を、接点の外向き up に沿って置く(面の法線に沿って置くのではない)。そのため、傾き s の面では隙間 0 でも r(√(1+s²)−1) だけ食い込む
(s = 0.12 で 0.8 mm、s = 0.0013 で 0.1 µm)。隙間 0 は「接点の真上に置く」で、傾いた面にちょうど接する置き方ではない。
ボールのメッシュは camera から見える Cell だけが選ばれ、傾いた面の最も食い込む点の Cell が選ばれないことがあるので、月のメッシュは解析の球で測る
(ボールの頂点の「高い方向に低い 64 個」で測ると、傾き 0.1 以上で食い込みを 1/2〜1/3 に見誤った)。

### 確認(起伏のあるボール、`MoonBallValidation.RunReliefBall`、2026-10-12)

`RadialBall` の `_PlanetCellUp.w = R + H` を H ≠ 0 で通した。ボール(半径 0.11 m、振幅 20 mm、波長 50 mm、6 octave の起伏)を、Cell の原点を持ち上げない設定(`HeightShiftThreshold` 1e9)と、全 Cell を持ち上げる設定(0)で描き比べる。
全 117 Cell を持ち上げた(`HeightShiftThreshold` 0。最大 H = 9.0 mm)。頂点の位置(接点からの相対)の差は 117 Cell すべてで 0.031 µm、単色の画像の不一致 0、見た目(模様)の画像の不一致(1 画素の帯の外、1 チャンネルの差 24 超)0(最大差 2)で PASSED。
選択の収束は撮影と同じ解像度で待って、数・頂点・画像を同じ選択で読む(別の解像度だと、撮影の 1 frame 目は子の生成待ちの粗い選択で、117 Cell のうち 39 Cell しか比べなかった)。
shader で H を無視する(`_PlanetCellUp.w` を R に固定する)と、見た目の不一致が 5,760 画素(最大差 213)になって FAILED(確認後に元へ戻した)。

### 結論と限界

月とボールの 10^7 倍の違いでも、10^20 m の天体とボールの 10^21 倍の違いでも、ボールを面にぎりぎり接して 1mm 単位で動かせた。
Omni の COSMIC-PRECISION-001(月の面がボールの縁を越える)に当たる現象は出なかった。
半径 10^20 m の天体は、ボールの大きさでは平面と区別がつかない(1 m 離れた所の球面の曲がりは約 5×10^-21 m)ので、巨大な平面と同じ実証になる。

- 分割しない場合(単純に拡大した面)との比較は §20。ボールを 1 mm ずつ沈めると、単純な拡大では交わりが飛び、VG の面では解析と一致した
- この節の 2 つの scene の月は手続き生成の起伏。実データ・合成の高起伏の月でも確かめた(上)。ボールは滑らかな球で、模様は shader(パネルの形状はない)。起伏のあるボールは `RunReliefBall`
- 色は照明ありの見た目も画像で確かめたが、見た目の合格条件はない(位置と形だけを数えた)
- キー操作は、`IMoonBallKeys` を差し替えて確かめた(押した frame に 1 回分だけ動く: ↑↓←→ PageUp/Down が各 1mm、Shift で 10mm、Ctrl で 0.1mm、両方なら Ctrl 優先。1/2/3/Space/R の結果も確認)。
  実キーボードから Input System を通る経路は確かめていない。焦点の無い batchmode では、仮想のキーボードに積んだ状態イベントが `InputSystem.onEvent` に 1 つも届かなかった
  (Input System の設定は `IgnoreFocus` と `AllDeviceInputAlwaysGoesToGameView`、`gameIsPlaying` と `gameHasFocus` は true、Dynamic 更新は回っていた。原因は未特定)。
  HUD の文字(IMGUI)は Game View で見ていない(batchmode では写らない)
- 論理 near が 0.03 m なので、近接の視点(面から 1 cm)では camera の真下の 3 cm 以内の地面が切れて背景が見える(near の切り取り)
- 4 つの天体(0.11 m、1 m、月、10^20 m)の同時配置は §17。この節の 2 つの scene は、ボールと 1 つの大きな天体の 2 つだけ
- 描いている頂点(メッシュ)の精度は、半径 6,371 km の地形の CPU と GPU の差(最大 24 mm、§13)のように、大きな半径の地形では Cell の原点の float の近似による数 mm〜数 cm の差が出うる。
  この demo の接点では、月の高さの CPU と GPU の差は 6 µm 以内だった(CPU の高さで置いたボールの接触の隙間で確かめた)。差の原因は調べていない


## 17. 4 つの天体の同時配置(P6)

デモ scene は `Assets/VirtualizedGeometrySample/BodyStack/BodyStack.unity`(操作は README.md)。

### 構成

- 下から、巨大な天体(半径 10^20 m、高さ ±100 m)、月(半径 1,737.4 km、高さ ±300 m)、球(半径 1 m)、ボール(半径 0.11 m)を、同じ垂直線の上に積む。すべて `PlanetTerrainSource`(球とボールは高さなし)
- 隣り合う天体の接点を 1(巨大な天体と月)、2(月と球)、3(球とボール)とする。上の天体の中心 = 下の天体の中心 + 下の天体の上の面の距離 + 上の天体の下の面の距離 + 隙間(上向き)+ 面に沿った移動。
  すべて Logical のまま計算し、面の距離は CPU の高さの Field(`TrySampleSurface`)で求める。接点を動かすと、その上に積んだ天体も一緒に動く
- `BodyStackDemo` が 1mm(Ctrl で 0.1mm)単位の移動、接点ごとの camera の視点(1・2・3)、全体の視点(4)を持つ。キーの入力元は `IMoonBallKeys`(§16)
- 月の格子の基準点は月の上の面(接点 2)、巨大な天体の基準点は接点 1(`SetReferencePoint`)

### 確認(`BodyStackValidation.Run`、2026-10-11、半径 10^20 m + 月 + 球 + ボールを同時に描く)

| 項目 | 結果 |
|---|---|
| 接点 3(球とボール)の隙間(メッシュ、接点の位置) | 隙間 0 で 1.0 µm。1、2、3mm 離すと変化は 1.000、2.000、3.000mm |
| 接点 2(月と球)の隙間 | 隙間 0 で 18 µm(月の頂点の float の刻み)。1、2、3mm 離すと変化は 1.000、2.000、3.000mm |
| 接点 1(巨大な天体と月)の隙間 | 隙間 0 で 1.3 µm。1、2、3mm 離すと変化は 0.991、1.996、2.986mm(差は 14 µm 以内。月の頂点の float の刻みは約 15 µm) |
| 接点 2・3 の画像(単色)と、Logical の位置から倍精度の光線で求めた球の比較 | 隙間 0、1、2、3、12mm、転がし 1、8mm、手前 1、8mm の各 frame で、ボールと球の画素が、解析の球の輪郭から 1 画素の帯の外で違う数は 0。背景・4 天体のどれでもない画素も 0 |
| 全体の視点(月と巨大な天体の面)の画像と解析(月の球と面の平面)の比較 | 4 種類の領域がどれも 1 画素の帯の外で違う数 0(月の画素 92,491 に対し解析 92,484)。月が巨大な天体の面に接している |
| 距離を変える(隙間 0) | 月の上の球を 3.5、10、30、100、300 m から、球の上のボールを 0.28、1、3、10、30 m から見て、画像の一致が崩れる距離は見つからなかった |
| 論理 near を小さくする(View Scale M) | M = 10、100、300、1,000、3,000(論理 near 30mm〜0.1mm)で、画像は同じで 1 画素の帯の外で違う数は 0 |
| 地上に立ったときの Cell 数と継ぎ目 | §12 の表(半径 10^3〜10^20 m。Cell 数は対数的、地面は欠けず、継ぎ目は最大 9 µm) |
| 描いた頂点の inf・NaN | 0(接点の近くの Cell の読み戻し) |

接点 1 の視点は巨大な天体の面の上 0.17m(月の中)で、月は裏面なので描かれず、月の skirt が細い線に見える(月を外すと、画面は全部が巨大な天体の面になる: 307,200 画素)。
半径の大きな 2 つの面が近づくだけなので、接点の近くでは 1mm の隙間の変化は画面では見えない(隙間が 1mm から 2mm に増えるまでに、接点から横に約 60 m 離れる)。そのため、接点 1 は頂点の読み戻しで確かめた。

### 描画の時間(`BodyStackValidation.RunTiming`、640×480、DX12、RTX 4060 Ti、batchmode の Editor)

画像を読み戻さずに 60 frame 続けて描き、CPU が描画を提出する時間と、まとめて GPU を待った全体の時間を測った(選択が収束してから測る)。

| 視点 | 描く Cell(巨大な天体/月/球/ボール) | 提出(Debug コード、Release コード) | 全体(Debug コード) |
|---|---|---|---|
| 接点 3 | 971 / 637 / 69 / 64 | 約 79 ms、約 40〜52 ms | 約 94 ms |
| 接点 2 | 932 / 727 / 50 / 6 | 約 76 ms、約 37〜42 ms | 約 89 ms |
| 接点 1 | 1,599 / 756 / 0 / 0 | 約 105 ms、約 53〜59 ms | 約 112 ms |
| 全体 | 1,010 / 51 / 6 / 6 | 約 48 ms、約 23 ms | 約 57 ms |

- 提出の時間は、描く Cell の数にほぼ比例する(1 Cell あたり、Release コードで約 21〜30 µs、Debug コードで約 44 µs)。**目安の CPU 2ms を大きく超える。** Release コードは Editor の `-releaseCodeOptimization` で、実行ごとに 15% ほどばらつく
- 内訳(Debug コード、接点 3): 巨大な天体の Cell の選択 9.9 ms、描画の準備 19.6 ms(配置 `Describe` 17.3 ms、Cell ごとの値 1.6 ms)、月の選択 6.2 ms、準備 13.0 ms。選択と準備は Cell の数に比例する。
  Release コードでは、巨大な天体の選択 2.5 ms、準備 7.0 ms(配置 6.3 ms)。残りの約 30 ms は、Cell ごとの draw の提出(Cell ごとに 1 回の indirect draw と MaterialPropertyBlock)と考えられる(仮説。draw を 1 回にまとめる P2b で確かめる)
- 描画の準備で重いのは、Omni の物体ごとの値(`DrawTransform` と Logical の姿勢の合成)の Cell ごとの計算(Release コードで約 6 µs/Cell)
- Editor では GPU の frame 時間は測れなかった(`FrameTimingManager` は batchmode の Editor では GPU 時間に 0 を返した。上の「全体」は CPU の提出が律速の値で、GPU 時間の上限の目安にもならない)。GPU 時間は下の Player で測った

### Player のビルドと実行(`PlanetPlayerBuild`、`PlanetPlayerSmoke`、2026-10-12)

BodyStack の scene に `PlanetPlayerSmoke`(`VirtualGeometry.Omni` の MonoBehaviour。Editor では何もしない。引数 `-planetResults <folder>` があるときだけ動く)を足して Windows の Player(DX12、Mono)をビルドし、可視のウィンドウ(640×480、vsync なし、`-nographics` なし)で動かす。
4 つの視点(接点 3・2・1、全体)で、選択が収束するのを待ち、120 frame の時間(`FrameTimingManager`)と、camera の描画(URP の StandardRequest、HUD なし)の画素を記録し、`PlayerReport.txt` を書いて終了する。
実行: `Tools/Run-PlanetRegression.ps1 -Jobs PlanetPlayer,PlanetPlayerRelease`(Development と非 Development)。結果は `TestResults/Planet/PlanetPlayer*`(`Report.txt` が合わせた結果、`PlayerReport.txt`、`Player.log`、各視点の PNG)。

Omni を入れたプロジェクトで Player をビルドするには、次が要る(どちらも Omni Manager から手で行う手順と同じで、無いと Omni の検査がビルドを止める)。`PlanetPlayerBuild` がビルドの前後で行う:

- `OmniRadialBuildPreparation.Prepare(scene)`: 対数版の標準 URP Shader を `Assets/Radial`(git 管理外)へ生成する
- URP の共有 resource の Camera MotionVectors を Omni 版へ接続する(`OmniUniversalRendererResourceInstaller.TryInstall`。internal なのでリフレクションで呼ぶ)。**ビルドの間だけ接続し、終わったら元の参照へ戻す**(プロジェクトの URP の設定を変えたままにしない)
- Graphics API を DX12 だけにし、`enableFrameTimingStats` を有効にする(これもビルドの間だけ)

2026-10-12 の結果(Mono、RTX 4060 Ti、DX12、640×480、vsync なし、`maxQueuedFrames` 2、各視点の 120 frame の平均。ビルド 0 エラー、Development 166 MiB・非 Development 101 MiB):

| 視点 | 描いた Cell(巨大/月/球/ボール、合計) | CPU frame(Development / 非 Development) | CPU の render thread | GPU(Development / 非 Development) |
|---|---|---|---|---|
| 接点 3 | 991 / 642 / 69 / 64(1,766) | 34.0 / 37.4 ms | 9.9 / 10.9 ms | 17.9 / 18.9 ms |
| 接点 2 | 952 / 734 / 50 / 6(1,742) | 34.2 / 32.4 ms | 10.0 / 9.4 ms | 17.2 / 18.2 ms |
| 接点 1 | 1,605 / 759 / 0 / 0(2,364) | 47.4 / 45.3 ms | 13.2 / 12.6 ms | 4.3 / 5.4 ms |
| 全体 | 1,019 / 51 / 6 / 6(1,082) | 21.1 / 19.6 ms | 6.3 / 5.9 ms | 11.3 / 11.4 ms |

- 画素: 接点の 3 視点で、水平より 2 画素以上下を向く光線の画素に背景は 0(穴なし)。全体の視点も全画素が背景ではない。Player のエラーログは 0(下の Omni の診断 1 件を除く)
- **CPU の frame 時間は、描いた Cell の数にほぼ比例する(1 Cell あたり約 19〜20 µs)。** 目安の CPU 2 ms を大きく超えるのは Editor と同じ。非 Development(Mono)でも Development とほとんど変わらないので、CPU の大半は管理コードではなく、Cell ごとの indirect draw と MaterialPropertyBlock の提出(エンジンの内部)と考えられる(仮説)。IL2CPP は試していない
- **GPU の frame 時間は、Cell の数に比例しない。** 接点 3・2 は約 17〜19 ms(1 Cell あたり約 10 µs)、全体は約 11 ms、接点 1 は draw が最も多いのに約 4〜5 ms(1 Cell あたり約 2 µs)。原因は未調査
  (仮説: 描いた Cell のうち、cull を生き残る cluster の数と画素の重なり。球とボールが写る視点で大きい)。frame の律速は CPU で、GPU は frame 時間の約 1/2(接点 3・2)〜1/10(接点 1)
- Omni の診断が 1 件、Player で `LogError` として出る: `[Omni][Scene BRG] No active Scene BRG manifest entry is available.`
  (`RadialSceneBatchRendererService.TryRebuild`。Renderer が 1 つも無い scene では、通常の Renderer を一括で描く経路に manifest が無いため。VG の描画とは無関係に見える)。smoke は件数を報告に書いて無視する。Omni への報告は未送信(宛先が未定)
- **確かめていないこと**: IL2CPP の Player、実際のキーボード入力・HUD の目視(Player は自動の視点だけ)、Editor の画像と Player の画像の画素単位の一致(Player は見た目の材質、Editor の検証は単色)、Player の長時間の動作、他の GPU

### pool の大きさ(`RealMoonValidation.Run -poolCapacity N`、2026-10-12)

月(半径 1,737.4 km、実データ、目標 2 px、高度 150 km → 0.17 m の 4 地点、選ぶ Cell は地面の近くで約 900〜1,400)で、cluster の pool(と描画 slot)の数 N を振った。
slot が足りないと、細分化に必要な Cell を確保できず(`starved`)、粗い Cell のまま描く。

| N | starved(要求 × frame) | 退避 | 地表 0.17 m の視点で最も近い Cell の quad | 結果 |
|---|---|---|---|---|
| 4096(既定) | 0 | 17,554 | 全 4 地点で 0.635 mm | PASSED |
| 2048 | 824 | 19,049 | 3 地点は 0.635 mm、1 地点は 1.27 mm(1 階層足りない) | FAILED |
| 1024 | 7,211 | 9,063 | 81〜650 mm | FAILED |
| 512 | 8,316 | 2,194 | 10〜41,642 mm(穴は月全体では 0。近くは粗いまま) | FAILED |

- 選ぶ Cell の約 3 倍が pool の目安(選んだ Cell に、その親・兄弟など、細分化の途中で使う Cell が加わる)。1 mm の細かさを地面で保つには、このシナリオで 4096 が要る(2048 は 1 階層足りない)
- **描画 slot(`maximumDrawSlots`)は pool と分けて小さくできる**(2026-10-12、`-maxDrawSlots N`。`MoonBallValidation` の月は pool 4096)。camera ごとの buffer は描画 slot の数に比例する(約 0.1 MiB/slot)。
  GPU memory(`Profiler.GetAllocatedMemoryForGraphicsDriver`、プロセス全体)は、描画 slot 4096 / 2048 / 1024 / 256 で 1,464 / 1,248 / 1,140 / 1,059 MiB(camera 1 台、月の source だけの違い)。
  この scene の月は、描画 slot 2048 と 1024 でも MoonBall が合格した(選ぶ Cell が約 800 以内)。**描画 slot が選ぶ Cell の数より少ないとあふれる**: 256 では、core が `LastError`(「描くslotが上限(256)を超え、541個を描けませんでした。」)を出し、高さ 0.17 m の視点で穴が 46,057 画素になって不合格(あふれは検出される)。
  描画 slot は、選ぶ Cell の最大(視点を動かす間のピーク)に余裕(目安 1.25 倍)を見て決める。既定(pool と同じ)は変えていない
- 目標を 2 px より粗くする(`targetPixels` を上げる)と、必要な Cell が減るので、pool を小さくできる。この測定は目標 2 px だけ

### 限界

- 深度の分解能(z-fighting が起きる相対距離)は §21 で測った(この節の demo の接点ではなく、半径 10^20 m の天体 2 つの重ねで)。この節の画像の一致が崩れる距離は見つからなかった(上の「距離を変える」)
- 月は起伏のある天体なので、「接点」は名目の点で、月が巨大な天体の面に突き刺さる所がある。接点 1 の近く(横 0.17m 以内)で、月の最下の頂点は接点の高さより 0.66mm 下にある(月の起伏の傾き約 4×10^-3)
- 4 つの天体を同時に描く Cell は合計 1,000〜2,400。視点を変える検証の間に、月の pool(4096)で退避が 10,688 回起きた(`starved` 0、設定エラーなし)。pool の大きさと Cell 数の関係は、上の「pool の大きさ」で測った
- 巨大な天体は高さの Field で 10^20 m だが、その地形は半径に比べて滑らかすぎ、地上の目線では平面と区別がつかない(§16)
- 実キーボードの Input System 経由の入力と、HUD の文字(IMGUI)は確かめていない(§16)


## 18. 実データの高さ(全球の格子と局所の Override)

### 方式

高さの Field に、緯度経度の格子(`PlanetRasterLayer`)の実データを足せる(`PlanetTerrainSource.SetRasters(global, regional)`)。手続きの項(§13)は、実データの帯域より上の細部に使う設定にする(実データがあるときの設定は呼び出し側が決める)。

- **全球の格子**: 北から南、経度 −180°〜+180°(画素の中心が格子点)の等距円筒。経度 = atan2(x, y)、緯度 = atan2(z, √(x²+y²))(Omni の PlanetSurface と同じ規約)。双線形補間
- **局所の Override**: 格子の中央の緯度経度と 1 度あたりの画素数を持つ格子。範囲の端から inset 画素の内側まで重み 0、そこから feather 画素かけて 1(smoothstep)。重み付きで全球の値と混ぜる(`h = lerp(全球, 局所, 重み)`)
- **頂点の位置は「Cell の原点の緯度経度 + 原点からの差」**。原点の画素の位置は CPU が倍精度で整数部と端数に分けて渡し、頂点ごとの緯度経度の差は、桁落ちしない式(`PlanetRasterLayer.Delta`、`PlanetRaster.hlsl` の `PlanetLonLatDelta`)で求めた単位方向の差 eps = sphere/R から求める。
  緯度経度を float の方向から求めると、半径 1.7×10^6 m で位置が約 0.1 m に量子化され、傾きの大きい所で高さに階段が出る
- **帯域制限**: quad の長さが画素より大きい Cell は、2×2 の平均で半分にしていく mip を使う(quad の長さ以上の最小の mip)。GPU は mip 付きの R32F texture を `Load` で読む
- CPU の地表の高さ(`TrySampleSurface`、降下カメラの地面の制限、月とボールの接点)は、実データを点自身の方向から倍精度で直接求める(Cell の原点からの差は float で、粗い Cell では 2cm ほど誤差が出るので)。
  Logical で返す版(`TrySampleSurface(in LogicalVector3, out LogicalScalar height, out LogicalScalar altitude)`)は、実データの倍精度の値に手続きの float の値を足して Logical にし、点の高さとの引き算も Logical で行う(float で返す版はその値を float にしたもので、|高さ| 9 km で刻み 1 mm)。
  mm の精度で物を地面に置くもの(`MoonBallDemo`、`BodyStackDemo`)は Logical 版を使う。降下カメラは目の高さの制限なので float 版のまま。Cell の頂点の CPU の式(`TryHeight`)は GPU と同じ式
- 極の真上で atan2(0, 0) が未定義(GPU が NaN を返し、NaN の高さが Cell の bounds に入って選択が細分化しなくなった)なので、`PlanetAtan2` が 0 を返す。生成した頂点に NaN・inf があるときは、その Cell の実際の bounds を使わず解析的な bounds のままにする(`PlanetLayerSource.InvalidBounds`)
- 差が小さいとき(x > 0 で |y| < 0.1x)の atan2 は、GPU の atan2 の相対誤差(1e-6 ほど)を避けるため、atan の級数で求める
- **極の近くの緯度経度の差**(`|o.z| ≥ 0.966`、緯度 75° 以上の原点。`PlanetRasterLayer.PolarDelta`、`PlanetRaster.hlsl` の `PlanetPolarDelta`):
  緯度を cos = √(1−z²) から求める式は極で桁落ちする(float の GPU では、`o.z` が 1.0 になる緯度 89.99° より先で緯度が −90° にずれて赤道の行を読む。緯度 89.9° の 1 km の Cell で、GPU と CPU の頂点の高さが 0.029 m、描画頂点と CPU の地表の高さが 1.26 mm 違った)ので、
  水平成分の大きさ ho = |o_h|、hn = |n_h| から求める。hn は n_h = o_h + eps_h の長さを成分の和から直接求める(ho² + d2 の平方根にすると、極に近い頂点で float が相殺して hn に ho の 3×10^-4 倍の誤差が出る)。原点が極そのもの(階層 0 の根)は、経度の差を頂点の経度にする。
  `|o.z| < 0.966` の原点は従来の式のままで、極以外の Cell の頂点はビット単位で変わらない(RealMoon の頂点ハッシュ 17 件が基準と一致)
- **GPU の atan2**: 極の近くの経度・緯度の差は、標準の `atan2` でなく `PlanetAtan2Accurate`(|a| ≤ tan(π/8) に折り返した級数、float の丸めに近い精度)で求める。
  標準の atan2 に由来すると考えられる誤差で、極冠(下)の列の位置が 1e-3 画素ずれ、極から 100 m の頂点で高さが 0.6〜1.3 mm 違った。差し替えで 35 µm 以下になった(合成の滑らかな場、緯度 85° 以上)
- **極冠**(`PlanetRasterLayer.PoleCap`。GPU は `PlanetRasterLayerSample` と `_RasterPole`): 全球の格子は画素の中心が格子点なので、極は先頭の行の半画素北(行の位置 −0.5)、最後の行の半画素南(行の位置 h−0.5)にある。
  先頭の行より北を先頭の行へ clamp すると、極の近くの高さが経度で決まり(先頭の行は列ごとにばらつく)、経度の定まらない極の 1 mm の範囲でも高さが経度で何 m も変わる。
  そこで極から半画素の中は、極の値 P(mip ごとの先頭・最後の行の平均。`PoleNorth(mip)`、`PoleSouth(mip)`)から、先頭(最後)の行の補間した値 ring へ、極からの距離に比例して移る: 値 = P + (ring − P)·t、t = 極からの距離/半画素。
  経度によらず P に収束して連続になり、場が局所的に平面なら極冠の中でも平面を正確に再現する。CPU の `Sample`、`SampleDirection`、GPU が同じ式。経度が一周する格子(全球)だけに働き、局所の格子には働かない
- データは Omni のデモ(`Assets/Omni/Demos/PlanetSurface/Data`)のコピーを `LocalData/OmniDemo/`(git 管理外)に置く。Omni の LICENSE と「外部資産は配布しない」の規則のため、commit しない。無いときは検証が、コピーする場所を示して失敗する

### 確認(`RealMoonValidation.Run`、2026-10-11、LOLA 5760×2880 と SLDEM2015 の 344×344(0N/0E、512 画素/度)で月を描く)

- 格子の範囲: LOLA の最小 −8,982 m(経度 −172.5°、緯度 −70.3°)、最大 +10,686 m(経度 −158.7°、緯度 +5.4°)。1 画素 1,895 m、mip 7 段。SLDEM は 1 画素 59.2 m
- CPU の地表の高さと、方向から直接求めた双線形補間(全球 + 局所の重み付き)の差: 3,000 点(局所の範囲の中 1,000 点を含む)で最大 0.24 mm(局所 0.03 mm)
- GPU が生成した頂点と、CPU の式の差(camera に近い 12 Cell、4 か所: SLDEM の中心、静かの海の周り、ティコの周り、南極エイトケン盆地の近く):
  階層 14〜26 で最大 0.18 mm(SLDEM の中心。高さ約 −720 m で原点を持ち上げない Cell なので、float の刻み 61 µm の数倍)、原点を持ち上げた他の 3 か所(高さ −1.9〜−7.3 km)は最大 43 µm(§19)。
  粗い Cell(階層 2、一辺約 680 km)は 3.4 m(Cell の大きさの 5×10^-6。原点からの差の float の丸めが位置に約 1 m の誤差を作る。許す差は階層 13 以下で 2 mm + Cell の大きさの 1.5×10^-5、階層 14 以上で 0.3 mm + Cell の大きさの 10^-6)
- 1 quad が約 1 mm の Cell(地表 0.17m の視点の真下の Cell)の頂点の高さの 2 階差分: 最大 0.061 mm(SLDEM の中心。他の 3 か所は 0.003〜0.036 mm)で、方向の量子化の階段(傾き 0.1 で 1e-2 m 以上)は無い。許すのは 0.2 mm
- 月全体の画像(高さ 2.5R)で穴 0。上空 150 km から斜めに見て、クレーターの地形が出る(`TestResults/Planet/RealMoon` の画像で確認)
- 世界パッケージのテスト 31 本(`PlanetRasterLayerTests`、`PlanetCellFrameTests.TryWithHeightShift`、`PlanetCellSelectorTests.Frame` を含む)合格。テストは、格子の座標の規約、緯度経度の差(極の近く `DeltaNearPole`、極そのもの `DeltaAtPole` を含む)、Cell の原点 + 差の補間と方向からの直接の補間の一致(粗い Cell から 1mm まで)、1mm 間隔の点列の高さの差(傾きの大きい格子)、mip の平均、局所の重み、極冠(`PoleCap`。極の値への収束、clamp では外れる平面の再現、mip が上がった Cell の極の値、shift)を見る

### 確認(極の近く、`RealMoonValidation.RunPolarSynthetic` と `RunPolar`、2026-10-12)

経度 0 と −172°、緯度 ±{80, 85, 89, 89.9, 89.969, 90}°(24 地点)、高度 1000 / 10 / 0.17 m で、camera に近い 12 Cell の頂点を GPU と CPU で比べる(極の近くの Cell を除外しない)。
非有限の頂点を持つ Cell(`InvalidBounds`)は 0。結果は `TestResults/Planet/SyntheticPolar`、`RealMoonPolar`。

- **合成の滑らかな場**(`RunPolarSynthetic`、極で連続): 修正の前後で、極(±90°)の階層 14 の Cell の GPU と CPU の頂点の差は、緯度の式だけ直した段階で 3 mm、極冠を足して 0.6〜1.3 mm、`PlanetAtan2Accurate` で 35 µm 以下(許す差は 0.47 mm)。
  緯度 85° 以上の全地点で GPU と CPU の差は 35 µm 以下、描画頂点と CPU の地表の高さの差は 19 µm 以下、1 mm の 2 階差分は 0.033 mm 以下、地表 0.17 m の視点で最も近い Cell の quad は全 24 地点で 0.635 mm。
  緯度 80° の北の地点は、原点を持ち上げない Cell(高さ −621 m)で、GPU と CPU の差 0.19 mm、2 階差分 0.186 mm(float の刻み 61 µm の数倍)。pool は 4,096 枠を使い切るが、starved は 0。PASSED
- **実データ(LOLA)**(`RunPolar`): 極の真上の GPU と CPU の頂点の高さの差(階層 14 以上)は、緯度の式だけ直した段階で北 109 m(高度 10 m)・南 715 m(高度 1000 m)、地表との差 29 m だったのが、北 0.37 mm、南 0.92 mm、描画頂点と CPU の地表の高さの差が北 0.11 mm、南 0.41 mm になった。
  許す差は階層 14 以上の GPU と CPU が 2 mm、地表が 1 mm(合成の滑らかな場より緩い。理由は下の限界)。PASSED。北は全地点で quad 0.0794〜0.635 mm まで細分化される

### 実データの帯域より上の細部の増幅(`PlanetTerrainSource.AmplifyAmplitude`、`AmplificationValidation`、2026-10-12)

実データ(格子)の画素より細かい波長の起伏は、データに無い。そこへ、シード固定の gradient noise(`PlanetOctaveNoise`、channel 3)を、波長が半分になるごとに振幅を 2^−H 倍にして足す(フラクタル)。

- 遮断波長 λc = 実データの 1/2 画素(`CutoffTexels` = 0.5。全球の LOLA は 1 画素 1,895 m なので約 0.95 km。下の検証の合成の格子は 1 画素 3.79 km で 1.9 km)。局所の格子があれば、重み(局所の格子の重み)で全球と局所の log2 λc を混ぜる(SLDEM の中では細かい波長まで増幅しない)
- octave d の波長 λd として x = log2(λc/λd) > 0 の octave を、重み saturate(x)(1 octave かけて入る)、振幅 A·2^(−H·x)で足す。A は遮断波長での octave の振幅(m)、H は Hurst 指数(既定 0.87。計画時の調査で LOLA から得た値で、ここでは測り直していない)。`AmplifyAmplitude` = 0 なら無効(既定)
- 帯域制限は手続きの項と同じ: 波長が 2·quadLength より短い octave は足さない。そのため quad が λc/2 以上の Cell(月で、LOLA の全球の格子なら階層 ≤ 6、下の検証の合成の格子なら階層 ≤ 5)には入らず、頂点は増幅の有無でビット単位で同じ
- 式は GPU の `PlanetHeight.hlsl` の `PlanetAmplifiedHeight` と C# の `PlanetHeightField.Amplified` で同じ。選択の bounds には、上界 1.5·A/(1−2^−H)(A = 40 m、H = 0.87 で 132.5 m)を足す。`TryAmplifiedHeight` が点ごとの増幅の項だけを返す
- 遮断波長を長くするほど、1 画素ごとの平均が 0 から離れる(gradient noise は低い周波数にも広がる): 2 画素で全体の 0.70 倍、1 画素で 0.50 倍、1/2 画素で 0.34 倍、1/4 画素で 0.26 倍。「データの 1 セルごとに平均 0」(§2)に近づけるため 1/2 画素にした(それより先は効果が小さい)。厳密な 0 は保証していない

確認(合成の高起伏の全球の格子、画素 3.79 km、A = 40 m、H = 0.87、月、RTX 4060 Ti、batchmode):

- 粗い Cell(階層 ≤ 5、月全体の視点の 56 Cell): 増幅の有無で頂点がビット単位で同じ(違い 0)
- 細かい Cell(階層 ≥ 14、地表 300 m の視点): 共通の 79 Cell すべてで頂点が変わる(最大 22.5 m)
- GPU の頂点と CPU の式(増幅あり)の差: 地表 1000 m で 0.12 mm、10 m と 0.17 m で 61 µm(許す差は階層 14 以上で 0.3 mm + Cell の大きさの 10^-6)。CPU の地表の高さ(増幅を含む `TrySampleSurface`)と描画の頂点の高さの差 124 µm(許す差 200 µm)
- 増幅の項の系列(赤道に沿って 4,096 点、画素の 1/16 刻み): 平均 −0.002 m、二乗平均の平方根 6.8 m。1 画素幅の箱形で平滑化した二乗平均の平方根は元の 0.34 倍(許す上限 0.35)、1 画素ごとの平均の二乗平均の平方根は元の 0.34 倍(許す上限 0.45)
- EditMode(`PlanetHeightFieldTests`): 振幅 0 で 0、遮断波長が最も細かい octave より短いと 0、quad が遮断波長の半分以上の Cell で 0、振幅に線形、平均が 0 に近い、Hurst 指数が大きいほど小さい、遮断波長を連続に振ると連続
- 他の検証(RealMoon の頂点ハッシュ 17 件が基準と一致、SyntheticRelief、P5a)は増幅 0 で変化なし

限界: 振幅 A は手で決める値で、実データの粗さから決める方法は未実施。見た目(模様・陰影)は、検証の画像が暗く(照明が地面に当たらず)評価していない。
実データ(LOLA + SLDEM)の増幅の検証は、合成の格子だけで確かめた(局所の格子の重みによる遮断波長の混合は、式と CPU・GPU の一致は同じだが、SLDEM の中の動きは確かめていない)

### 色(アルベド)の格子(`PlanetColorLayer`、`ColorLayerValidation`、2026-10-12)

全球の色の texture(北が上・経度 −180°〜+180°・画素の中心が格子点の等距円筒。sRGB、mip 付き)を、`PlanetTerrainSource.ColorLayer` に設定すると、`RadialMoonGrid.shader` が地面の色(`_BaseColor`)の代わりに texture の色 × `_ColorGain` を使う。
近くで色を 4,096 画素の画像から求めると、色の位置も同じ量子化(約 0.1 m)の問題が出るので、高さと同じ「Cell の原点 + 差」の方式にした。

- CPU(`PlanetColorLayer.TryCell`): Cell の原点の方向を倍精度で正規化し、画素の位置を整数部と端数に分けて、Cell ごとの値(`_PlanetColorDirection`、`_PlanetColorIndex`、`_PlanetColorFraction`)として draw の MaterialPropertyBlock に書く(slot ごとに覚える)
- GPU(fragment): 画素の方向 n と原点の方向 o の差 eps = n − o を、桁落ちしない式で求める。n = (o·(R+H) + p) の方向(p は Cell の原点からの相対位置)として、
  eps = (p + o·c)/|w|、c = −(2(R+H)(o·p) + |p|²)/(|w| + R+H)、w = o·(R+H) + p。緯度経度の差は、高さの格子と同じ `PlanetLonLatDelta`(`PlanetLonLat.hlsl` へ切り出して共有。極の近くは `PolarDelta`)。
  texture の位置 = (原点の画素の整数部 + 端数 + 差 × 1 ラジアンあたりの画素数 + 0.5)/大きさ。mip は、画素の大きさ(m)と texture の 1 画素の大きさ(m)の比の log2 で `SAMPLE_TEXTURE2D_LOD` する
- `_ColorOnly` = 1 は、照明も格子もなしで、その色(線形)を出す検証用

確認(月、合成の高起伏、RTX 4060 Ti、batchmode。色は線形の 0〜1、誤差は 255 を掛けた値):

- 地上(高度 2 m、真下): 画像の中央 5×5 画素の平均が、真下の方向の CPU の双線形補間の色に、全 9 地点で 0.7/255 以内で一致。地点は、経度 0・90°・45°・180° の継ぎ目・±179.9° の継ぎ目の両側、緯度 ±89.99° の極の近く、−75°。合成の texture(1024×512、画素 10.7 km)と LROC(4096×2048、画素 2.7 km)の両方
- 月全体の視点(高度 2.5R): 中央は合成の texture で 2.1/255 以内、離れた 4 画素(光線と球の交点の方向)も 1.7/255 以内。LROC は mip(1 画素 7.6 km、texel 2.7 km)で引くので、CPU の mip 0 の補間とは中央で最大 11/255、離れた画素で最大 18/255 違った(許す差 30/255)
- 画像: LROC が月に貼られて、海と高地が見える(`TestResults/Planet/ColorLayer/lroc_orbit_*.png`)
- 緯度経度の差の式を `PlanetLonLat.hlsl` へ切り出した後も、RealMoon の頂点ハッシュ 17 件が基準と一致、SyntheticPolar、Amplification、P5a、MoonBall は PASSED
- データ: Omni のデモのテクスチャ(`Assets/Omni/Demos/PlanetSurface/Textures/LrocGlobal4k.png`)を `LocalData/OmniDemo/lroc-global-4096x2048.png`(git 管理外。Omni の LICENSE により配布・commit しない)にコピーして使う。無い環境では合成の texture だけを確かめる

限界: 色は全球の texture 1 枚で、局所の色の Override は無い。近景の質感(texture の画素 2.7 km より細かい模様)は無い(増幅は高さだけ)。mip は等方(異方性フィルタ無し)。
極の画素は、高さの格子の極冠のような処理をせず、texture の端の画素が続く(極で texture の端の行が全経度に広がる)。月のデモの scene と、`_ColorGain`(既定 1)の決め方は未実施

### 限界

- **実データ(LOLA)の極の行は列ごとにばらつく**(行 0: 北 −80〜+65 m、南 +342〜+1,431 m。隣の列と北で 27 m、南で 306 m 違う。極冠の外周 947 m の 1 列は約 1 m)。極冠はこのばらつきを外周に残すので、南極の近くに崖がある。
  (a) 勾配が大きく、float の位置の丸め(約 0.1 mm)が高さの 0.3〜0.9 mm の違いになる(許す差を緩めた理由)。
  (b) 南極の真上と緯度 −89.969° 付近は、細分化が 1 mm まで収束せず(pool が尽きる。used 4096/4096、starved 約 6 万)、地表 0.17 m の視点でも最も近い Cell の quad が 20 mm〜1.3 m で止まる(緯度 −89.9° は 0.08〜0.3 mm まで届く)。
  (c) そのため、実データの極の近くでは「最も近い Cell が 1 mm」と 1 mm の 2 階差分を合否に使わない(合成の滑らかな場では使う)。
  崖は元データの特徴で、極冠の式の問題ではない。対処は元データの前処理(極の近くの行を、経度方向の分解能に合わせて低域通過する等)で、Field の供給元の仕事として未実施
- 既定の `Run`、`RunSouth` の GPU/CPU の照合は、粗い Cell のうち |z| > 0.966 の Cell を除外したまま(頂点ハッシュの基準を保つため)。極は `RunPolar`、`RunPolarSynthetic` が全 Cell で比べる
- 頂点の位置の float の刻みは、Cell の原点の持ち上げ(§19)で解消した。残る限界は §19
- 実データの帯域より上の増幅は、上の「実データの帯域より上の細部の増幅」で実装・検証した。実データの検証(RealMoon)は増幅 0 のまま(頂点ハッシュ 17 件が基準と一致)。増幅の振幅 A を実データの粗さから決める方法と、1 画素ごとの平均を厳密に 0 にすることは未実施
- 「Field の供給元の interface」(格子以外の供給元)は作っていない。いまは全球 + 局所の 2 枚の格子だけ
- 色(LROC のアルベド)は、下の「色(アルベド)の格子」で、高さと同じ「原点 + 差」の方式で実装・検証した。月のデモの scene(MoonAndBall、BodyStack など)には組み込んでいない(`PlanetTerrainSource.ColorLayer` を設定すれば使える)
- 視点の高さの降下(高度 150 km → 0.17 m)で、pool(4096)の退避が 17,554 回起きた(`starved` 0、設定エラーなし)

## 19. Cell の原点の持ち上げ(起伏の大きな天体の頂点の精度)

### 方式

頂点の高さ方向の成分が地形の高さ h そのものだと、float の刻みは |h| が 4 km で 0.5 mm、9 km で 1 mm になり、実データの月(高さ −9.0 km〜+10.7 km)で `1mm の精度` を超えた。
Cell の原点を、Cell の中心の方向の地表の高さ H だけ半径 R の球面から持ち上げ、頂点を (h − H) で持つ。Cell の中の起伏だけが残るので、高さ方向の成分は Cell の大きさと勾配のぶんになる。

- `PlanetCellFrame`:
  - `Origin` = `SphereOrigin` + `HeightShift`·`Direction`(Logical。単位方向 × H で作り、`Origin·(H/R)` の比は使わない。R=10^20 m でも固定小数点の絶対精度 2^-96 のまま足せる)
  - `SphereOrigin`: 高さ 0 の球面上の点(持ち上げる前の値)
  - `HeightShift`: float の H。GPU にも同じ値を渡す(float は Logical に正確に変換でき、CPU と GPU で同じ値になる)。持ち上げない Cell は 0 で、`Origin == SphereOrigin`
- **`SphereOrigin` から求めるもの**(高さ 0 の球面で定義される値): ノイズの格子の基点(`TryNoiseLattice`)、波の位相(`TryWavePhase`)、実データの方向(`PlanetRasterLayer.TryCell`)、`TrySampleSurface` の点との差。
  持ち上げた原点から求めると、持ち上げ量が違う隣の Cell と高さ場が食い違い、継ぎ目が割れる(テスト `PlanetCellFrameTests.TryWithHeightShift` が、持ち上げの有無で格子・位相が同じことを見る)
- **`Origin` から求めるもの**(頂点の基準): カメラとの差(`FromCamera`)、bounds の中心、`IPlanetPlacement.Describe`(描画行列、Omni の物体の位置)、`_PlanetCellRef`、頂点を読み戻して絶対位置を作る検証
- **H の決め方**: `PlanetTerrainSource.TryCellHeightShift` が Cell の中心の頂点の高さ(quad の長さで帯域制限した高さ。手続き + 実データ)を評価し、|高さ| ≥ `HeightShiftThreshold`(既定 1,024 m)のとき H = その高さ。float のまま使う(量子化しない)。
  有限でない、|H| ≥ R は 0。H は Selector の frame が最初に作られるとき(Cell の割り当て、または `TrySampleSurface` などの問い合わせ)に決めて覚える。`TryCellHeightShift` は同じ Cell に同じ値を返す前提。
  `PlanetLayerSource` は生成済みの Cell には slot の frame の値を返すので、Selector の frame の cache を捨てても、選択の frame と slot の frame は常に同じ原点になる。海の Cell は 0
- **頂点**: position = sphere + sphere·(H/R) + direction·(h − H)(`PlanetCellGeometry.VertexPosition`、GPU は `PlanetCellVertex`)。sphere は `SphereOrigin` からの球面上の相対位置、direction は頂点の単位方向。
  (R+h)n − (R+H)u = (R+H)(n−u) + (h−H)n から導いた恒等式で、H によらず P = Origin + position = (R+h)·n
- **h − H は丸めの前に引く**: 実データは texel の段階で引き(`PlanetRaster.hlsl`。補間した後で引くと、補間結果の float の丸めが高さの桁で残る)、実データが無いときは `−H` を返す。手続きの項は `clamp(total)·A` に足す。CPU は `PlanetRasterLayer.Sample(…, shift, …)` が、GPU と同じく texel から先に(倍精度で)引いてから補間し、float にする
- 選択の bounds: `FrameBounds` の offset は direction·(−skirt/2 − H)。`SampledBounds` は `ApproximateOrigin` との差。読み戻した bounds は頂点の基準(持ち上げた原点)のまま
- `_PlanetCellUp.w` = R + H(`RadialBall` は dir = normalize(up·w + position) で使う)
- 海(`PlanetOceanSource`)は H = 0。水深は絶対の高さで、`PlanetOceanGenerate.compute` は `_HeightShift` = 0 の Cell で `PlanetHeight` を呼ぶ
- 検証のコマンドライン: `-heightShiftThreshold <m>`(`HeightShiftOverride`)。0 は高さが 0 でない全ての Cell を持ち上げ(持ち上げの読み手の直し忘れを、振幅の小さい検証でも H≠0 で通して見つける)、1e9 は持ち上げない(持ち上げ前の挙動の再現)

### 確認(2026-10-11)

- 世界パッケージのテスト 27 本合格(この時点。極の近くの確認を足した現在は §18 の 31 本)。
- `RealMoonValidation.Run`、持ち上げなし(`-heightShiftThreshold 1e9`): 読み戻した全頂点(位置・法線)の FNV ハッシュ 17 件が、持ち上げを入れる前(基準)とビット単位で同じ。evictions も同じ 17,554 回。GPU・CPU・shader の変更が、H = 0 のとき挙動を変えない
- 同、持ち上げあり(既定):
  - GPU と CPU の頂点の差(階層 14〜26、camera に近い 12 Cell): −7.3 km の地点 1.64 mm → 2.3 µm(地表 0.17 m)、−2.9 km の地点 0.55 mm → 14 µm、−1.9 km の地点 0.41 mm → 1.5 µm。上空 1 km の視点でも最大 43 µm
  - 1 quad が約 1 mm の Cell の頂点の高さの 2 階差分: −7.3 km で 1.46 mm → 0.0068 mm、−2.8 km で 0.98 mm → 0.036 mm、−1.9 km で 0.37 mm → 0.003 mm
  - 高さ約 −720 m の SLDEM の中心は H = 0(しきい値未満)のまま不変(0.061 mm は float の刻み 61 µm)
- CPU の地表の高さ(`TrySampleSurface` の Logical 版)と、描いている頂点(持ち上げた Cell の原点 + 相対位置)の高さ(|原点 + 相対位置| − R)の差(`RealMoonValidation`、地表 0.17 m の視点の真下の Cell、81 点):
  持ち上げあり 1.4〜15 µm(高さ −1.9〜−7.3 km の 3 地点。持ち上げない SLDEM の中心は float の刻み 61 µm の丸めで 51 µm)、持ち上げなし(`-heightShiftThreshold 1e9`)は 0.16〜0.55 mm。許すのは 0.1 mm。
  CPU の高さと方向から直接求めた双線形補間の差は 3.5×10^-10 m(float で返していたときは 0.24 mm)
- 合成の高起伏(`RealMoonValidation.RunSynthetic`、データ無しで走る。方向の滑らかな関数 `sin(60·n.x+0.7)·cos(60·n.y+0.3)+0.5·sin(37·n.z+1.1)` を −7,300 m〜+9,000 m に線形に写した 2880×1440 の全球の格子、勾配は最大約 0.3。場所は格子の最大・最小の位置、経度・緯度 0、最大の近く):
  持ち上げあり(既定)は PASSED。最大 +9,000 m の地点で GPU と CPU の頂点の差 1.65 µm(地表 0.17 m)、CPU の地表の高さと描画頂点の高さの差 1.6 µm、1 mm の Cell の 2 階差分 0.011 mm。最小 −7,300 m の地点で 3.0 µm、3.6 µm、0.006 mm。持ち上げ量の評価 22,858 回、1 回 18.7 µs。
  持ち上げなし(`-heightShiftThreshold 1e9`)は、締めた許容値で FAILED(期待どおり)
- 頂点ハッシュの基準の自動比較(`RealMoonValidation`): 記録(17 件)→ 再実行で比較して 17 件一致(実行ごとに同じ値)→ 1 件を書き換えると、その名前を指して FAILED → 設定(`-heightShiftThreshold 1e9`)を変えると「環境違いのため未比較」
- 検証の許容値を実測の 2〜10 倍へ締めた(`CheckGpu` は階層 14 以上で 0.3 mm + Cell の大きさの 10^-6、`CheckMillimeter` は 2 階差分 0.2 mm)。持ち上げなしの実行は、この許容値で失敗する(修正前の挙動で不合格、修正後に合格)
- 回帰: `P2`、`P2Large`、`P4b`、`P4bEarth`、`P5a`、`MoonBall`、`GiantBall`、`BodyStack`、`BodyStackCells` を、既定と `-heightShiftThreshold 0`(全 Cell を持ち上げる)の両方で実行し、すべて PASSED。
  既定では、振幅が 1,024 m 未満の検証は H = 0 のままで、`P2`・`P5a`・`MoonBall` の報告は持ち上げ前と同じ。
  振幅 3,000 m の `P4b`、`P4bEarth`、`P2Large` は一部の Cell で H ≠ 0 になり、`P4b` の `maxErrorPerAllowed` が 0.262 → 0.147(`P4bEarth` は 0.548 → 0.46)に下がり、`P2Large` は Standard との色の差の画素数が数 % 動いた(輪郭の差は 0 のまま)。
  全 Cell を持ち上げた実行では、画素数・接点の隙間などが ±1 画素・数 µm 動く(float の丸めの違い)が、合否の基準は全て満たす

### 限界

- **手続きだけの大振幅の項** `clamp(total)·A` の float の丸めは、H を引いても残る(A = 8 km で 0.25〜0.5 mm)。実データの月のように、振幅を実データに任せる使い方では出ない
- 粗い Cell(階層 2 で 3.4 m)の GPU・CPU の差(原点からの差 eps の float の丸め)、float の `_CenterTangent` は別の原因で残る(極の近くは §18 の極の近くの確認と限界)
- 持ち上げ量の評価の時間(`RealMoonValidation` の report の `height shift:` の行): 高度 150 km から 0.17 m までの降下 4 か所で、評価 23,967 回(持ち上げた Cell は 17,782 回)、累計 330 ms、1 回 13.7 µs(Editor、batchmode)。
  `BodyStack` の timing は、持ち上げ前後の差を比べていない(振幅が小さく H = 0 のままの検証で、機械の負荷により ±数十 % 揺れた)
- `TryCellHeightShift` の評価結果は Cell ごとに frame に入れて覚える(Selector の frame の cache は 65,536 件で捨てるが、生成済みの Cell は slot の frame の値を返すので変わらない)。`HeightShiftThreshold` を有効なまま変えると作り直す
- 実データ(`LocalData/OmniDemo`、git 管理外)の検証は、データが無い環境では走らない。データ無しで走る合成の高起伏の検証は `RealMoonValidation.RunSynthetic`(下の確認)
- 頂点の FNV ハッシュ(`RealMoonValidation` の report の `vertexHash=`)は、`VertexHashBaseline`(`-vertexHashBaseline <file>`、`-vertexHashMode record|compare|auto`)が基準のファイルと自動で比べる。ハッシュは GPU とドライバーで変わるので、ファイルの先頭の環境(GPU 名、API、Unity の版、持ち上げの設定、データ、既定でない pool の大きさ)が違えば比べず「環境違いのため未比較」と report に書く(失敗にはしない)。基準のファイルは環境ごとの値なので、リポジトリには置かない(`TestResults/` は git 管理外)
- `RadialBall` の `_PlanetCellUp.w = R + H` は、起伏のあるボールで H ≠ 0 を通した(§16 の `RunReliefBall`。H を無視すると不合格になる)。月の頂点と描画行列・`_PlanetCellRef` も H ≠ 0 で通した(`MoonBall`、`BodyStack` を `-heightShiftThreshold 0` で実行、`RealMoonBall`)

## 20. 単純に拡大した面と VG の面に、ボールを沈める比べ(PlaneSink)

Omni の COSMIC-PRECISION-001(`D:\Unity\6.6\Omni\Docs\CurrentWork.md`。単位球を拡大した月の面が、物体空間の float の分解能で量子化され、ボールの手前に出た)に当たる、分割しない場合の比較(負の対照)。
左は単位の平面を巨大に拡大した 1 つの物体、右は VG の面で、同じボール(`PlanetTerrainSource`、半径 0.11 m)を三人称の camera で 1 mm ずつ沈める。
scene は `Assets/VirtualizedGeometrySample/PlaneSink/PlaneSink.unity`、操作は同じフォルダーの README.md。

### 構成(`PlaneSinkLayout`)

Logical の原点を継ぎ目 x=0 に置く。

| 量 | 値 |
|---|---|
| 左(単純な拡大) | 単位の XZ 平面(物体空間で y=0 ちょうど、座標 ±0.5)を一様に S=2(D+3) 倍した 1 つの物体。高さ 1 m、回転なし、右端 x=0。試験点は x=−3。D は面の原点から試験点までの距離(既定 10^6 m) |
| 右(VG) | `PlanetTerrainSource`、半径 R=2×10^6 m、高さ 0。中心 (3, −R, 0)、試験点は上端 (3, 0, 0) |
| 段 | 左は右より 1 m 高い。右の球面は左の面の下のどこでも 1 m 以上低い(画像に z-fighting は出なかった) |
| 壁 | x=0、高さ 0〜1 m の面(右の低い面から段が見える)。世界の座標のまま z 方向に細かく分けた mesh(238 頂点)。検証では隠す |

- 左の面の mesh は、試験点のまわり ±32 m を 0.5 m 刻み、外へ 1.25 倍ずつ広げて端 ±S/2 までの格子(約 3.2 万頂点)。座標は「原点からの距離 / S」を float にした値で、試験点の付近(座標 ≈ 0.5)の float の刻みは S·2^-25(D=10^6 m で約 6 cm)。
  壁を細分しない 1 枚の四角形(幅 2,000 m)で作ったときは、近い側が描かれなかった(画像で観測。原因は未確認で、Radial の写像が頂点ごとであることによる推測)ので、壁も細分した。左の面を 1 枚の四角形にした場合の比較はしていない
- 左の shader `RadialScaledPlane`(素の MeshRenderer 用)は、`RadialMoonGrid` と同じ格子(1 m、10 cm、1 cm)を物体空間の float のまま描く。右の格子は `_PlanetCellRef`(Logical)から描く(右の格子の基準点は惑星のローカル座標の上端 (0, R, 0))
- 左の material は実行時の複製に値を書く(scene の共有 asset を書き換えない)
- ボールの位置は Logical のまま計算する(試験点 + (x, r + y, z)、0.1 mm 単位の整数)。camera は Logical の周回(yaw、pitch、距離)で、ボールの中心を常に向く(pitch の下限は camera が面から 5 cm より下へ行かないように決める)
- キー: ↑↓ 1 mm 浮かせる/沈める(Shift で 10 mm、Ctrl で 0.1 mm)、WASD 水平、←→ PageUp/PageDown Home/End ホイール 右ドラッグ 周回と距離、1/2/3 左/右/全体、[ ] 左の面の D を {10^4, 10^5, 10^6, 10^7} で切り替え、Space 自動で沈める、R 初期化(領域と D は変えない)
- 物理は使わない。Omni は安全域 10^4 m を超える物体の Collider を無効にする(`RenderProxyRegistry`。コードを読んだ範囲で、動作は確かめていない)ので、ボールは Logical の計算で動かす

### 確認(`PlaneSinkValidation`、2026-10-11、640×480、DX12、RTX 4060 Ti、batchmode)

単色の画像(ボール赤、左の面 青、右の面 緑、背景 マゼンタ)を、Logical の位置から倍精度の光線で求めた球と面と比べた。
各 frame で、撮影と同じ解像度(640×480)で選択が収束してから撮る。camera はボールに付いて動く。
数える画素は、描画と解析で種類が違い、どの種類の領域の境から 1 画素以内にも入らないもの(`BodyStackValidation` と同じ 1 画素の帯。1 つの違う画素を 1 回だけ数える)。1 画素は、ボールまで 0.35 m で約 0.7 mm。

**合否**:
- 右(VG): 全 frame(63+21+31+21=136)で、違う画素 0、どの種類でもない画素 0、解析上のボールと右の面の画素が 1,000 以上(空振りで通らない)、ボールの画素数の解析との差が 40 以下、中央の列の最下のボールの行の差が 1 以下。
  ボールが全部見える frame(沈めの +2 mm、+1 mm、0 mm)で、ボールの重心が画像の中央から 3 画素以内
- 左(D=10^6 m): 沈めで、違う画素が最大 100 以上、ボールの画素数が逆戻り(2 画素超の増加)1 回以上。D の掃引で、D=10^4 は 100 以下、D が大きいほど減らず、D=10^7 は D=10^6 の 5 倍以上

| 段(各側) | frame | 右(VG) | 左(D=10^6 m、刻み 59.6 mm) |
|---|---|---|---|
| 沈め +2 mm → −60 mm(1 mm 刻み) | 63 | 違う画素 0 | 最大 1,545 画素、ボールの画素数の逆戻り 21 回 |
| 水平 0〜20 mm(−30 mm 沈めたまま) | 21 | 0 | 最大 279 |
| 周回 yaw 0〜90°(3° 刻み) | 31 | 0 | 最大 4,483 |
| 距離 0.350〜0.370 m | 21 | 0 | 最大 866 |

- 右は全 frame で、違う画素 0、どの種類でもない画素 0、設定エラーなし。ボールの画素数の解析との差は最大 8 画素。ボールの重心は (320.0, 240.0)(画像の中央)
- 沈めの +2 mm〜約 −26 mm の frame は、ボールが面と交わらないか、交わりが小さく、解析の画像が左右とも似ている(合否の力が小さい frame も含む)。−30 mm 以降が交わりを見る frame
- 左の面の D を変える(沈め 2 mm 刻みで +2 → −40 mm、水平 2 mm 刻みで 0〜20 mm。違う画素の最大):

| D(m) | 物体空間の float の刻み | 沈め | 水平 |
|---|---|---|---|
| 10^4 | 0.6 mm | 0 | 0 |
| 10^5 | 6 mm | 0 | 0 |
| 10^6 | 6 cm | 441 | 279 |
| 10^7 | 0.6 m | 41,387 | 30,814 |

  D=10^7 では、平面が球を貫いてちらつく(`Left_D1E+07-sink_-30.png`)。D=10^6 では、交わりの縁が 1〜数画素ずつ崩れる(`Left_sink_-30.png`)。
- 見た目(`Shaded*.png`、目視): 左の面の格子は崩れて途切れ、右の面の格子は全域でまっすぐに見える(右の格子の位置の精度は数値では測っていない)
- 素の MeshRenderer(`Omni.Logical.Transform` で拡大、Radial 対応の材質)は、Omni が自動で集めて Radial の物体ごとの経路で描いた(spike で確認)。Collider は付けていない

Play Mode(`PlaneSinkPlaySmoke`、batchmode): 初期化は 1 frame、キー 27 項目(↑↓ の量 ±1 mm、Shift 10 mm、Ctrl 0.1 mm、Shift+Ctrl で Ctrl 優先、WASD、周回・距離、右ドラッグ、ホイール、1/2/3、Space、R、[ ])が全て期待どおりで、エラーなし。
camera がボールの中心を向くことは、demo 自身の状態からの計算で確かめた(描かれた画像でのボールの重心は `PlaneSinkValidation`)。

EditMode テスト(`VirtualGeometry.Omni.Tests.Editor`): `ScaledPlaneMesh`(座標の列の端・昇順・focus 付近の刻み・不正な入力の例外、平面と壁の mesh の巻き順・法線・端、壁が細分されていること)と `PlaneSinkLayout`(配置の式、D を変えても右端が継ぎ目、float の刻みが D に比例)。

実行: `Tools/Run-PlanetRegression.ps1`(`-Jobs PlaneSink,PlaneSinkPlay -Modes on -Tests`)。`PlaneSinkValidation.RunSpike` は少ない frame で素の MeshRenderer が描かれることと D への依存を見るだけで、`PASSED` とは書かず `SPIKE-DONE` と書く。

### 限界

- **誤差の原因の内訳は切り分けていない**。D が大きいほど増える(10^4 と 10^5 で 0、10^6 から出る)ことと、画像の様子は、Omni の記録(`CurrentWork.md` の COSMIC-PRECISION-001)の「物体空間の float の分解能」の説明と合う。
  ただし、CPU が物体の中心を float の仮数・指数に詰める量子化、GPU の頂点の加算、画素ごとの補間のどれが支配的かは測っていない(推測)
- 左の面を、試験点の付近だけ細かい mesh にしている。巨大な 1 枚の四角形にした場合との比較はしていない
- 深度の分解能は §21 で測った(camera からの距離に対する相対距離で 2×10^-6〜10^-3)。深度バッファの形式(D24 か D32F か)を撮影用の RenderTexture で変えても、分解能の値は同じだった(§21)
- 右の面は球の上端で、ボールの水平の移動は試験点から ±2 m(継ぎ目までの距離からも制限)。球面の下がりはこの範囲で 1 µm 未満(計算)で、平面として扱っている
- 結果は GPU(RTX 4060 Ti)とドライバーに依存する。他の GPU で同じ画素数になる保証はない
- 実際のキーボードとマウスから Input System を通る経路、HUD の文字(IMGUI)は確かめていない(§16 と同じ)


## 21. 深度の分解能(DepthResolutionValidation)

Omni の `SV_Depth` で、近い 2 つの面を前後に分けられる最小の相対距離を測る(`VirtualGeometry.Omni.Editor.DepthResolutionValidation.Run`、結果は `TestResults/Planet/DepthResolution`)。

### 方式

- 半径 10^20 m・高さ 0 の `PlanetTerrainSource` を 2 つ(A 青、B 緑の単色)重ね、B の中心を A の中心から up 方向へ Δ(Logical)だけずらす。camera は A の頂上の上 d の高さから真下を向く
- Δ > 0 なら B が手前(正しい色は緑)、Δ < 0 なら A が手前(青)。深度が分けられなければ、同じ深度では後から描いた B が勝つので、Δ < 0 側が間違う。Δ > 0 側は同点でも B が勝つので、測れない(常に合格)
- |Δ|/d を 1e-9 から 2 倍ずつ 1.7×10^-2 まで振り、誤った色の画素が 0 になる最小の値(3 点連続で合格)を求める。斜めの光線は d も Δ も同じ倍率なので、全画素が同じ Δ/d の確認になる。値は 2 倍刻みの粒度
- 距離 d は 1.7 m〜10^20 m(12 点)。View Scale M(1、10、100、1000)、Camera.near(0.03、0.3、3)、撮影用の RenderTexture の深度の形式(D24、D32F)も振る

### 確認(2026-10-12、RTX 4060 Ti、DX12、reversed-Z、160×120、M = 10、Camera.near = 0.3、色は 24 ビット深度の `M0Validation.Capture`)

Δ < 0 側の、分けられる最小の |Δ|/d:

| d | 1.7 m | 10 m | 100 m | 1 km | 10〜100 km | 1000 km | 10^8〜10^12 m | 10^15 m | 10^20 m |
|---|---|---|---|---|---|---|---|---|---|
| 最小の \|Δ\|/d | 2.1×10^-6 | 6.6×10^-5 | 1.3×10^-4 | 6.6×10^-5 | 1.3×10^-4 | 2.6×10^-4 | 5.2×10^-4 | 1.0×10^-3(Δ > 0 側は 5.2×10^-4) | 5.2×10^-4 |

- View Scale M: 1 と 10 と 100 は同じ(d = 1.7 m で 1.0×10^-6〜2.1×10^-6、1 km で 6.6×10^-5、1000 km で 2.6×10^-4、10^12 m で 5.2×10^-4)。M = 1000 は約 2 倍粗い
- Camera.near: 0.03 と 0.3 は同じ。3 は約 2 倍粗い(1 km で 1.3×10^-4、10^9 m で 5.2×10^-4)
- 撮影用の RenderTexture の深度の形式を D24 と D32F(`GraphicsFormat`)で変えても、全条件で同じ値だった
- 画面に穴(背景の画素)は d = 10^15 m までの全距離で 0(Δ = 0)

### 解釈と限界

- 計画時の見積もり(32 ビット float の逆 Z で 3.7×10^-7〜5.8×10^-6)より、遠距離で約 100 倍粗い。d ≲ 100 m は「深度 = near/距離」を 24 ビット固定小数点で量子化した場合の Δd/d = d·2^-24/near(d = 1.7 m、near = 0.03 で 3.4×10^-6)に近く、それより遠くは 10^-4〜5×10^-4 で頭打ちになる。
  要因(Omni の fragment の深度の計算、URP が実際に使う深度バッファの形式)は切り分けていない(推測)。撮影用の RenderTexture の形式が効かなかったことから、URP が自前の camera 深度に描いている可能性がある(未確認)
- 実用上の意味: 分解能より近く重なる 2 つの面は、描く順番で決まる。分解能は距離で変わる(上の表): 地上 1.7 m の目線では 2.1×10^-6 の約 3.6 µm、月から 3.8×10^8 m の地球では 5.2×10^-4 の約 190 km。この計画の接触(ボールと月の面、距離 0.28 m、隙間 1 mm で Δ/d = 3.6×10^-3)は、この分解能より十分粗い
- 10^20 m は、天体の見かけの半径 30° が画面の水平半画角 32° より小さく、隅に背景が出る(3,576 画素)。背景の画素は数えない
- 真下の見下ろしだけを測った。斜めの視線の端や地平線近くは測っていない。結果は GPU(RTX 4060 Ti)とドライバーに依存する
- 合否の基準(10^12 m までの最悪が 1×10^-3 以下)は、実測の約 2 倍の回帰の検出用で、仕様ではない。10^15 m 以上は記録だけ
