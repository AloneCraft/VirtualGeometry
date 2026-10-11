# 4 つの天体を積んだデモ(大きさが 10^21 倍違う物体の精密な配置)

巨大な天体(半径 10^20 m)の面に月(半径 1,737.4 km)を接して置き、月の上に球(半径 1 m)、球の上にサッカーボール(半径 0.11 m)を、同じ垂直線の上に積みます。
隣り合う天体は面で接し(接点 1: 巨大な天体と月、2: 月と球、3: 球とボール)、接点ごとに 1mm(Ctrl で 0.1mm)単位で動かせます。

すべての天体は、cube-sphere の Cell に分けた実行時生成 cluster(`PlanetTerrainSource`)で描きます。
Cell の頂点は Cell の中心からの相対位置、Cell の中心と天体の位置は Omni の Logical(Q95.96 固定小数点)で持つので、
半径が大きくても float の桁が足りなくなりません。天体の中心は、下の天体の中心から Logical のまま積み上げて求めます。

## 開き方

1. メニュー `Tools > Virtualized Geometry > Moon And Ball > Use Moon And Ball Pipeline`(この demo の URP の Renderer と Pipeline を選ぶ。月とボールのデモのものを使う)
2. `BodyStack.unity` を開いて Play
3. 作り直すとき: メニュー `Tools > Virtualized Geometry > Body Stack > Build Scene`(batchmode は `-executeMethod VirtualGeometry.Omni.Editor.BodyStackSceneBuilder.Run`)

Omni(`com.omni.rendering`)が必要です。Omni の `Radial` で描く camera(`Logical.Transform` と `RadialCamera`)を使います。

## 操作(Game View をクリックしてから)

| キー | 動き |
|---|---|
| 1 / 2 / 3 | 接点 1(巨大な天体と月)/ 2(月と球)/ 3(球とボール)を選び、camera をそこへ |
| 4 | 全体(月と巨大な天体の面) |
| ↑ ↓ | 選んだ接点の上の天体を、下の天体の面から 1mm 離す / 近づける |
| ← → | 面に沿って 1mm 転がす(球は転がり、模様が回る) |
| PageUp / PageDown | camera の手前 / 奥へ 1mm |
| Shift / Ctrl を押しながら | 10mm / 0.1mm |
| R | 初期位置 |

接点を動かすと、その上に積んだ天体も一緒に動きます(上の天体どうしの隙間は変わりません)。

接点 1 の視点は、巨大な天体の面の上 0.17m にあり、月の中です(月は内側から見るので描かれません。月の skirt が細い線に見えます)。
月と巨大な天体の間は、半径の大きな 2 つの面がほぼ平行に近づくだけなので、接点の近くでは 1mm の隙間の変化が画面では見えません(隙間が 1mm から 2mm に増えるまでに、接点から横に約 60 m 離れます)。
接点 1 の隙間は、描いている頂点(メッシュ)の読み戻しで確かめます(`Requirements/PlanetWorld.md` の §17)。

## 検証

```
Unity.exe -batchmode -force-d3d12 -projectPath <project> -executeMethod VirtualGeometry.Omni.Editor.BodyStackValidation.Run
```

結果は `TestResults/Planet/BodyStack`(Report.txt と画像)。描画の時間だけは `RunTiming`、地上に立ったときの Cell の数と継ぎ目は `RunCells`。
