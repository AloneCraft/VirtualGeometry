# 変換

## 入口

- Project でモデル(FBX・OBJ・DAE・3DS・DXF・.blend など、Unity 標準の ModelImporter が読む形式)を選び、Inspector 上部の「Virtual Geometry」にチェックを入れて Apply します。
- Apply するとモデルが再インポートされ、MeshRenderer を持つ各 Mesh がクラスタ化・LOD 化されます。変換データ(`<Mesh名> (Virtual Geometry)`)はモデルのサブアセットになります。
- インポート結果の GameObject は通常の MeshFilter + MeshRenderer のままです。MeshFilter には低密度の代替 Mesh(UE5 の Fallback Mesh 相当)が入り、VG とつなぐ部品が非表示で付きます。Material・影の有無は MeshRenderer で設定します。モデルを Scene へドラッグすればそのまま VG で描画されます。
- 元の高密度 Mesh もサブアセットとして残ります。MeshFilter は代替 Mesh を参照するため、元 Mesh は別に参照しない限り Player へ収録されません。`MeshFilter.sharedMesh` を読む script には低密度の形が返ります。
- 元のモデルファイル・Import 設定・Virtual Geometry 設定のどれかが変わると、Unity がモデルを再インポートし、変換データも作り直されます。変換器の版を上げた場合も全モデルが再インポートされるため、古い変換結果は残りません。
- Read/Write が無効なモデルもそのまま変換します(インポート中の Mesh を read-only snapshot で読みます)。Import 設定は変更しません。
- チェックを外して Apply すると、変換データと VG の部品が消え、MeshFilter は元の Mesh に戻ります。
- 対象外:
  - glTF・USD など、パッケージ独自のインポーター(ScriptedImporter)で読むモデル。Inspector に非対応である旨を表示します。
  - SkinnedMeshRenderer の Mesh(VG は静的 geometry 用)。
  - `.asset` として保存した Mesh。モデル形式で書き出してからインポートしてください。
- 変換できなかった Mesh は元の Mesh のまま残り、理由がインポートの warning に出ます。
- モデルの userData を他のツールが使っている場合は、内容を壊さないよう設定できない旨を表示します。

## 設定(モデルの Inspector)

| 項目 | 意味 |
|---|---|
| Virtual Geometry | 有効にすると、インポートのたびに変換し、MeshFilter を代替 Mesh にして VG の部品を付けます。 |
| 詳細: クラスタの三角形数 | 1 cluster の三角形数(1〜128、既定 64)。最も粗い段もこの数以下になるまで段を作ります。 |
| 詳細: 親が束ねる数 | 親 1 つに束ねる子の数(2〜8、既定 4)。 |
| 詳細: 1段ごとの三角形の割合 | 1 段ごとの三角形数の倍率(0.1〜0.9、既定 0.5)。 |
| 詳細: 位置の精度 (bits) | 頂点座標をモデル全体の格子へ丸める精度(8〜24、0 で無効、既定 16)。 |
| Apply / Revert | 変更があるときだけ表示します。Apply で userData に保存して再インポートします。 |
| 状態 | 変換済み Mesh ごとの LOD 段数、最も細かい段と代替表示(Proxy)の三角形数。 |

設定は `ModelImporter.userData` に `{"virtualGeometry":{...}}` として保存します。複数のモデルを選択すると同じ設定をまとめて適用できます。

変換データ(サブアセット)の Inspector は読み取り専用です。LOD 段数・三角形数・形状データ量の要約と、「元のモデルを選択」ボタンだけを表示します。

## 階層

三角形は全軸を最大辺長で正規化した Morton 順に並べます(軸ごとに正規化すると、波状平面の高さのような薄い軸が他軸と同じ重みに伸び、cluster と group が高さ帯で空間的に散らばって、group 外周の固定が増え親の誤差が段で跳ねるため)。leaf cluster をその順に fanout 個ずつ束ね、各 group の外周を固定して簡略化した親を作ります。1 node へまとまった後も、root が 1 cluster(`MaxTrianglesPerCluster`)以下になるまで単一子の段を重ねます(Nanite の最粗 cluster 相当)。この段には隣接 group がないため、group 境界も mesh の開いた縁も固定しません。別 material と共有する位置だけは固定し、material 間の継ぎ目に crack を作りません。縁の変化は簡略化誤差として親の geometric error に加算します。group の外周と mesh の開いた縁は、辺を頂点番号ではなく位置で数えて固定します。法線・UV の継ぎ目で分かれた頂点は縁とみなさず、継ぎ目の形は meshoptimizer が保ちます。小さな閉じた部品が集まった mesh(ボルトや板の集合)は、形を保つ簡略化では部品を消せず三角形が減りません。そのため目標数に届かないときは、固定頂点を持たない部品(group の内側で完結する部品)を小さい順に取り除き、その大きさ(包む箱の対角の半分)を誤差に加えます。取り除くのは、その大きさが画面上の許容誤差より小さくなる距離からです。meshoptimizer の Prune は固定頂点を守らず group 境界に crack を作るため使いません。開いた縁を持つ部品(板 1 枚の decal など)は縁が固定されるため取り除きません。node の境界球は子の球をすべて包む最小寄りの球で、親の球は常に子の球と自分の表現を包みます。簡略化が進まず 1 cluster を超えたまま止まった場合は VG2004 を出します。

