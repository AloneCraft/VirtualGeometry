# 単純に拡大した平面と VG の平面を、同じ高さで隣り合わせるデモ(PlaneSeam)

Omni の対数描画(Radial)で、2 枚の平面を同じ高さ(y=0)で継ぎ目 x=0 に隣り合わせ、位置合わせの誤差(継ぎ目の隙間・段差)を見ます。camera は自由に動かせます。

- **左(青みの面、x<0)**: 単位の平面を一様に S 倍した 1 つの物体(単純な拡大)。物体空間の float の座標が量子化され、位置と深度が S·2^-25(面の原点から試験点まで D=10^6 m で約 6cm)の刻みになります。継ぎ目(右端)が x=0 からずれて、右の面との間に隙間や段差が出ます。格子の線も崩れます。
- **右(緑の面、x≥0)**: VG の平面(`PlanarTerrainSource`、一辺 2^25 m、高さ 0)。Cell の原点は Logical、頂点は Cell の原点からの相対位置なので、継ぎ目は x=0 に正確に置かれ、格子の線もまっすぐです。

右の面は ↑↓ で 1mm ずつ上下に動かせます(左との高さの差 δ)。δ を変えながら継ぎ目を見ると、位置合わせの誤差を mm の単位で確かめられます。

## 開き方

1. メニュー `Tools > Virtualized Geometry > Moon And Ball > Use Moon And Ball Pipeline`(この demo の URP の Renderer と Pipeline を選ぶ。月とボールのデモのものを使う)
2. `PlaneSeam.unity` を開いて Play
3. 作り直すとき: メニュー `Tools > Virtualized Geometry > Plane Seam > Build Scene`(batchmode は `-executeMethod VirtualGeometry.Omni.Editor.PlaneSeamSceneBuilder.Run`)

Omni(`com.omni.rendering`)が必要です。Omni の `Radial` で描く camera(`Logical.Transform` と `RadialCamera`)を使います。
左の面の mesh は、Play したときに `PlaneSeamDemo` が作ります(scene には保存しません)。左の面の材質 `LeftPlane.mat` には、Play 中だけ格子の値(`_GridOriginOS`、`_ObjectScale`)を書き、終了時に元へ戻します。

## 操作(Game View をクリックしてから)

| キー | 動き |
|---|---|
| W A S D | camera の向き(yaw)を基準に水平へ動く(Shift で 10 倍、Ctrl で 1/10) |
| E Q | camera を上げる / 下げる(面から 5cm より下へは行かない) |
| ← → / PageUp PageDown | camera の向き(yaw / pitch) |
| 右ドラッグ | camera の向き |
| ホイール | 向いている方向へ寄る / 離れる |
| ↑ ↓ | 右の面(VG)を 1mm 上げる / 下げる(Shift で 10mm、Ctrl で 0.1mm。長押しで繰り返す) |
| 1 / 2 / 3 | 継ぎ目 / 左の試験点 / 右の試験点の視点 |
| [ ] | 左の面の原点までの距離 D を小さく / 大きく(10^4、10^5、10^6、10^7 m) |
| R | 初期化(右の面の高さの差と camera) |

HUD に、左の面の倍率と D、float の刻み、右の面の高さの差 δ、camera の位置と向きが出ます。

見方: 継ぎ目(1 の視点)の左右を比べます。D=10^6 m では、左の面の端が x=0 から数 cm ずれて、背景の隙間(黒い帯)が見えます。D=10^4 m では 0.6 mm の刻みなので、ほとんど見えません。
右の格子の線は全域でまっすぐで、継ぎ目で左の格子と同じ位置(整数 m)に並ぶはずですが、左は D が大きいと崩れます。

## 検証

```
Unity.exe -batchmode -force-d3d12 -projectPath <project> -executeMethod VirtualGeometry.Omni.Editor.PlaneSeamValidation.Run
Unity.exe -batchmode -force-d3d12 -projectPath <project> -executeMethod VirtualGeometry.Omni.Editor.PlaneSeamPlaySmoke.Run
```

`Run` は、右の面だけ(左を消す)と、左右とも(D を 4 段)で、右の面の高さの差 δ(−3〜+3mm)と視点 3 つを振り、単色の画像を、Logical の位置から倍精度の光線で求めた 2 枚の面と、1 画素の帯の外で比べます。
結果は `TestResults/Planet/PlaneSeam`(Report.txt、Metrics.csv、画像)。`PlaneSeamPlaySmoke.Run` は Play Mode で、キーごとの量と向き、エラーが出ないことを確かめます(キーは `IPlaneSeamKeys` を差し替えて送ります)。
`Tools/Run-PlanetRegression.ps1 -Jobs PlaneSeam,PlaneSeamPlay`。結果と考察は `Requirements/PlanetWorld.md` の §22。

開いている Editor の中では、Edit Mode の `Time.frameCount` が進まず、Cell の生成が 1 回しか進まないので、`PlaneSeamValidation` は batchmode(Editor を閉じたプロジェクト)で走らせます。
実際のキーボードとマウスから Input System を通る経路と、HUD の文字(IMGUI)は、batchmode では確かめていません。
