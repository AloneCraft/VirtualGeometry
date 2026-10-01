# 低密度 Proxy(代替 Mesh)

変換した階層のうち、root から単一子の段を降りた「group 束ねで 1 node へまとまった段」から小さな Mesh subasset を生成します。最粗の root(1 cluster 以下)は影・collider・fallback 表示には粗すぎるため使いません。Player は巨大な変換元 Mesh を必要としません。Inspector に source / leaf / Proxy triangles と root geometric error を表示します。この error は階層の簡略化誤差で、全視点の pixel 誤差保証ではありません。

Proxy は VG で描く GameObject の MeshFilter に入り(UE5 の Fallback Mesh 相当)、通常の MeshRenderer がそのまま代替表示・影・カリングを受け持ちます。当たり判定が必要なら同じ Mesh を MeshCollider に使います。モデルの Import Settings にある衝突形状の事前ベイク(Pre-bake Collision、三角形 / convex)は代替 Mesh にも引き継ぐので、Player build で「Pre-baked collision is disabled」の警告が出たら、通常のモデルと同じくモデル側で有効にします。VG が描く camera では MeshRenderer の描画を保存されない `forceRenderingOff` で止め、影は MeshRenderer の影設定が有効なときだけ非表示の影専用の子(ShadowsOnly)が Proxy から落とします。影は Material に ShadowCaster pass があればそれ、無ければ(URP Unlit 等)白の URP Lit で落とします。root の転送完了前や読込拒否時、VG が描けない camera では MeshRenderer が通常どおり表示し、対応 camera の root 公開後に排他的に切り替えます。MeshRenderer を無効にすると VG も描きません。backend override で実 GPU 上でも fallback を再現できます。

影は低密度 Proxy から通常 URP object へ落とします。VG 本体が通常 object の main light shadow を受ける検証は別に行っています。Collider / picking は Proxy の近似形状を使うため、高密度 detail と衝突位置が一致する保証はありません。小さな部品が集まった mesh では、粗い段で小さな部品を取り除くため([変換](Conversion.md))、Proxy・影・collider にもその部品は含まれません。

Game / Scene camera の描画直前に互換性を判定します。対応 feature のない camera も通常 Proxy を保持します。root 読込中、欠損 archive、予算拒否の理由は GameObject の Inspector の「Virtual Geometry」欄、VG Stats、Monitor window へ表示します。renderer の disable / destruction で instance の選択状態を破棄し、最後の共有 lease で geometry と I/O を解放します。
Inspector の serialized 更新では OnValidate は再構築要求だけを立て、次の Update で Proxy と資源を更新します。OnValidate 内で GameObject を破棄・生成しません。Undo / reload 後も直接子の所有 marker を回収し、以前の影専用の子が残って二重に影を落とさないことを検証します。VG の部品を止めたときは MeshRenderer の描画停止を必ず解除します。script から変換データを外した(`SetMeshAsset(null)`)ときは、部品が MeshFilter へ入れた代替 Mesh も外し、古い形が通常の Mesh として見え続けないようにします。利用者が後から入れた Mesh は外しません。
