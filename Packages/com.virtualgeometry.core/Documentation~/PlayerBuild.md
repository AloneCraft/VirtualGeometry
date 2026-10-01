# Windows Player ビルド

対象は Unity 6000.6.0f1、URP 17.6、Windows x64 / DX12。Editor 専用の変換 DLL を Player は使用しない。変換した v3 archive を実ファイルとして StreamingAssets から読み込む。

## 収録境界

`VirtualGeometryPlayerBuild.Build(BuildPlayerOptions)` をビルド入口として使う。指定 scene と Assets 内 Resources の依存関係から `VirtualGeometryMeshAsset` を集め、archive path を重複排除する。動的な Addressables / AssetBundle だけから参照する archive の自動収録は対象外で、専用の追加収録設計が必要。

ビルド中は `Assets/StreamingAssets/VirtualGeometry` とその folder .meta を `Library/VirtualGeometryBuildArchives` へ退避し、Unity の `BuildPlayerContext.AddAdditionalPathToStreamingAssets` で必要な archive だけを元の相対パスへ収録する。`using` / `finally` で成功・失敗の両方から原本を復元する。Editor の自動 refresh はこの区間だけ抑止する。Editor が終了した場合は次の起動時に固定パスから復元する。原本と staging の両方が存在する異常状態では上書きせず停止する。

通常の `BuildPipeline.BuildPlayer` を直接呼ぶとこの archive フィルタは適用されない。配布する Player では必ず専用入口を使い、BuildReport と出力 StreamingAssets を確認する。

ビルド用 scene clone の処理では、専用 renderer と同じ GameObject にある無効化済み通常 MeshRenderer / MeshFilter を、source GUID と local file ID が変換元に一致する場合だけ除去する。Editor の scene と元 Mesh asset は変更しない。任意の script、有効な通常 renderer、Resources などに別途残した source 参照は自動削除しない。

## 再現する検証

Editor を DX12 batchmode で起動し、`VirtualizedGeometry.Editor.M5PlayerBuildValidation.Run` を実行する。M5 の既存変換 fixture から `M5Player.unity` を作り、`Builds/M5Player/VirtualGeometryM5.exe` を Development build と DetailedBuildReport 付きで生成する。

続いて Player を `-force-d3d12 -vgResults <絶対出力フォルダ>` で実行する。bootstrap Proxy と root 公開、2 instance の資源共有、URP Lit 比較、遅延 I/O と chunk transfer、LOD 改善、forced unsupported fallback、2 camera の選択状態、3 回の enable/disable を検証して終了する。

`TestResults/M5/PlayerBuild` は収録検査、指定した Player 出力フォルダは Results JSON/TXT・Metrics CSV・Environment・PNG の保存先。同期 readback と画像保存を含む機能検証なので、そのフレーム時間を通常性能と比較しない。build/run の PASS は実際の Results によって判定する。