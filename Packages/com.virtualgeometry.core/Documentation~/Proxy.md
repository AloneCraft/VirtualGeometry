# 低密度 Proxy

変換した階層のうち、root から単一子の段を降りた「group 束ねで 1 node へまとまった段」から小さな Mesh subasset を生成します。最粗の root(1 cluster 以下)は影・collider・fallback 表示には粗すぎるため使いません。Player は巨大な変換元 Mesh を必要としません。Inspector に source / leaf / Proxy triangles と root geometric error を表示します。この error は階層の簡略化誤差で、全視点の pixel 誤差保証ではありません。

同じ Proxy を通常可視 fallback、ShadowsOnly renderer、MeshCollider、Editor ray picking に使います。Proxy は Renderer の Materials をそのまま使い、影は Material に ShadowCaster pass があればそれ、無ければ(URP Unlit 等)白の URP Lit で落とします。専用 geometry が root の転送完了前や読込拒否時には通常 Proxy を表示し、対応 camera の root 公開後に排他的に切り替えます。backend override で実 GPU 上でも fallback を再現できます。

影は低密度 Proxy から通常 URP object へ落とします。VG 本体が通常 object の main light shadow を受ける検証は別に行っています。Collider / picking は Proxy の近似形状を使うため、高密度 detail と衝突位置が一致する保証はありません。

Game / Scene camera の描画直前に互換性を判定します。対応 feature のない camera も通常 Proxy を保持します。root 読込中、欠損 archive、予算拒否の理由は Renderer の Inspector、VG Stats、Monitor window へ表示します。renderer の disable / destruction で instance の選択状態を破棄し、最後の共有 lease で geometry と I/O を解放します。
Inspector の serialized 更新では OnValidate は再構築要求だけを立て、次の Update で Proxy と資源を更新します。OnValidate 内で GameObject を破棄・生成しません。Undo / reload 後も直接子の所有 marker を回収し、以前の Proxy が残って二重表示されないことを検証します。
