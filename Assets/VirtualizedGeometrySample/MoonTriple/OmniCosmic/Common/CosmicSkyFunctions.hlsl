// CosmicScale の背景の空（手続き生成、画像なし）。スカイボックス CosmicSky とブラックホールの重力レンズの背景が同じ関数を使います。
// 星は方向の3次元格子のセルごとに1個まで置き、画素の角度の大きさで描くので、画面の解像度で鋭く見えます（既存の 1024x512 の画像はぼけるため使いません）。
// _CosmicSky.x（星）、y（遠方の銀河の場）で、天の川の中 → 銀河の外 → 宇宙地図（黒）へ切り替えます。
#ifndef COSMIC_SKY_FUNCTIONS_INCLUDED
#define COSMIC_SKY_FUNCTIONS_INCLUDED

// 1層分の星。density はセルの細かさ（方向1あたり）、probability はセルに星がある確率。
float3 CosmicStarLayer(float3 d, float density, float probability, float pixelAngle, int layer)
{
    float3 p = d * density;
    int3 cell = (int3)floor(p);
    float3 h = CosmicHash3(cell + int3(layer * 131, 7, 3));
    if (h.x > probability) return 0;
    float3 h2 = CosmicHash3(cell + int3(19, layer * 37, 101));
    float3 star = normalize((float3(cell) + 0.5 + (h2 - 0.5) * 0.4) / density);
    float distance = length(d - star);
    float size = max(pixelAngle * 0.75, 1e-6);
    float core = exp(-distance * distance / (size * size));
    float magnitude = pow(h.y, 10.0) * 4.0 + 0.04;
    // 星の色温度（3000〜12000 K）。
    float3 color = CosmicBlackbody(lerp(3200.0, 11000.0, h.z * h.z));
    return color * core * magnitude;
}

float3 CosmicSkyColor(float3 d, float pixelAngle)
{
    float stars = _CosmicSky.x, field = _CosmicSky.y;
    float3 c = 0;
    if (stars > 0.001)
    {
        c += CosmicStarLayer(d, 40.0, 0.35, pixelAngle, 0) * 1.4;
        c += CosmicStarLayer(d, 120.0, 0.16, pixelAngle, 1) * 0.6;
        c += CosmicStarLayer(d, 360.0, 0.035, pixelAngle, 2) * 0.35;
        // 天の川の帯（銀河の中から見た円盤）。
        float3 galacticNorth = normalize(float3(0.25, 0.92, 0.3));
        float band = exp(-pow(dot(d, galacticNorth) / 0.16, 2.0));
        float clouds = 0.5 + 0.5 * CosmicFbm(d * 7.0, 5, pixelAngle * 7.0, 2.0, 0.55);
        float dust = smoothstep(0.35, 0.75, CosmicFbm(d * 11.0 + 5.0, 4, pixelAngle * 11.0, 2.0, 0.5) * 0.5 + 0.5);
        c += float3(0.050, 0.044, 0.038) * band * clouds * (1.0 - 0.7 * dust);
        c *= stars;
    }
    if (field > 0.001)
    {
        // 銀河の外: 小さく淡い銀河の場。
        float3 p = d * 90.0;
        int3 cell = (int3)floor(p);
        float3 h = CosmicHash3(cell + int3(5, 9, 77));
        if (h.x < 0.06)
        {
            float3 g = normalize((float3(cell) + 0.5 + (CosmicHash3(cell + int3(8, 1, 2)) - 0.5) * 0.4) / 90.0);
            float3 offset = d - g;
            float3 axis = normalize(CosmicHash3(cell + int3(3, 3, 3)) - 0.5);
            float along = dot(offset, axis), across = length(offset - axis * along);
            float size = max(pixelAngle * 1.2, 0.0015 * (0.4 + h.y));
            float blob = exp(-(along * along / (size * size * 2.5) + across * across / (size * size * 0.6)));
            c += lerp(float3(1.0, 0.85, 0.65), float3(0.7, 0.8, 1.0), h.z) * blob * 0.22 * field;
        }
    }
    return c;
}

#endif
