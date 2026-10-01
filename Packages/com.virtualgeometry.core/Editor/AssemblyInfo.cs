using System.Runtime.CompilerServices;

// インポート処理(OBJ書き出し・モデル設定)を外部公開せずにEditModeテストから検証する。
[assembly:InternalsVisibleTo("VirtualizedGeometry.Tests.Editor")]
