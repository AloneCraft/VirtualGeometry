# Virtualized Geometry

Unity 6000.6 / URP 17.6 / Windows DX12 向けの独立した仮想化ジオメトリ実装です。Epic の公式製品・互換実装ではありません。M0〜M5 の必須範囲を実装・検証した v0.1（package 0.1.0）です。1M/3M/10M × 通常 MeshRenderer / LODGroup / Virtualized Geometry の CPU/GPU benchmark は各 6,480 samples、GPU 欠測 0 です。対応条件と品質・性能の制限を下記の文書に記録しています。

Mesh を境界保持階層と独立 page に変換し、GPU で画面誤差・frustum・現フレーム HZB を使って cluster を選択します。固定 geometry pool、非同期 I/O、転送上限の中で親表現を保持します。低密度 Proxy が初期読込、互換外設定、影、Collider、Editor picking を担当します。

- Project でモデル(FBX・OBJ など Unity 標準の形式)を選び、Inspector 上部の「Virtual Geometry」にチェックを入れて Apply します。インポートのたびに自動でクラスタ化・LOD 化されます。GameObject は通常の MeshFilter + MeshRenderer のままで、Material・影は MeshRenderer で設定します(UE5 の Nanite と同じく、MeshFilter には影・当たり判定・代替表示用の低密度 Mesh が入ります)。モデルを Scene へドラッグすればそのまま VG で描画されます。
- Scene view の overlay toolbar「Virtual Geometry」で debug view・カリング切替・実測統計(VG Stats)を操作します。camera ごとの選択状態は `Tools > Virtualized Geometry > Monitor` で確認します。
- 専用描画には Universal Renderer の `VirtualGeometryRenderFeature` が必要です。セットアップ手順は `Samples~/Practical/README.md` を参照してください。
- Player build では、参照される変換データの page だけが自動で収録されます(通常の File > Build で可)。

対応条件は [SupportMatrix](Documentation~/SupportMatrix.md)、変換は [Conversion](Documentation~/Conversion.md)、描画は [Rendering](Documentation~/Rendering.md)、ストリーミングは [Streaming](Documentation~/Streaming.md)、Proxy は [Proxy](Documentation~/Proxy.md)、検証状況は [Development](Documentation~/Development.md)、要件との対応は [Traceability](Documentation~/Traceability.md) に記載します。性能優位は保証せず、品質目標未達と取得できない計測値を区別します。