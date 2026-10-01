# Benchmark

同一生成 asset / camera 軌道 / 1920×1080 / VSync 0 / Windows DX12 Development Player で、通常 MeshRenderer、自動生成 LODGroup、Virtual Geometry を別 process として比較します。同じ opaque Lit、Base Color (0.65,0.4,0.2)、metallic 0、smoothness 0.25、directional light 1 個、影・fog・ambient なしです。

## 現在の測定方法

Player は **WindowStyle Normal の可視 window** で起動し、通常の enabled camera が swapchain を描画します。`QualitySettings.maxQueuedFrames=1`、targetFrameRate=-1、runInBackground=true、Profiler/GPU Profiler は無効です。全経路で同じ設定を使います。計測中の StandardRequest、readback、PNG、診断 overlay、独自 pacing はありません。計測後だけ backbuffer の実画像と同じ4軌道点の比較画像を保存します。

`FrameTimingManager.gpuFrameTime` の実値を GPU ms とし、frameStartTimestamp と各 Update の native QPC を対応付けます。全9 runで cold/warm各360、CPU/GPUとも合計6,480 samples、GPU欠測0を確認しました。生値は TimingRaw.csv、対応境界は FrameSnapshots.csv、採用値は Metrics.csv。分位点は昇順の ceil((n-1)×p) です。0値が返る環境では欠測として件数と空欄を残し、0 msへ置き換えません。

CPU frame ms は待ちを含む FrameTiming値です。FPS は通常 Player-loop の frame数/実時間です。queue深度1はCPU/GPUの並行度を制限してCPUのGPU待ちを増やし得るため、queue設定が異なる結果と比較しません。coldは空のVG CPU/GPU residency、warmは同じprocessで同じ360-frame軌道を反復します。OS file cacheはflushしていません。

旧Hiddenはcamera callback0、可視の通常queueではGPU欠測が残りました。公開DX12 queue制御を1へ統一して欠測が解消しました。旧offscreen結果と限定probeは `TestResults/M5/GpuTimingInvestigation` に履歴として保存し、現在の比較表には混ぜていません。外部計測ツールや追加native pluginは不要でした。

CPU memoryはGetProcessMemoryInfoのworking setを10 frame間隔でsampleします。GPU driver bytesはUnity allocation estimateであり実VRAM resident bytesではありません。VG pool/CPU cacheの管理値は別記録します。Draw Calls counterは0で取得不能、Triangles counterはVG indirect trianglesを含まずframeにより集約も異なります。実VG geometry draw/trianglesは計測後のindirect argumentsから別に記録し、frame全体のdraw countと混同しません。

## 実行規模

| 入力 triangles | cold変換秒 | worker監視 peak working set (B) | unique geometry (B) | pages | root Proxy triangles |
|---:|---:|---:|---:|---:|---:|
| 1,048,352 | 27.2948 | 2,355,552,256 | 84,794,304 | 10,922 | 12,202 |
| 2,996,352 | 57.9999 | 3,322,302,464 | 238,706,600 | 31,216 | 14,826 |
| 9,999,392 | 210.1538 | 8,053,088,256 | 799,095,952 | 104,167 | 27,792 |

10Mも縮小せず実行しました。後続LOD cut生成中の観測最大working setは9,056,894,976 B。変換workerの12 GiB / 600秒上限へ到達していません。今回のGPU計測では同じ変換assetを再利用し、再変換は行っていません。

## CPU frame ms

| 規模 | 経路 | cold p50 / p95 / p99 (ms) | warm p50 / p95 / p99 (ms) |
|---|---|---:|---:|
| 1M | MeshRenderer | 1.122000 / 1.763400 / 2.205100 | 1.173600 / 1.803200 / 2.104700 |
| 1M | LODGroup | 1.080800 / 1.734100 / 2.393700 | 1.097000 / 1.554700 / 1.925100 |
| 1M | VG | 6.142500 / 7.631600 / 9.182900 | 6.227600 / 7.625700 / 7.809500 |
| 3M | MeshRenderer | 1.879900 / 3.502600 / 4.241000 | 1.850800 / 2.451900 / 2.747700 |
| 3M | LODGroup | 1.726800 / 2.545100 / 2.947900 | 1.754100 / 2.700000 / 2.936200 |
| 3M | VG | 8.390900 / 11.491500 / 12.980300 | 8.816300 / 12.141000 / 12.694700 |
| 10M | MeshRenderer | 4.042600 / 4.802400 / 5.300100 | 3.994000 / 4.671600 / 4.885400 |
| 10M | LODGroup | 3.906700 / 4.661900 / 4.946700 | 3.939300 / 4.633600 / 4.974200 |
| 10M | VG | 12.519800 / 16.306400 / 18.743400 | 12.958900 / 16.214300 / 18.767900 |

## GPU frame ms

| 規模 | 経路 | cold p50 / p95 / p99 (ms) | warm p50 / p95 / p99 (ms) |
|---|---|---:|---:|
| 1M | MeshRenderer | 0.222208 / 0.251904 / 0.253952 | 0.221184 / 0.251904 / 0.254976 |
| 1M | LODGroup | 0.219136 / 0.251904 / 0.256000 | 0.219136 / 0.252928 / 0.262144 |
| 1M | VG | 3.592704 / 4.966656 / 5.308928 | 3.661824 / 4.969216 / 5.328384 |
| 3M | MeshRenderer | 0.817152 / 2.052096 / 2.379776 | 0.811008 / 1.135616 / 1.242112 |
| 3M | LODGroup | 0.711680 / 1.104896 / 1.274880 | 0.785408 / 1.167360 / 1.278976 |
| 3M | VG | 4.551936 / 6.904576 / 7.315200 | 4.546560 / 7.079936 / 7.581952 |
| 10M | MeshRenderer | 2.835456 / 3.460096 / 3.718144 | 2.853888 / 3.424256 / 3.644416 |
| 10M | LODGroup | 2.816000 / 3.397632 / 3.785728 | 2.763776 / 3.378176 / 3.650560 |
| 10M | VG | 4.082688 / 7.472896 / 7.838208 | 4.357120 / 7.498240 / 7.933952 |

