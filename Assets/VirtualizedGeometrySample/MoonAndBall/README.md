# 月とサッカーボール(精密な配置のデモ)

半径 1,737.4 km の月の表面に、半径 0.11 m のサッカーボールを接して置き、1mm(Ctrl で 0.1mm)単位で動かすデモです。
大きさが 10^7 倍違う 2 つの物体を同じ scene に置いても、ボールの 1mm の動きが 1mm のまま画面に出ることを示します。

月もボールも、cube-sphere の Cell に分けた実行時生成 cluster(`PlanetTerrainSource`)で描きます。
Cell の頂点は Cell の中心からの相対位置で持ち、Cell の中心と物体の位置は Omni の Logical(Q95.96 固定小数点)で持つので、
月の半径が大きくても float の桁が足りなくなりません。

| scene | 大きい天体 |
|---|---|
| `MoonAndBall.unity` | 月(半径 1,737.4 km、地形の高さ ±300 m) |
| `GiantAndBall.unity` | 半径 10^20 m の天体(ボールの大きさでは平面と区別がつかない。高さ ±100 m) |

## 開き方

1. メニュー `Tools > Virtualized Geometry > Moon And Ball > Use Moon And Ball Pipeline`(この demo の URP の Renderer と Pipeline を選ぶ)
2. `MoonAndBall.unity` を開いて Play
3. 作り直すとき: メニュー `Tools > Virtualized Geometry > Moon And Ball > Build Scenes`(batchmode は `-executeMethod VirtualGeometry.Omni.Editor.MoonBallSceneBuilder.Run`)

Omni(`com.omni.rendering`)が必要です。Omni の `Radial` で描く camera(`Logical.Transform` と `RadialCamera`)を使います。

## 操作(Game View をクリックしてから)

| キー | 動き |
|---|---|
| ↑ ↓ | ボールを月の面から 1mm 離す / 近づける |
| ← → | 面に沿って 1mm 転がす(模様が回る) |
| PageUp / PageDown | camera の手前 / 奥へ 1mm |
| Shift / Ctrl を押しながら | 10mm / 0.1mm |
| Space | 自動の動き(1mm ずつ、上下・転がす・手前奥) |
| 1 | 接触の視点(Omni の COSMIC-PRECISION-001 と同じ配置: 月の面から 0.17m、ボールの中心から 0.28m) |
| 2 | 月全体 |
| 3 | 月全体から接触の視点までのズーム(25 秒) |
| R | 初期位置 |
| WASD / QE、右ドラッグ | 飛行(速度は月の面からの高さに比例。面より下へは行かない) |

画面左上に、隙間(mm)、転がした量、ボールの中心の月の中心からの距離(Logical の値)を出します
(文字は IMGUI。Game View で確かめていません)。

月の面の格子は、接点を原点に 1m、10cm、1cm です。

## 仕組みの要点

- 位置は `MoonBallDemo` が Logical のまま計算する(接点 = 月の中心 + 単位ベクトル·(R + 高さ)。ボールの中心 = 接点 + up·(r + 隙間) + u·x + v·z)。float にするのは camera との差を取った後だけ
- 月の高さは CPU(`PlanetTerrainSource.TrySampleSurface`)と GPU の頂点で同じ式。接点の高さは CPU で求める
- 格子は、Cell の原点 − 基準点(接点)を Logical で引いた値(`_PlanetCellRef`)から作る。惑星の大きさによらず mm の精度で描ける
- 詳しい設計と検証の結果は `Requirements/PlanetWorld.md` の §16

## 検証

```
Unity.exe -batchmode -force-d3d12 -projectPath <project> -executeMethod VirtualGeometry.Omni.Editor.MoonBallValidation.Run
Unity.exe -batchmode -force-d3d12 -projectPath <project> -executeMethod VirtualGeometry.Omni.Editor.MoonBallValidation.RunGiant
Unity.exe -batchmode -force-d3d12 -projectPath <project> -executeMethod VirtualGeometry.Omni.Editor.MoonBallPlaySmoke.Run
```

結果は `TestResults/Planet/MoonBall`、`GiantBall`、`MoonBallPlay`(Report.txt と画像)。
`MoonBallPlaySmoke` のキーの確認は、入力元(`IMoonBallKeys`)を差し替えて、キーの割り当てと量を見る。実キーボードの Input System 経由は、手で Game View を操作して確かめる。
