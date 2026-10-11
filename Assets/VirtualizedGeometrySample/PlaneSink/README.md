# 単純に拡大した面と VG の面に、ボールを沈めるデモ(PlaneSink)

Omni の対数描画(Radial)で、三人称 camera のボール(半径 0.11 m、画面の中央)を、2 つの巨大な面に 1mm(Ctrl で 0.1mm)ずつ沈めて比べます。

- **左(青みの面)**: 単位の平面を一様に S 倍した 1 つの物体(単純な拡大)。物体空間の float の座標が量子化され、深度と位置が S·2^-25(面の原点から試験点まで D=10^6 m で約 6cm)の刻みになります。
  ボールを沈めると、面との交わりが飛び、格子の線が崩れます。
- **右(緑の面)**: VG(`PlanetTerrainSource`、半径 2×10^6 m、高さ 0)の上端。Cell ごとの局所原点なので、1mm の動きが 1mm のまま画面に出ます。格子の線もまっすぐです。

左は右より 1 m 高い段で、すぐ隣(継ぎ目 x=0)に並んでいます。重なりも z-fighting もありません。
ボールは左右とも VG です。差は面だけです。

## 開き方

1. メニュー `Tools > Virtualized Geometry > Moon And Ball > Use Moon And Ball Pipeline`(この demo の URP の Renderer と Pipeline を選ぶ。月とボールのデモのものを使う)
2. `PlaneSink.unity` を開いて Play
3. 作り直すとき: メニュー `Tools > Virtualized Geometry > Plane Sink > Build Scene`(batchmode は `-executeMethod VirtualGeometry.Omni.Editor.PlaneSinkSceneBuilder.Run`)

Omni(`com.omni.rendering`)が必要です。Omni の `Radial` で描く camera(`Logical.Transform` と `RadialCamera`)を使います。
左の面の mesh は、Play したときに `PlaneSinkDemo` が作ります(scene には保存しません)。

## 操作(Game View をクリックしてから)

| キー | 動き |
|---|---|
| ↑ ↓ | ボールを面から 1mm 浮かせる / 沈める(Shift で 10mm、Ctrl で 0.1mm。長押しで繰り返し) |
| W A S D | camera の向きを基準に水平へ(速さは連続。Shift で速く、Ctrl で遅く。試験点から ±2 m まで) |
| ← → / PageUp PageDown | camera の周回(yaw / pitch) |
| Home End / ホイール | camera の距離 |
| 右ドラッグ | 周回 |
| 1 / 2 | 左(単純な拡大)/ 右(VG)の試験点へ |
| 3 | 全体を見る |
| [ ] | 左の面の原点までの距離 D を小さく / 大きく(10^4、10^5、10^6、10^7 m) |
| Space | 自動で沈める / 浮かせる |
| R | 初期化 |

HUD に、今の側、D、物体空間の float の刻み(左)、沈めた深さ、camera の値が出ます。

沈めるときの見方: 右では、沈めた深さに比例して交わりの円が広がり、縁はなめらかです。
左では D=10^6 m で、縁がぎざぎざになり、1mm ずつ沈めても変化が飛びます。D=10^7 m では平面がボールを貫いてちらつきます。D=10^4 m では右とほとんど変わりません。

## 検証

```
Unity.exe -batchmode -force-d3d12 -projectPath <project> -executeMethod VirtualGeometry.Omni.Editor.PlaneSinkValidation.Run
Unity.exe -batchmode -force-d3d12 -projectPath <project> -executeMethod VirtualGeometry.Omni.Editor.PlaneSinkPlaySmoke.Run
```

`Run` は、各側でボールを 1mm ずつ沈め(+2mm から −60mm)、水平・周回・距離を動かし、左だけ D を変えて、単色の画像を、Logical の位置から倍精度の光線で求めた球と面と、1 画素の帯の外で比べます。
結果は `TestResults/Planet/PlaneSink`(Report.txt、Metrics.csv、画像)。`RunSpike` は少ない frame で確かめます(`TestResults/Planet/PlaneSinkSpike`)。
`PlaneSinkPlaySmoke.Run` は Play Mode で、キーごとの量と向き、ボールが常に画面の中央にあること、エラーが出ないことを確かめます(キーは `IPlaneSinkKeys` を差し替えて送ります)。
結果と考察は `Requirements/PlanetWorld.md` の §20。

実際のキーボードとマウスから Input System を通る経路と、HUD の文字(IMGUI)は、batchmode では確かめていません。
