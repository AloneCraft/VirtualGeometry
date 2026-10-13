// 階層の Scene のブラックホール（CosmicLensingBlackHole）。光線の積分は CosmicBlackHoleShading.hlsl（CosmicScale）と同じ式で、
// 最後に曲がった光線の向きを画面へ投影し、CosmicLensPass が透明物体の後に写した画面の色（_CosmicLensSource）を引きます。
// これで、ブラックホールの後ろにある他の段の天体（太陽の光点、銀河）が曲がって見えます（CosmicScale は星空だけを曲げます）。
// ・手前の天体の二重像を防ぐ: 引いた位置の深度がブラックホールの中心より手前なら、その色は使わず星空にします
//   （CosmicScale で画面の色を曲げて手前の恒星の縁が二重になった失敗、4ae79ef7 の対策）。
// ・画面の外へ曲がった光線は、いて座A* から見たデータの空（天の川の淡い光と星の地図、CosmicHierarchySkyShading.hlsl）へ滑らかに切り替えます
//   （画面の外の天体は、地図にない点群の外のものは写りません: 制限）。
// ・不透明度は CosmicScale と同じく「光の曲がりが何画素か」で決め、曲がらない所は透明にして後ろの景色をそのまま残します。
// 共有のファイルを変えないため、ヘルパー（乱流・赤方偏移・背景）は CosmicBlackHoleShading.hlsl を読み込んで使い、
// 積分のループだけをこのファイルに持ちます（そのファイルの CosmicShellShade は使いません）。
#ifndef COSMIC_LENSING_BLACK_HOLE_SHADING_INCLUDED
#define COSMIC_LENSING_BLACK_HOLE_SHADING_INCLUDED

#include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicBlackHoleShading.hlsl"
#include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicHierarchySkyShading.hlsl"

TEXTURE2D_X(_CosmicLensSource); SAMPLER(sampler_CosmicLensSource);

// 物体空間の向きの、画面の UV（投影の y の向きは呼び出し側で合わせる）。画面の後ろなら valid = false。
float2 CosmicLensProjectDirection(float3 directionOS, out bool valid)
{
    float3 world = OmniRadialUsesLogicalTransform() ? OmniRadialObjectToLogicalDirection(directionOS) : normalize(TransformObjectToWorldDir(directionOS));
    float3 view = OmniRadialUsesLogicalTransform() ? OmniRadialCameraRelativeToView(world) : mul((float3x3)UNITY_MATRIX_V, world);
    float4 clip = mul(UNITY_MATRIX_P, float4(view, 1.0));
    valid = clip.w > 1e-6;
    return clip.xy / max(clip.w, 1e-6) * 0.5 + 0.5;
}

