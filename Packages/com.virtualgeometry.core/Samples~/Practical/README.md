# 小型 Lit / Streaming sample

Package Manager の sample import 後、または package を導入した状態で、`Tools > Virtualized Geometry > Create Practical Sample` を実行します。既存の検証データは不要です。未保存 scene がある場合は Unity の保存確認が表示されます。

`Assets/VirtualizedGeometrySample/Practical` に 2,048 triangles の wave Mesh、変換 asset と archive、専用 Forward renderer/pipeline、`Practical.unity` を生成します。生成した pipeline を Graphics / 現在の Quality に割り当て、Windows Standalone の graphics API を DX12 に設定します。元の任意の Mesh asset を変更しません。

scene には同じ変換データを共有する 2 instances、異なる Base Color、directional light、通常 mesh の shadow receiver を配置します。root 準備中は低密度 Proxy を表示し、非同期 I/O と転送の後に専用描画へ切り替えます。GPU geometry 予算は全 page 分(slot bytes × page 数)、転送は 2 slot 分 / frame で、近づくと leaf まで詳細化されます。低予算時の挙動は M4 / M5 検証が別設定で確認します。Shadow Proxy と MeshCollider も有効です。

生成した `Practical.unity` の Game View では、`Sample camera` に実行時カメラ操作が付きます。Play Mode に入り、Game View をクリックしてフォーカスを置いた状態で、`W/S` で前後（対象への接近・離脱）、`A/D` で左右、`Q/E` で上下へ移動します。`Shift` を押している間は移動速度が 3 倍になります。カメラの距離を変えると、画面誤差に基づくクラスタ LOD の切り替えを Game View で確認できます。`Tools > Virtualized Geometry > Monitor` を開くと、Game view を覆わずに描画三角形数、Level 別の選択 node 数(Level 0 が最詳細)、cut 変化 /s、page 常駐を確認できます。静止中に cut 変化が 0 であれば LOD は振動していません。

自分の Mesh は Project で右クリック →「仮想ジオメトリに変換」で変換し、生成した asset の Inspector の「選択GameObjectに配置」で専用 Renderer と Proxy を配置します。builder 7 より前に生成した `Converted.asset` は、Inspector の「再変換」で root が 1 cluster 以下になるまでの階層へ更新できます。Inspector の誤差・メモリ・理由、Scene view の Virtual Geometry toolbar の各 mode・通常/専用切替と VG Stats を確認できます。任意の shader 互換や高密度 collider を提供する sample ではありません。

headless 検証は `VirtualizedGeometry.Editor.VirtualGeometrySampleSetup.Run`。画像と結果は `TestResults/M5/Sample` に保存します。Player の作成は `VirtualGeometryPlayerBuild.Build` を使い、README と PlayerBuild.md の収録境界を確認します。