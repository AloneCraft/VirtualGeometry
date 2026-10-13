# Debugger

UE5 の viewport toolbar(View Mode ▸ Nanite Visualization、Show、stat Nanite)を模した Scene view の overlay toolbar「Virtual Geometry」で操作します。Scene view の overlay menu(`` ` `` キー)から表示・非表示を切り替えられます。Unity の Overlay API は Scene view 専用のため、Game view には toolbar も表示も重ねません。camera ごとの選択状態は下記の Monitor window で確認します。

- `VG: <mode>` ドロップダウン: 可視化 mode を全 instance に適用します。通常 / Triangles / Clusters / LOD Level / Screen Error / Resident Pages / Loading / Culling。renderer 単位の設定なので Game view にも同じ表示が出ます。
- `Show` ドロップダウン: Frustum culling、Occlusion culling (HZB)、通常 Proxy の強制表示、診断 readback と CPU/GPU timing を切り替え、Monitor window を開きます。チェック状態は最初に見つかった有効な instance の値です。
- `Stats` トグル: VG Stats panel を開閉します。

VG Stats panel では対象 camera を指定します。空欄では各 instance の直近 camera の状態を使います。診断 readback は GPU 同期を含むため既定で無効で、有効時も「診断を更新」を押したときだけ取得します。panel を閉じると診断を無効に戻します。通常性能の測定中は有効にしません。

Triangles は描画中の三角形ごとに色を変えます。debug buffer を使わず、近づくと三角形が細かくなる様子を確認できます。色は compaction 後の三角形番号から作るため、cut が変わると色の割り当ても変わります。Clusters / LodLevel / ScreenError / ResidentPages は実際の選択結果から色を生成します。LodLevel は、Level が上がる(粗くなる)ほど青→緑→黄→赤へ連続して変わります(12 段以上は赤)。Clusters の塊の大きさは、画面上の誤差を一定に保つ階層が選ばれるため距離で変わりにくいので、LOD の変化は LodLevel の色と、VG Stats の visible clusters / drawn triangles(「自動更新」で 0.5 秒ごとに更新)で見てください。Loading は queued + loading + uploading の値を使い、Culling は選択・棄却した bounds を使います。未描画の page を resident の固定色として扱いません。debug buffer を必要とする mode では通常時より追加メモリを使います。

入力 source triangles と unique leaf triangles は asset 単位、instance triangles は配置数込み、drawn triangles は実際の indirect arguments から数えます。geometry / metadata / CPU cache は共有資源単位、選択 buffer は instance × camera で合計します。nonempty draws、considered / frustum / HZB / visible、I/O、miss、queue overflow、quality target miss、eviction を別に表示します。

CPU/GPU 時間は有効化した VG marker の直前 frame 合計です。取得できない GPU 値は「未取得」です。Editor キャプチャや複数 camera を含む診断値を Player の通常 frame time と比較しません。quality target miss は非同期 feedback の観測値であり、現在の CPU 描画フレームと同時刻とは限りません。

## Monitor window

`Tools > Virtualized Geometry > Monitor`、または toolbar の `Show` から開きます。Game view を覆わず、対象 camera(空欄では Main Camera)について instance ごとの選択状態を約 0.1 秒ごとに更新します。

- 専用描画 / Proxy(理由)。Proxy 表示は camera ごとに切り替わるため、対象 camera の直近判定を表示します。
- drawn triangles / leaf triangles、選択 node 数、Level 別の選択 node 数(Level 0 が最詳細 leaf)。
- cut 変化 /s: 選択 node 集合が直前の取得から変わった回数。静止 camera で 0 以外が続く場合は LOD の振動(点滅)です。
- considered / frustum / HZB / visible と、可視 cluster があるのに描画 0 の場合の警告。
- streaming の resident / queued / loading / uploading、eviction /s、quality target miss。page 系の値は共有資源単位です。

値は `VirtualGeometrySelectionMonitor` が AsyncGPUReadback で非同期に取得し、VG Stats の診断 readback とは異なり GPU 同期を入れません。表示は数 frame 遅れ、Edit Mode では対象 camera が描画されたときだけ更新されます。
