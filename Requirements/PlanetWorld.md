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
| `VirtualGeometry.Omni`(asmdef) | Logical の差の符号化、ViewSnapshot によるカメラ入力、Omni の RendererFeature の互換登録、Radial shader 経路 | VG core、`com.omni.rendering` |
| 世界パッケージ | Domain、Cell、Field、Layer、Override、惑星、海、降下カメラ | VG core(Omni は adapter 経由) |

VG core は Omni に依存しない。

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

確認したのはコンパイルまでで、実画面での描画は未実施である。

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
- Omni 接続の検証(`Assets/Development/PlanetSpike` の P3a など)は、Omni を入れたプロジェクトで実行する

新しい git worktree では、`core.autocrlf=true` のため、検証が生成する OBJ が CRLF で取り出される。

検証は LF で書き出すので、初回の実行だけ M4、M5Shared、M5Lit、M5Geometry が
`Build asset version error` で不合格になる。2 回目から合格する。

2026-10-10 の確認:Omni なし・P3a の変更あり(コミット前)で、EditMode 51/51 と M0〜M5 の 11 本が合格した。
M3 の cull の統計(Frustum 棄却、HZB 棄却、可視 cluster、画像の差分)は、変更前の記録と全シナリオで一致した。
Player のビルドと実行(`M5PlayerBuildValidation`)は、この変更では実行していない。


## 10. 惑星の Cell の選択と生成(P3)

実装は `Packages/com.virtualgeometry.world`(`PlanetCellKey`、`CubeSphere`、`PlanetCellSelector`、`PlanetTerrainSource`、`PlanetTerrainGenerate.compute`)。

cube-sphere の面の軸は、外向き法線が cross(v,u) になる向きに置く。

面の座標から方向へは等角写像(tan)で写す。

同じ階層の隣の Cell は、面の境界をまたぐ場合も整数で求める(浮動小数の誤差を持ち込まない)。

Cell の bounds は、生成済みなら GPU が求めた cluster の bounds を非同期で読み戻したものを使い、無ければ解析的な bounds を使う。

解析的な bounds は惑星全体の高さの幅を含むため大きく、小さな Cell の選択には使えない。

2026-10-10 の確認:解析的な bounds だけでは、地上 10m で Cell が 947 個選ばれて pool(2048)が枯渇した。
実際の bounds に変えると 79 個になり、退避も枯渇も無くなった。

GPU は面の通し番号を float で扱うため、階層は 18 までに限る(`PlanetTerrainSource.GpuMaximumLevel`)。

それより細かい階層は、Cell の局所原点(P4)で扱う。

### 検証(`VirtualGeometry.World.Editor.P3bPlanetValidation`)

穴は「内側の球(半径 R−Hmax から、描いている最も粗い階層の弦の沈み込みを引いたもの)に、縁から 1 画素分より深く入る光線の画素が背景色であること」と定める。

縁では地形の輪郭と内側の球の輪郭が画素未満しか離れないため、許容量なしでは描画が正しくても穴と数える
(2026-10-10 に、許容量なしで 32 画素を数え、1 画素分の許容量で 0 になった)。

2026-10-10 の結果(半径 10km、Hmax 300m、目標 2px、640×480 と 320×240):固定の 4 視点で穴と継ぎ目の候補が 0、
目標を 1/4 にした描画との輪郭の差が 1px の帯の外で 0、宇宙から地上までの 121 frame で穴 0、
生成予算 1 での瞬間移動の 60 frame で穴 0、使用 slot は最大 1626/2048。
