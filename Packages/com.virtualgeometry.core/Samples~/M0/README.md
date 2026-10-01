# M0検証サンプル

Unityで `Tools > Virtualized Geometry > Create M0 Validation Scene` を実行する。
`Assets/VirtualizedGeometrySample/M0.unity`、専用のForward RendererとPipelineを生成する。
生成済みの同名パスが存在する場合は再利用する。

M0はバッファ由来の三角形とGPU生成間接引数を検証する専用サンプル。
Mesh変換、LOD、カリング、ストリーミングの完成例ではない。