float4 CosmicLensShade(CosmicRay ray, float centerDepth)
{
    float boundary = _CosmicP0.x;
    float scale = boundary / 0.5;
    float rin = _CosmicP0.y, rout = _CosmicP0.z;
    float core = _CosmicP2.w > 0 ? _CosmicP2.w : boundary;
    uint mode = (uint)(_CosmicP0.w + 0.5);
    float peakTemperature = _CosmicP1.x;
    float brightness = _CosmicP1.y * CosmicEmission();
    float t = CosmicSeconds();
    float seed = _CosmicBody.z;

    float3 x = (ray.origin + ray.direction * ray.enter) * scale;
    float3 v = ray.direction;
    float3 startDirection = v;
    float impact = length(cross(x, v)) / boundary;
    float h2 = dot(cross(x, v), cross(x, v));
    float3 emission = 0;
    float transmittance = 1;
    bool captured = false;
    float pixelRs = ray.pixelAngle * length(ray.origin * scale);
    [loop] for (int i = 0; i < COSMIC_BH_STEPS; i++)
    {
        float r = length(x);
        float dt = clamp(0.08 * r * r / (r + 2.0), 0.02, 2.0);
        float taper = 1.0 - smoothstep(core, boundary, r);
        float3 a = -1.5 * h2 * x / (r * r * r * r * r) * taper;
        float3 vNext = v + a * dt;
        float3 xNext = x + vNext * dt;
        if (mode == 0 && x.y * xNext.y < 0.0)
        {
            float f = x.y / (x.y - xNext.y);
            float3 c = lerp(x, xNext, f);
            float rc = length(c.xz);
            if (rc > rin && rc < rout && transmittance > 0.01)
            {
                float profile = pow(rc / rin, -0.75) * pow(saturate(1.0 - sqrt(rin / rc)), 0.25) / 0.488;
                float g = CosmicRedshift(c, rc, -normalize(vNext));
                float turbulence = CosmicDiskTurbulence(c, rc, t, _CosmicP1.w, rin, pixelRs * 0.9, seed, 5);
                float edge = smoothstep(rout, rout * 0.82, rc) * smoothstep(rin, rin * 1.08, rc);
                float3 color = CosmicBlackbodyChroma(peakTemperature * profile * g) * pow(saturate(profile), 3.0) * pow(g, 4.0) * turbulence * edge;
                float alpha = 0.92 * edge;
                emission += transmittance * alpha * color;
                transmittance *= 1.0 - alpha;
            }
        }
        if (mode == 1)
        {
            float rr = length(xNext);
            float rho = length(xNext.xz);
            float shell = exp(-pow((rho - _CosmicP2.x) / _CosmicP2.y, 2.0)) * exp(-pow(xNext.y / (0.45 * rho + 0.3), 2.0));
            if (shell > 1e-3 && rr > 1.05)
            {
                float g = CosmicRedshift(xNext, rr, -normalize(vNext));
                float turbulence = CosmicDiskTurbulence(xNext, rr, t, _CosmicP1.w, _CosmicP2.x, pixelRs * 0.9, seed, 2);
                float3 color = CosmicBlackbodyChroma(peakTemperature * g) * pow(g, 3.0) * shell * turbulence * (0.6 + _CosmicP2.z * 0.4);
                emission += transmittance * color * dt * 0.9;
            }
        }
        x = xNext; v = vNext;
        if (dot(x, x) < 1.0) { captured = true; break; }
        if (dot(x, x) > boundary * boundary * 1.0001 && dot(x, v) > 0.0) break;
    }
    float3 endDirection = normalize(v);
    float bend = atan2(length(cross(endDirection, startDirection)), dot(endDirection, startDirection));
    float edge = 1.0 - smoothstep(0.92, 1.0, impact);
    float coverage = captured ? 1.0 : smoothstep(0.4, 1.5, bend / max(ray.pixelAngle, 1e-6)) * edge;
    float3 background = 0;
    if (!captured && coverage > 0)
    {
        bool startValid, endValid;
        float2 startUV = CosmicLensProjectDirection(startDirection, startValid);
        float2 endUV = CosmicLensProjectDirection(endDirection, endValid);
        // 投影の y の向き（API・描画先で反転する）を、この画素自身の UV と比べて合わせます。
        if (abs((1.0 - startUV.y) - ray.screenUV.y) < abs(startUV.y - ray.screenUV.y)) { endUV.y = 1.0 - endUV.y; }
        float2 margin = min(endUV, 1.0 - endUV);
        float onScreen = endValid ? smoothstep(0.0, 0.03, min(margin.x, margin.y)) : 0.0;
        float3 skyDirection = OmniRadialUsesLogicalTransform() ? OmniRadialObjectToLogicalDirection(endDirection) : normalize(TransformObjectToWorldDir(endDirection));
        float3 sky = CosmicHierarchySgrASky(skyDirection, ray.pixelAngle);
        float3 scene = sky;
        if (onScreen > 0)
        {
            float2 uv = saturate(endUV);
            float rawDepth = SampleSceneDepth(uv);
#if UNITY_REVERSED_Z
            bool front = rawDepth > centerDepth;
#else
            bool front = rawDepth < centerDepth;
#endif
            scene = front ? sky : SAMPLE_TEXTURE2D_X(_CosmicLensSource, sampler_CosmicLensSource, uv).rgb;
        }
        background = lerp(sky, scene, onScreen);
    }
    float3 color = emission * brightness + background * transmittance * coverage;
    return float4(color, max(coverage, 1.0 - transmittance));
}

// CosmicShellPass.hlsl の CosmicShellFragment と同じ手順で、中心の深度を光線の色の計算にも渡します。
void CosmicLensFragment(CosmicShellVaryings input, out half4 outputColor : SV_Target, out float outputDepth : SV_Depth)
{
    UNITY_SETUP_INSTANCE_ID(input);
    float fade = CosmicFade();
    if (fade <= 0.001) discard;
    CosmicRay ray;
    ray.surface = input.positionOS.xyz / input.positionOS.w;
    ray.origin = input.eyeOS;
    ray.direction = normalize(ray.surface - ray.origin);
    float2 hit = CosmicRaySphere(ray.origin, ray.direction, 0.5);
    ray.enter = max(hit.x, 0.0);
    ray.exit = max(hit.y, ray.enter);
    float3 dx = ddx(ray.direction), dy = ddy(ray.direction);
    ray.pixelAngle = max(sqrt(max(dot(dx, dx), dot(dy, dy))), 1e-6);
    ray.screenUV = input.positionCS.xy / _ScaledScreenParams.xy;
    float centerDepth = CosmicInvariantFragmentDepth(float4(0, 0, 0, 1), input.positionCS.z);
    outputColor = half4(CosmicLensShade(ray, centerDepth) * fade);
    outputDepth = centerDepth;
}

#endif
