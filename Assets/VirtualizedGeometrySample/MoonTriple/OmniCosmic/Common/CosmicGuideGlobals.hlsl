// CosmicScale の画像のガイドの星雲の global（CosmicVolumeAtlas が設定）。星雲の体積（CosmicVolumeShading.hlsl）と星（CosmicNebulaStars.hlsl）で共有します。
#ifndef COSMIC_GUIDE_GLOBALS_INCLUDED
#define COSMIC_GUIDE_GLOBALS_INCLUDED

// 星雲のスラブごとに 3 行: 画像の x 軸、y 軸、奥行き（主役のカメラは −z の側）。q（単位半径）の座標。
// w は主役のときのカメラの位置（画像の面の座標、見た目の半径 = 1）の x・y・z。
float4 _CosmicGuideBasis[48];
// 星雲のスラブごと: x = 形の模型の番号（CosmicNebulaModels.hlsl、0 は模型なし）、y = _CosmicGuideColumn の層の番号、
// z = 星の数（_CosmicNebulaStars）。
float4 _CosmicGuideModel[16];
// 1 = guide の配列がある（面で描く）。0 = 焼いた体積で描く。
float _CosmicGuideCards;
// 星雲の星（CosmicVolumeAtlas.WriteStars）。スライス = スラブ、星 k の 2 つの画素は (2 (k mod n) + j, k / n)、n = _CosmicNebulaStarsInfo.x。
// j = 0: (px, py, 奥行き z, 星ごとの一様乱数)。px, py = ガイドの面の座標（-1..1）、z = 画像の面の座標の奥行き（|z| > 1.5 なら模型の密度から選ぶ）。
// j = 1: (r, g, b, 0) = 光の総量（ガイドの RGB × ガイドの面の面積。面全体 = 4）。
TEXTURE2D_ARRAY(_CosmicNebulaStars);
float4 _CosmicNebulaStarsInfo;

#endif
