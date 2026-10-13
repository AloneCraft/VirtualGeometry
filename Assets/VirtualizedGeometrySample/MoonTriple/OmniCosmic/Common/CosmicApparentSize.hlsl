// 主カメラ（Radial の圧縮の中心）から見た物体の見かけの大きさ。原子と陽子の印（CosmicMicroMarks）と陽子のガラスの陰影（CosmicMicroSurface）が、
// 「どの段で何を見せるか」をこれで決めます。描画中のカメラではなく圧縮の中心から測るので、俯瞰カメラにも主カメラと同じ見た目が映ります
// （俯瞰カメラの論理の目は対数圧縮を逆にたどった遠い位置にあり、そこから測ると印が消えていた）。物体全体で 1 つの値です
// （画素ごとの footprint は面の傾きで変わり、2×2 画素の塊のむらになった）。RadialInject.hlsl の後に読みます。
#ifndef COSMIC_APPARENT_SIZE_INCLUDED
#define COSMIC_APPARENT_SIZE_INCLUDED

// x = 主カメラの 1 画素の角度（rad、CosmicScaleDirector が毎フレーム書く）。0 なら描画中のカメラの値で代用します（Edit モード）。
float4 _CosmicMainView;

// 物体空間の圧縮の中心（主カメラの目）の位置。OmniRadialCenterRelative は物体の中心 − 圧縮の中心で、主カメラでは
// OmniRadialLogicalEyePosition と同じ値、俯瞰カメラでは俯瞰カメラの目ではなく主カメラの目からの差になります。
float3 CosmicCenterObjectSpace()
{
    if (OmniRadialUsesLogicalTransform() && !OmniRadialUsesSharedUnityTransform())
    {
        float3 mantissa;
        float exponent;
        OmniRadialCenterRelative(mantissa, exponent);
        float4 exponents = OmniRadialTransformExponentsValue();
        return OmniRadialInverseDirection(OmniRadialBasis(), -mantissa) * exp2(clamp(exponent - exponents.y, -126.0, 126.0));
    }
    return TransformWorldToObject(GetCameraPositionWS());
}

// 物体の中心にある半径 radius（物体空間）の球の、主カメラから見た見かけの半径（画素）。eyeOS は CosmicCenterObjectSpace()。
float CosmicApparentRadiusPixels(float3 eyeOS, float radius)
{
    float distanceOS = max(length(eyeOS), radius * 1.0001);
    float angularRadius = asin(saturate(radius / distanceOS));
    float pixelAngle = _CosmicMainView.x > 0.0 ? _CosmicMainView.x : 2.0 / (abs(UNITY_MATRIX_P[1][1]) * _ScaledScreenParams.y);
    return angularRadius / max(pixelAngle, 1e-12);
}

#endif
