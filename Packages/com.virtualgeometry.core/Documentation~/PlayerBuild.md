# Windows Player ビルド

対象は Unity 6000.6.0f1、URP 17.6、Windows x64 / DX12。Editor 専用の変換 DLL を Player は使用しない。変換した v3 archive を、build 時に収録した StreamingAssets の実ファイルから読み込む。

## 収録境界

- 通常の File > Build でも `BuildPipeline.BuildPlayer` でも、`VirtualGeometryPlayerBuildProcessor`(`BuildPlayerProcessor.PrepareForBuild`)が働きます。
- 収録対象を決める手順:
  1. build 対象の scene と、Assets 内 Resources の依存関係から、変換データ(`VirtualGeometryMeshAsset`、モデルのサブアセット)を集めます。
  2. その page を重複排除します。
  3. Editor の変換キャッシュ `Library/VirtualGeometry` から、`BuildPlayerContext.AddAdditionalPathToStreamingAssets` で `StreamingAssets/VirtualGeometry/` の同じ相対パスへ収録します。
- 参照されない page は収録しません。
- page が変換キャッシュに無い場合は build を失敗させます。その場合は元のモデルを再インポートしてください。
- 動的な Addressables / AssetBundle だけから参照する変換データの自動収録は対象外で、専用の追加収録設計が必要です。
- 変換したモデルのインポート結果では、MeshFilter が低密度の代替 Mesh を参照します。そのため、高密度の元 Mesh は、別の script・通常 renderer・Resources などから参照しない限り収録されません。

## 再現する検証

Editor を DX12 batchmode で起動し、`VirtualizedGeometry.Editor.M5PlayerBuildValidation.Run` を実行する。M5 の共有モデル(`M5/Shared.obj`、Virtual Geometry 有効)から `M5Player.unity` を作り、`Builds/M5Player/VirtualGeometryM5.exe` を Development build と DetailedBuildReport 付きで生成する。当たり判定に代替 Mesh を使うため、利用者と同じくモデルの Import Settings で三角形の衝突形状を事前ベイクに設定し、build の警告 0 件を確認する。保存前に、各 GameObject の MeshFilter が代替 Mesh を持つことも確かめる。scene は Editor で開いてそのまま動く既定の設定で保存し、検証用の小さい予算・読込遅延・LOD 閾値 0 は runner(`VirtualGeometryPlayerValidation`)が Player の起動時に両 instance へ適用する。Editor で再生したときは検証しない。

続いて Player を `-force-d3d12 -vgResults <絶対出力フォルダ>` で実行する。bootstrap Proxy と root 公開、2 instance の資源共有、URP Lit 比較、遅延 I/O と chunk transfer、LOD 改善、forced unsupported fallback、2 camera の選択状態、3 回の enable/disable を検証して終了する。

`TestResults/M5/PlayerBuild` は収録検査、指定した Player 出力フォルダは Results JSON/TXT・Metrics CSV・Environment・PNG の保存先。同期 readback と画像保存を含む機能検証なので、そのフレーム時間を通常性能と比較しない。build/run の PASS は実際の Results によって判定する。