例: Practical sample の 2,048 triangles 波状平面(8×8、高さ ±0.4、cluster 64、fanout 4、簡略化率 0.5、画面高 590px・目標 2px)。

| | leaf 半径 | L1→L2 誤差比 | root 半径 | 選択が切り替わる距離 |
|---|---|---|---|---|
| builder 6 | 最大 4.15 | 23 倍 | 24.3 | L0〜11、L1〜181、以降 L2〜L6 が 181〜511 に集中 |
| builder 7 | 最大 1.14 | 3.4 倍 | 6.5 | L0〜6.4、L1〜22、L2〜65、L3〜107、L4〜199、L5(root 63 tri)以降 |

## 生成と保存

- 変換はモデルのインポート中に同期で行います(`VirtualGeometryModelPostprocessor`)。同じ段の group の簡略化と、streaming archive の page ごとの符号化・CRC は並列に行い、出力は逐次処理と同一です。CRC は表引き(slicing-by-8)で計算します。Unity 外の .NET 8 での計測では、10M triangles の変換と書き出しが約 45 秒から約 12 秒になりました(Unity の Editor 上ではこれより遅く、OBJ などの読込時間も加わります)。
- GPU 階層 LOD と v3 独立 streaming page を常に生成します。page は node representation 単位で原子的に作り、全 root を page 0 にまとめます。
- page archive は Editor の変換キャッシュ `Library/VirtualGeometry/<内容の Hash128>.vgpage` に置きます。名前が内容の hash なので、同じ内容は書き直しません。Unity の Mono 上では SHA-256 が数百 MB の page で数十秒かかるため、Unity の native な 128 bit hash(`Hash128.Compute`)を使います。書き込みは一時ファイルを経由し、壊れた page を残しません。
- Editor 起動時に変換キャッシュを点検します。Library の復元などで page が欠けたモデルは再インポートし、どの変換データからも参照されない page は削除します。
- Player build では、参照される page だけを StreamingAssets へ収録します([PlayerBuild](PlayerBuild.md))。
- 配布形式は v3 で、v1/v2 は全常駐の互換読込だけを残します。階層なしの旧形式(v2)は、検証 API の `VirtualGeometryBuildSettings` からだけ生成できます。
- builder の版:
  - builder 5: simplify 入力を、group が実際に参照する頂点へ局所化。
  - builder 6: 上記の単一子の段を追加。
  - builder 7: Morton 正規化と境界球を修正。
  - builder 8: 継ぎ目を縁と判定しない境界固定と、group 内で完結する小さな部品の除去。Blender で作った space_elevator.fbx の 28 Mesh で、root の三角形の合計が 92,911 から 4,177 になりました(AnchorTower は 2,606 個の小さな箱の集まりで 32,890 から 260、page の固定枠は 2.46 MB から 64 KB)。builder 7 までは、継ぎ目を縁と誤って固定していたことと部品を消せなかったことで、こうした mesh の粗い段の三角形がほとんど減りませんでした。

## 配置

- 変換したモデルを Scene へドラッグすると、MeshRenderer のまま VG で描く GameObject が配置されます。
- 変換データ(サブアセット)を Scene view か Hierarchy へドラッグすると、代替 Mesh の MeshFilter・MeshRenderer と VG の部品を持つ GameObject を新しく作ります。
  - 置く位置: Scene view ではドロップ位置。Hierarchy では、項目の上なら子、項目の間ならその直後。
  - Material は未設定(既定の白い URP Lit)です。
- どちらも Undo 1 回で戻せます。
- 通常 Proxy 表示と専用描画の切替は、Scene view toolbar の `Show` で行います。
