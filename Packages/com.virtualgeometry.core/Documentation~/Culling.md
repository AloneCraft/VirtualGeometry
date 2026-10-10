# M3: Frustumと現フレームHZB

M3はUnity 6000.6.0f1 / URP 17.6.0 / Windows DX12 / Forward / Base Camera / MSAA 1で検証済み。v0.1全体の完成を意味しない。

## 実行順序と境界

通常opaqueの描画後、RenderPassEvent.AfterRenderingOpaquesでactive camera depthを読み、現フレームのHZBを生成する。その後、GPU階層選択で採用した各clusterをFrustum/HZB判定し、可視indexと間接描画引数を生成して描画する。

URP 17.6はRenderPassEventを区間として注入する。AfterRenderingOpaques - 1は通常opaqueの前の区間に入るため使用しない。

HZBはカメラのRenderGraphに属するtransient textureで、履歴を保持しない。遮蔽物やカメラの移動、解像度や投影の変更は、その描画の深度と行列に反映される。前フレームのHZB、別カメラのHZB、virtual geometry自身の前フレーム深度は使わない。この方式ではvirtual geometry同士の自己遮蔽を棄却できない。

現在のカリングは階層nodeを持つページが対象。階層なしページは従来の全cluster描画を維持する。XR、Camera Stack、MSAAは現在の対応外。検証は全画面viewportで行っている。

## 保守規則

- ローカル境界球をobject matrixと最大軸scaleでworldへ変換する。Frustumの6平面のいずれかから球全体が外れた場合だけ棄却する。
- HZB mip0はraw device depthをコピーする。サイズを2の累乗へ広げ、画面外を背景深度で埋める。通常Zはmax、Reversed Zはminで最遠深度を縮小する。背景を含む領域は過剰棄却を防ぐ。
- カメラが球内/近傍にある場合、球がnear planeに交差する場合、AABB cornerのclip.wが正でない場合はHZBで棄却しない。
- world sphereを覆うAABBの8 cornerをGPU projectionで投影する。矩形を1画素拡張し、各軸の長さが1 texel以内に収まるmipをceil(log2(extent))で選ぶ。この矩形が交差し得る最大2x2 texelを4 cornerで全て参照する。
- 最寄りobject depthがHZBの最遠depthより奥にあり、raw depth bias 0.00001を超える場合だけ棄却する。
- 階層の採用nodeとleaf被覆はM2の規則を維持する。カリングは採用表現内のclusterに対して行うため、選択node flagsは棄却後も階層cutを表す。

大きなcluster境界球は部分遮蔽物に収まらず、棄却数が減る。これは保守性によるもので、画像欠落を避けるため判定を緩めない。クラスタリングの空間的なまとまりは別途性能を左右する。

CPUでも、視錐台の外のinstanceはGPU選択・描画・feedbackのpassを作らない(部品のworld boundsをcameraの視錐台で判定)。GPUのFrustum棄却と同じ結果になる(全clusterが棄却されるinstanceを省くだけ)が、そのcameraでは選択状態を作らず、`CullingStatistics`も更新しない。`SetCullingSettings`のfrustumをfalseにした部品はCPUでも判定しない。

## 設定と統計

VirtualGeometryRenderer.SetCullingSettings(frustum, occlusion)で個別に有効/無効を切り替える。両方falseが比較基準モード。

ReadbackSelectionForDiagnostics().CullingStatisticsは直近の当該renderer描画について、considered / Frustum棄却 / HZB棄却 / visibleの4値を返す。considered == Frustum + HZB + visibleを維持する。同期GPU readbackのため、常時稼働する性能HUDには使わない。

Selectは1 materialにつき独立したDFSで、1 thread/groupをdispatchする。64 thread/groupでは非使用laneにも大きなstackを予約してregister警告を起こすため、実処理単位に合わせている。M3はmaterial並列の基本実装であり、大規模sceneの性能保証ではない。

## 実証結果

TestResults/M3/Results.txt、Metrics.csv、Environment.txt、各シナリオのCulled/Reference/Difference PNGに記録する。

- 通常Z/Reversed Zの両方で、7x3入力→8x4背景埋め→全4mipのGPU値がCPU期待値と完全一致。
- 基準: considered 576 / Frustum 340 / HZB 45 / visible 191、indices 18,336。
- 遮蔽物移動直後: HZB 0 / visible 236、indices 22,656。隠れていた領域が同じ描画から現れる。
- カメラteleport、713x431 resize、orthographic、負の非一様scale、near plane交差を含む7シナリオで、カリング無効画像との全画素差が0。
- 実カメラの全工程はDX12 Reversed Zで検証。通常ZはGPUコピー/縮小とCPU比較規則を検証しており、通常Zのカメラ描画対応を実証したものではない。

再実行はUnityをこのprojectで起動していない状態で、Unity 6000.6.0f1へ以下の引数を指定する。

    -batchmode -force-d3d12 -projectPath G:\Unity\6.6\VirtualGeometry -executeMethod VirtualizedGeometry.Editor.M3Validation.Run -logFile G:\Unity\6.6\VirtualGeometry\Logs\M3Validation.log

非同期のEditor updateで検証するため-quitは付けない。検証自身が結果を書いて終了する。