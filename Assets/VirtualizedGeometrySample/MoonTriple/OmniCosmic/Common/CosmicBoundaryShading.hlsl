// CosmicHierarchy の境界の殻（CosmicBoundaryShell）。楕円体（物体空間の半軸 _CosmicP2.xyz）の輪郭を、画素の大きさで決まる一定の太さの線で描き、
// 内側をごく淡く塗ります。「この範囲に属する」を示す枠なので、中身（体積）は描きません。CosmicShellPass.hlsl の CosmicShellShade の実装です。
// 輪郭は「視線が楕円体に接する」位置です。楕円体を単位球へ写した空間で、視線と中心の距離 m が 1 になるところを線にします。
// （面の法線と視線の角度で縁を光らせる方式は、細長い楕円体の端で広い範囲が明るくなり、隣の球と重なって別の球のように見えたため使いません。）
#ifndef COSMIC_BOUNDARY_SHADING_INCLUDED
#define COSMIC_BOUNDARY_SHADING_INCLUDED

float4 CosmicShellShade(CosmicRay ray)
{
    float3 axes = max(_CosmicP2.xyz, 1e-4);
    float3 origin = ray.origin / axes;
    float3 direction = normalize(ray.direction / axes);
    bool inside = dot(origin, origin) < 1.0;
    // カメラが外にいるときは、視線が中心の手前を通る向きだけ（後ろ向きの視線は楕円体に届かない）。
    if (!inside && dot(origin, direction) > 0.0) return 0;
    float m = length(cross(origin, direction));
    float width = max(fwidth(m), 1e-6) * 1.3;
    float outline = inside ? 0.0 : exp(-pow((m - 1.0) / width, 2.0));
    // 内側の淡い塗りは外から見たときだけ（カメラが中にいるときに画面全体を曇らせないため）。
    float fill = !inside && m < 1.0 ? 0.03 : 0.0;
    return float4(_CosmicP0.rgb * (_CosmicP0.w * (outline * 1.4 + fill)), 0);
}

#endif
