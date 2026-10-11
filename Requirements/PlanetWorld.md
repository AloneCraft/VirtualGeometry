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
データの1セルごとに平均0にする。

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
Player のビルドと実行(`M5PlayerBuildValidation`)は、この変更では実行していない。

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

`PlanetTerrainSource` は Cell の頂点を、Cell の原点(Cell の中心の方向の半径 R の点、惑星のローカル座標)からの相対位置で生成し、Cell ごとに draw する。

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
- M≠1 の View Scale、eye が warp 中心に無い場合(fail-open の経路)
- z-fighting と深度の分解能の測定
- Play/Stop の往復と、GraphicsBuffer のリークの計数
- Scene View での表示


## 12. Cell の精度の基盤(P4a)

### 方式

Cell の中心の値を CPU が Logical 精度で求め(`PlanetCellFrame`)、GPU は Cell の中心からの相対量だけを float で計算する。

- 中心の角度は π·n/2^(L+2)(n=2x+1−2^L)。n/2^(L+2) を固定小数点で正確に作るため、階層は 94 まで(`PlanetCellFrame.MaximumLevel`)
- 中心の正接 x0、y0 は、角度の sin と cos をテイラー展開(14 項、|角度|≤π/4)で求めて割る。半角の公式の繰り返しは、深い階層で角度の誤差が倍々に増えるので使わない
- Cell の原点 = R·p0/|p0|(p0=n+x0·u+y0·v)。R を掛けるのは正規化の後(R² を作らない)
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

未実施・未解決:

- 地面から 1.7m では pool(2048)を使い切る(半径 600km で 586 Cell 描画、使用 2048)。退避と生成待ちは起きるが、穴と設定エラーは無い

(選択の Logical 化と高さの帯域制限は §13 で扱った。)


## 13. 高さの Field、選択の Logical 化、降下カメラ(P4b)

### 選択の Logical 化

`PlanetSelectionView` はカメラの位置を惑星のローカル座標の Logical で持つ。

Cell の bounds の中心は Cell の原点(`PlanetCellFrame.Origin`)からの相対位置で持ち、カメラとの差を Logical で取ってから float にする(`PlanetCellSelector.FromCamera`)。

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
- 時刻の位相 ω·t は 2π で巻いてから渡す(float の t が大きくなると精度が落ちる。長時間の連続実行での精度は未確認)
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
- z-fighting: camera を 0.1mm ずらして撮り直すと、海と陸の境から 1px の帯の外で判定が入れ替わる画素は 0。
  帯の中(境の線そのもの)では 0〜2 画素が入れ替わった(0.1mm のずれで境の線が画素の中心を横切るため。深度の精度の問題とは区別できていない)
- 高度帯の遷移(目標 1/4 との比較): P5a の時点(材質は単色の Lit、海岸線は深度の交わり)では、1.7m と 10km では海と陸の境が 1px の帯の外で 0、
  **100m と 1km では 2 画素ずつ帯の外で違い、不合格だった**。違ったのは 1〜2 画素の小さな陸の点(水面からわずかに出た岩の先)で、
  細かい描画で入る短い波と細かい地形で水面との上下が入れ替わる。傾きのゆるい海岸ではわずかな上下の差が大きな水平のずれになる。
  P5b の shader が海岸線を Field の水深で決めるようにして解消した(§15)

未実施・未解決(P5b で扱ったものは §15):

- 深度の分解能の実測(Omni の対数深度での、距離ごとの z-fighting が起きる相対距離)
- 陸だけの Cell の海を描かない最適化(今は全部描いて、水深で捨てている)
- 長時間の連続実行での波の時刻の精度


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

未実施・未解決:

- 波の法線の式(`OceanShortWaves`)と不動点反復の精度を、CPU の参照と数値で比べていない(見た目の遷移の平均の差だけで確かめた)
- 変位が Gerstner の水平成分(尖り)を持つ波の傾きは、法線へ足すときは無視している(一次近似)
- 空の反射・大気・水中の見え方は未実装
- 深度の分解能の実測、陸だけの Cell の海を描かない最適化は §14 のまま


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

### 結論と限界

月とボールの 10^7 倍の違いでも、10^20 m の天体とボールの 10^21 倍の違いでも、ボールを面にぎりぎり接して 1mm 単位で動かせた。
Omni の COSMIC-PRECISION-001(月の面がボールの縁を越える)に当たる現象は出なかった。
半径 10^20 m の天体は、ボールの大きさでは平面と区別がつかない(1 m 離れた所の球面の曲がりは約 5×10^-21 m)ので、巨大な平面と同じ実証になる。

- **分割しない場合の比較(負の対照)はしていない**。分割しないと壊れることは、Omni の記録と、float の分解能の理屈による(月 1 つの物体では物体空間の分解能が約 0.1〜0.2 m)
- 月は手続き生成の起伏で、実際の月の地形ではない。ボールは滑らかな球で、模様は shader(パネルの形状はない)
- 色は照明ありの見た目も画像で確かめたが、見た目の合格条件はない(位置と形だけを数えた)
- キー操作は、`IMoonBallKeys` を差し替えて確かめた(押した frame に 1 回分だけ動く: ↑↓←→ PageUp/Down が各 1mm、Shift で 10mm、Ctrl で 0.1mm、両方なら Ctrl 優先。1/2/3/Space/R の結果も確認)。
  実キーボードから Input System を通る経路は確かめていない。焦点の無い batchmode では、仮想のキーボードに積んだ状態イベントが `InputSystem.onEvent` に 1 つも届かなかった
  (Input System の設定は `IgnoreFocus` と `AllDeviceInputAlwaysGoesToGameView`、`gameIsPlaying` と `gameHasFocus` は true、Dynamic 更新は回っていた。原因は未特定)。
  HUD の文字(IMGUI)は Game View で見ていない(batchmode では写らない)
- 論理 near が 0.03 m なので、近接の視点(面から 1 cm)では camera の真下の 3 cm 以内の地面が切れて背景が見える(near の切り取り)
- 1 つの scene に 0.11 m、1 m、月、10^20 m の 4 つを同時に置く試験(§7 の P6)はしていない。ボールと 1 つの大きな天体の 2 つだけ
- 10^20 m の scene では、月の Cell の pool(4096)を使い切る。設定エラーは出なかったが、描画の時間は測っていない
- 描いている頂点(メッシュ)の精度は、半径 6,371 km の地形の CPU と GPU の差(最大 24 mm、§13)のように、大きな半径の地形では Cell の原点の float の近似による数 mm〜数 cm の差が出うる。
  この demo の接点では、月の高さの CPU と GPU の差は 6 µm 以内だった(CPU の高さで置いたボールの接触の隙間で確かめた)。差の原因は調べていない