## FPSとメモリ

| 規模 | 経路 | FPS cold / warm | CPU working set peak (B) | GPU driver estimate peak (B) |
|---|---|---:|---:|---:|
| 1M | MeshRenderer | 311.90 / 812.87 | 581,722,112 | 274,774,332 |
| 1M | LODGroup | 656.58 / 888.60 | 590,372,864 | 280,929,160 |
| 1M | VG | 117.78 / 157.53 | 571,744,256 | 290,629,444 |
| 3M | MeshRenderer | 166.49 / 524.88 | 651,743,232 | 359,759,036 |
| 3M | LODGroup | 455.89 / 557.21 | 677,507,072 | 368,201,296 |
| 3M | VG | 108.09 / 110.03 | 614,588,416 | 347,806,052 |
| 10M | MeshRenderer | 215.64 / 252.97 | 995,557,376 | 668,106,556 |
| 10M | LODGroup | 250.55 / 281.49 | 1,024,286,720 | 695,459,068 |
| 10M | VG | 71.11 / 73.59 | 764,919,808 | 622,974,052 |

GPU/CPU memoryの数値はそれぞれdriver estimate/working setで、geometry poolの予算と意味が異なります。同期diagnostic readbackや画像生成は時間/メモリの計測区間に含みません。

## Streamingと品質

VG geometry / CPU cacheは各8,388,608 B上限、I/O concurrency2、制御遅延25ms、transfer65,536 B/frame、request capacity64、target2pxです。全source geometryがGPU poolを超えます。

| 規模 | GPU pool (B) | CPU cache peak (B) | I/O 累積 (B) | eviction | quality miss cold / warm | transfer peak (B/frame) |
|---|---:|---:|---:|---:|---:|---:|
| 1M | 8,051,760 | 6,123,432 | 5,956,920 | 256 | 352/360 / 360/360 | 65,536 |
| 3M | 7,981,864 | 8,320,080 | 8,115,320 | 128 | 351/360 / 360/360 | 65,536 |
| 10M | 7,290,000 | 7,870,632 | 7,222,528 | 44 | 344/360 / 360/360 | 65,536 |

I/O/evictionは累積値。quality missは非同期feedbackを観測したframe数で、目標を満たしたという意味ではありません。LODGroup thresholdsは0.75 / 0.4 / 0.001、VGは2px目標なので品質基準も同一ではありません。

1M mean RGB差最大=0.0062208、silhouette XOR=2、reference欠落=1 pixels。
3M mean RGB差最大=0.0019640、silhouette XOR=0、reference欠落=0 pixels。
10M mean RGB差最大=0.0025899、silhouette XOR=0、reference欠落=0 pixels。

これらは計測後4軌道点のVG対full geometry比較です。benchmark軌道の数値depth比較は未実施で、depthの機能検証はM3/M4/M5の別証跡です。限られた常駐予算のVGでは品質目標未達が続き、CPU/GPU両方の中央値も全規模で通常方式より長く、今回の条件で性能優位はありません。通常MeshRenderer/LODGroupが予算内に収まり、frame timeと品質目標を優先する場合は通常方式へ戻す判断が妥当です。

## 証跡と再現

`TestResults/M5/Benchmark{724,1224,2236}` にPreparationEnvironment / PreparationResults / ConversionMetrics / BuildReport、各経路Results.json・Metrics.csv・Environment・Swapchain.png・Trajectory PNG・GeometryDiagnostics.csv・ImageComparison.jsonを保存します。横断集計はBenchmarkSummary.csv/json、集計scriptはSummarizeBenchmark.pyです。

`TestResults/M5/Reproduce-M5.ps1 -Stage BenchmarkPrepare -Grid 724` → BenchmarkBuild → BenchmarkRun。3Mは1224、約10Mは2236。準備した元モデル(Source.obj、最大約1000万三角形)・中間LOD・それらを参照するBenchmark sceneは、BenchmarkBuildがPlayerのbuild成功後に削除します(sceneはBenchmarkBuildが毎回作り直すため、gitにも入れません)。数百MBあり、変換器の版が変わるたびにEditorの再インポートで数分止まるためです。計測をやり直すときはPrepareから実行します(`M5BenchmarkPreparation.Clean`で手動削除もできます)。Unity buildはHidden、benchmark PlayerはNormal、各exact PIDの終了を待って逐次実行します。Playerをminimize/非表示にしません。baseline比較のためbenchmark Playerには高密度sourceを意図的に収録します。配布Playerのsource/native除外は別のPlayerBuild証跡です。

ローカルUnity 6000.6 APIと[Frame Timing Manager](https://docs.unity3d.com/ja/current/Manual/frame-timing-manager.html)、[maxQueuedFrames](https://docs.unity3d.com/cn/6000.0/ScriptReference/QualitySettings-maxQueuedFrames.html)の契約を確認しています。GPU時間からGPU内の処理内訳や他processの影響を分離する分析はしていません。
