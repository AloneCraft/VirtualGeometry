# 変換

## 入口

- Project で Mesh、または MeshFilter を含む model を右クリックし「仮想ジオメトリに変換」を選びます。元 asset と同じフォルダに `<Mesh名>_VG.asset`(model 内の Mesh は `<model名>_<Mesh名>_VG.asset`)を既定設定で生成・更新します。変換は Background Tasks に進捗を出し、そこから Cancel できます。VG は静的 geometry 用のため SkinnedMeshRenderer の Mesh は対象外です。
- Hierarchy で MeshRenderer を持つ GameObject を右クリックし「仮想ジオメトリに置き換え」を選ぶと、同じ変換の完了後にその GameObject へ Virtual Geometry Renderer を付けます。Material は MeshRenderer から引き継ぎ、元の MeshRenderer は無効化します(Undo 1 回で戻せます)。
- Read/Write が無効な model の Mesh は、確認ダイアログで同意した場合だけ model の Import 設定で Read/Write を有効化して再インポートします。geometry は変更しません。
- `VirtualGeometryMeshAsset` の Inspector は変換結果を読み取り専用で表示し、変換・再変換・キャンセル・配置をまとめます(下記)。空の asset を作る Create menu と、独立した Converter window はありません。

## 階層

三角形は全軸を最大辺長で正規化した Morton 順に並べます(軸ごとに正規化すると、波状平面の高さのような薄い軸が他軸と同じ重みに伸び、cluster と group が高さ帯で空間的に散らばって、group 外周の固定が増え親の誤差が段で跳ねるため)。leaf cluster をその順に fanout 個ずつ束ね、各 group の外周を固定して簡略化した親を作ります。1 node へまとまった後も、root が 1 cluster(`MaxTrianglesPerCluster`)以下になるまで単一子の段を重ねます(Nanite の最粗 cluster 相当)。この段には隣接 group がないため、group 境界も mesh の開いた縁も固定しません。別 material と共有する位置だけは固定し、material 間の継ぎ目に crack を作りません。縁の変化は簡略化誤差として親の geometric error に加算します。node の境界球は子の球をすべて包む最小寄りの球で、親の球は常に子の球と自分の表現を包みます。簡略化が進まず 1 cluster を超えたまま止まった場合は VG2004 を出します。

例: Practical sample の 2,048 triangles 波状平面(8×8、高さ ±0.4、cluster 64、fanout 4、簡略化率 0.5、画面高 590px・目標 2px)。

| | leaf 半径 | L1→L2 誤差比 | root 半径 | 選択が切り替わる距離 |
|---|---|---|---|---|
| builder 6 | 最大 4.15 | 23 倍 | 24.3 | L0〜11、L1〜181、以降 L2〜L6 が 181〜511 に集中 |
| builder 7 | 最大 1.14 | 3.4 倍 | 6.5 | L0〜6.4、L1〜22、L2〜65、L3〜107、L4〜199、L5(root 63 tri)以降 |

## Mesh Asset Inspector

| 項目 | 意味 |
|---|---|
| 情報欄 | 入力 / leaf triangles、cluster・node・material 数、page 数と geometry bytes、Proxy triangles と誤差、形式と builder version。旧 builder の結果なら再変換を促します。 |
| 元 Mesh | 変換元。変換済み asset では変換時の Mesh を sourceGuid / localId から解決して表示します。元 Mesh は変更しません。 |
| このアセットへ変換 / 再変換 | 元 Mesh と詳細の設定で生成し、この asset を更新します(常に強制生成)。 |
| キャンセル / 進捗 | 実行中の変換を中止します。公開前の途中結果は残りません。 |
| 選択GameObjectに配置 | 選択中の GameObject に Virtual Geometry Renderer を付けて割り当てます。選択が無ければ新規作成し、元 Mesh を表示中の MeshRenderer を無効化します。Undo 1 回で戻せます。 |
| 詳細: Cluster最大triangles | 1 cluster の三角形数(1〜128、既定 64)。root もこの数以下になるまで段を作ります。 |
| 詳細: 階層fanout | 親 1 つに束ねる子の数(2〜8、既定 4)。 |
| 詳細: 親の目標簡略化率 | 1 段ごとの三角形数の倍率(0.1〜0.9、既定 0.5)。 |
| 詳細: 位置量子化 bits | 頂点座標を mesh 全体の格子へ丸める精度(8〜24、0 で無効、既定 16)。 |
| 詳細: 参照page cacheを削除 | この asset の archive を削除します。次回ロードは Proxy となり、次の変換で再生成します。 |

GPU 階層 LOD と v3 独立 streaming page は常に生成します。旧形式(階層なし、v2)は検証 API の `VirtualGeometryBuildSettings` からだけ生成できます。page は node representation 単位で原子的に作り、全 root を page 0 にまとめます。

main thread で Unity Mesh を snapshot し、background worker が build・encode・hash・一時ファイルを生成します。Cancel は worker の CancellationToken へ伝播します。公開 asset と archive の commit は main thread で行い、失敗時には前の公開状態へ戻します。native の単一 simplify 呼出し、snapshot、main thread commit 自体は分割されません。

右クリック変換は source GUID / local file ID / dependency hash / settings / builder version / content hash が一致し、archive の検証にも成功した場合だけ cache を再利用します。Inspector の変換・再変換は強制生成します。builder 5 は simplify 入力を group が実際に参照する頂点へ局所化し、builder 6 は上記の単一子の段を追加し、builder 7 は Morton 正規化と境界球を修正します。builder 6 以前の asset は cache 不一致として再変換されます。配布形式は v3、v1/v2 は全常駐互換読込です。

配置は Undo に対応し、既存 source MeshRenderer の無効化も復元できます。元 Mesh asset を変更しません。通常 Proxy 表示と専用描画の切替は Scene view toolbar の `Show` で行います。

294,912 triangles の旧 Converter window での UI 検証では変換中の Editor update 継続、21.25% 時点での cancel、137.064 ms の取消待ち、partial asset/page/staging 0 を観測しました。Cancel の時間上限を全入力へ保証する値ではありません。