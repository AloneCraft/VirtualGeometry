// CosmicScale のシェーダー共通部: global（CosmicScaleDirector が毎フレーム書く）、材質の定数（全シェーダー・全パスで同一の UnityPerMaterial）、
// 手続き生成の部品（CosmicNoise.hlsl）。keyword は使いません。
#ifndef COSMIC_COMMON_INCLUDED
#define COSMIC_COMMON_INCLUDED

#define COSMIC_MAX_BODIES 64

#include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicNoise.hlsl"

// x = 表示の重み（0 で消す）、y = 発光の倍率（比較の演出）。添字は材質の _CosmicBody.x（梯子の段の番号）。
float4 _CosmicState[COSMIC_MAX_BODIES];
// x = 経過秒（周期で畳んだ値）、y = 分。
float4 _CosmicTime;
// xyz = 惑星を照らす演出用の主光源の向き（天体から光源へ、Logical の世界軸）、w = 強さ。
float4 _CosmicKeyLight;
// MoonTriple が追加: ボールの点光源。xyz = ボールの中心の、カメラからの相対位置(Logical の世界軸、m)、w = 強さ(0 で消す)。MoonTripleDemo が毎フレーム書く。
// 面の点との距離 d の減衰は 1 / (1 + (d / COSMIC_BALL_LIGHT_RADIUS)^2)(ボールの近くを明るく、遠ざかると暗くする)。
float4 _CosmicBallLight;
#define COSMIC_BALL_LIGHT_RADIUS 0.4
// ボールの半径(m)。MoonTripleLayout.BallRadius と同じ。
#define COSMIC_BALL_RADIUS 0.11
// x = 星空の明るさ、y = 遠方の銀河の場、z = 入れ子（宇宙地図）の重み。
float4 _CosmicSky;
// 診断の表示（URL・起動引数の cosmic-debug=、0 = 通常）。不透明面（CosmicOpaquePass）が、照明の結果の代わりに入力の項を色で出します:
// 1 = 視線、2 = 法線、3 = UV の画素の細かい縞（画像の 4096 画素ごと）、4 = 法線・視線の内積、5 = 法線・主光源の内積。
float _CosmicDebugView;
// Radial の歪みの中心にない目（俯瞰のワイプ、Scene View などの観察者・比較の目）で描いているか。製品のカメラの情報で判定します:
// _OmniRadialCamera_State.x = Radial が有効、z = −1 比較の目、+1 自己中心の Scene View の表示残差あり、0 それ以外。
// z = 0 には、目が歪みの中心にある主カメラ（目の相対位置 = 0）と、中心から離れた観察者（目の相対位置 ≠ 0）の両方が入るので、
// 目の相対位置（_OmniRadialCamera_EyeRelative）で分けます。観察者の目は Radial で圧縮した世界を外から見るので、
// 物体空間の視線の復元が近似になります。そこでは視線に強く依存する表現（ブラックホールの空の置き換え、銀河の星）を省き、
// 歩数も減らします（主画面の約 8% の画素なのに、ワイプが毎フレーム 4〜8 ms かかっていた）。RadialInject を含まない描画（全画面の効果）は主の視点です。
bool CosmicIsObserverPass()
{
#if defined(OMNI_RADIAL_INJECT_INCLUDED)
    float4 state = OmniRadialCameraStateValue();
    if (state.x <= 0.5 || state.z > 0.5) return false;
    if (state.z < -0.5) return true;
    float3 eye = OmniRadialCameraEyeRelativeValue().xyz;
    return dot(eye, eye) > 0.0;
#else
    return false;
#endif
}

// 全シェーダー・全パスで同じ配置（BRG は UnityPerMaterial の大きさが違う描画を拒否します）。
CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    float4 _BaseColor;
    // x = 段の番号（global 配列の添字）、y = 種類・形の番号、z = シード、w = 予備。
    float4 _CosmicBody;
    float4 _CosmicP0;
    float4 _CosmicP1;
    float4 _CosmicP2;
    float4 _CosmicP3;
CBUFFER_END

uint CosmicBodyIndex(){return min((uint)(_CosmicBody.x+0.5),(uint)(COSMIC_MAX_BODIES-1));}
float CosmicFade(){return _CosmicState[CosmicBodyIndex()].x;}
float CosmicEmission(){return _CosmicState[CosmicBodyIndex()].y;}
float CosmicSeconds(){return _CosmicTime.x;}

// 不透明な球の表面の点の情報（CosmicOpaquePass が作り、各シェーダーの CosmicSurfaceColor が受け取る）。
// unitPosition は単位半径の球の上の点（物体空間）、footprint は画素の大きさ（同じ単位、半径 = 1）。
struct CosmicSurface
{
    float3 unitPosition;
    // 表面の点の物体空間の法線（球は位置の向き、分割した地面は天体の局所の方向、COSMIC_SURFACE_MESH_NORMAL は頂点の法線の補間）。
    float3 normalOS;
    float3 normalWS;
    float3 viewWS;
    float2 uv;
    float footprint;
    // 表面の点の、カメラからの相対位置(Logical の世界軸、m)。ボールの点光源 _CosmicBallLight の距離に使う。
    float3 cameraRelative;
};

#endif
