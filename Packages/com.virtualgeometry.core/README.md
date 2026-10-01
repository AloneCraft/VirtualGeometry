# Virtualized Geometry

Unity 6000.6 / URP 17.6 / Windows DX12 向けの独立した仮想化ジオメトリ実装です。Epic の公式製品・互換実装ではありません。M0〜M5 の必須範囲を実装・検証した v0.1（package 0.1.0）です。1M/3M/10M × 通常 MeshRenderer / LODGroup / Virtualized Geometry の CPU/GPU benchmark は各 6,480 samples、GPU 欠測 0 です。対応条件と品質・性能の制限を下記の文書に記録しています。

Mesh を境界保持階層と独立 page に変換し、GPU で画面誤差・frustum・現フレーム HZB を使って cluster を選択します。固定 geometry pool、非同期 I/O、転送上限の中で親表現を保持します。低密度 Proxy が初期読込、互換外設定、影、Collider、Editor picking を担当します。

- Project で Mesh を右クリック →「仮想ジオメトリに変換」で変換し、生成した asset の Inspector で再変換・配置します。Hierarchy の GameObject を右クリック →「仮想ジオメトリに置き換え」なら変換と配置を一度に行います。
- Scene view の overlay toolbar「Virtual Geometry」で debug view・カリング切替・実測統計(VG Stats)を操作します。camera ごとの選択状態は `Tools > Virtualized Geometry > Monitor` で確認します。
- 専用描画には Universal Renderer の `VirtualGeometryRenderFeature` が必要です。セットアップ手順は `Samples~/Practical/README.md` を参照してください。
- Player の archive フィルタは `VirtualGeometryPlayerBuild.Build` から実行します。

対応条件は [SupportMatrix](Documentation~/SupportMatrix.md)、変換は [Conversion](Documentation~/Conversion.md)、描画は [Rendering](Documentation~/Rendering.md)、ストリーミングは [Streaming](Documentation~/Streaming.md)、Proxy は [Proxy](Documentation~/Proxy.md)、検証状況は [Development](Documentation~/Development.md)、要件との対応は [Traceability](Documentation~/Traceability.md) に記載します。性能優位は保証せず、品質目標未達と取得できない計測値を区別します。