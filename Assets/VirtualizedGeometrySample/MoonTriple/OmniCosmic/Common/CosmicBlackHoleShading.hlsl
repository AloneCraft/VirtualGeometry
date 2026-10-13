// ブラックホール（CosmicBlackHole）。境界の球の中で、シュバルツシルト時空の光線（ヌル測地線）を数値積分します。
// 単位はシュバルツシルト半径 Rs = 1。光線をカメラから逆にたどり、加速度 a = −(3/2)·h²·x/|x|⁵（h = |x × v| は保存量）で曲げます
// （空間への射影がシュバルツシルトのヌル測地線と一致する、実時間描画でよく使われる形）。
// ・r < 1 で事象の地平面に吸い込まれた光線は黒（影）。影の見かけの半径は √27/2 Rs ≈ 2.6 Rs になり、地平面より大きく見えます。
// ・降着円盤（mode 0: クエーサー型）: 外側ほど厚くなる体積の円盤（赤道面 = 物体空間の y = 0）を、曲がった光線に沿ってたどります。
//   円盤の裏側を回り込んだ光で、円盤の上下に裏側の像ができます。外縁は影の約 20 倍（CosmicCatalog.QuasarDiskOuterRs、2026-10-05 のユーザーの判断）。
//   温度は r^(−3/4)(1−√(r_in/r))^(1/4)。表示の色温度はその COSMIC_DISK_COLOR_RADIAL_POWER 乗で、中心は白く、外縁は赤い黒体の色
//   （物理の r^(−3/4) では外縁まで約 8 倍しか下がらず、白から赤まで届かないため強めた演出）。明るさは外縁まで見えるよう r^−1.3 で緩く減らします。
//   厚さは外側ほど薄く広がり（半径の 2.5%）、雲の所だけ上下へふくらみます（厚すぎると、円盤が膨らんだ楕円の塊に見え、影の上へ曲がる奥側の像が隠れた）。
//   ケプラー回転（Ω ∝ r^−1.5）のドップラー効果と重力赤方偏移 g = δ√(1−1/r) で近づく側が明るくなり（強度 ∝ g⁴ を g³ に弱める）、
//   色は近づく側を少し青く、遠ざかる側を少し赤くします（COSMIC_DISK_DOPPLER_TINT）。
// ・mode 1: EHT で撮影された M87*・いて座A* のような、光子リングの近くの光学的に薄い高温のガス（体積の放射）。
// ・模様（mode 0）: 円盤と一緒に回る（内側ほど速い差動回転、2位相の周期リセット）ノイズを、半径の対数と角度の座標で読みます
//   （CosmicQuasarDiskNoise。模様の大きさが半径に比例するので、巨大な円盤の外側でも細かい木目にならない）。周方向に長い稜線の流れ（streams）と、
//   大きな粗い雲（clouds）を重ね、雲で濃さと上下のふくらみを変えます。mode 1 と CosmicHierarchy は CosmicDiskTurbulence（Rs の座標）のまま。
// ・背景: 曲がった光線の向きの星空（CosmicSky の関数）。不透明度は光の曲がりの大きさで決め、曲がりの小さい所は透明にして
//   後ろの本物の景色をそのまま見せます。曲げた星空で置き換えるのは、深度テクスチャで何も描かれていない（空の）画素だけです
//   （画面内の天体はレンズで曲げません。手前の天体のずれた二重像や、隣の天体が星空で塗りつぶされるのを防ぐ割り切り）。
// ・曲がりの範囲: 実際の曲がりは弱い場で 2Rs/b と遠くまで続くので、境界の球（15 Rs）で打ち切ると縁で急に歪みが始まります。
//   中心から core（6 Rs）までは厳密に積分し、その外は境界（15 Rs）でゼロになるよう滑らかに弱めます。隣の天体が球の中に入ることがあり、
//   その場合も空の画素だけが置き換わるので天体は消えません。
// ・mode 2: 相対論的ジェット（CosmicJetShade）。ジェットは境界の球の外まで伸びるので、ブラックホールと同じ位置・向きの別の物体
//   （境界の球の半径 = ジェットの長さ）に、同じシェーダーの別の材質で描きます。
// ・mode 3: 中性子星（PSR J0740+6620、SAX J1808.4−3658）。表面（赤道半径 R = _CosmicP0.y Rs、光子球 1.5 Rs より外）に当たった光線は表面の色、
//   当たらない光線は曲がった星空です。影は無く、光が曲がるので見かけの半径は R/√(1 − 1/R)（約 1.4 R）になり、裏側の一部（裏の磁極のホットスポット）も見えます。
//   表面は速い自転でふくらんだ回転楕円体（極の半径 = R(1 − 扁平率)、実際の大きさ）。一様な熱放射（色温度・強度とも重力赤方偏移 g = √(1 − 1/r) を掛ける）に、
//   自転とともに回る 2 つの磁極のホットスポット（自転軸から傾いた磁軸、CosmicMagneticAxis）。磁軸が視線の向きを通る瞬間は閃光（パルス）で、
//   表面を明るくし、まわりに光の暈を足します。降着円盤（_CosmicP0.z > 0）は赤道面の薄く不透明な円盤で、曲がった光線が面を横切る所で足します
//   （模様は CosmicQuasarDiskNoise、温度は r^(−3/4)(1−√(r_in/r))^(1/4) で色は巨大な円盤と同じく中心が白く外ほど赤い、ドップラーと重力赤方偏移は CosmicRedshift）。
//   _CosmicP0.z = 円盤の外縁（Rs、0 で無し）。_CosmicP1: x = 表面の温度（K）、y = 明るさ、z = 円盤の明るさ、w = 自転の表示周期（秒）。
//   _CosmicP2: x = 磁軸の傾き（rad）、y = ホットスポットの角半径（rad）、z = ホットスポットの明るさ、w = core。
//   _CosmicP3: x = 扁平率、y = 円盤の内縁（Rs）、z = 円盤の最高温度（K）、w = 閃光の強さ。
// ・mode 4: パルサーのビーム（CosmicPulsarBeamShade）。ジェットと同じく中性子星と同じ位置・向きの別の物体に描きます（単位は星の半径 R）。
// _CosmicP0: x = 境界の球の半径（Rs）、y = 円盤の内縁（Rs。mode 3 は表面の半径）、z = 外縁（Rs）、w = 形（0 薄い円盤、1 光子リング型、2 ジェット、3 中性子星、4 ビーム）。
// _CosmicP1: x = 円盤の最高温度（K）、y = 明るさ、z = 未使用、w = 円盤の内縁の回転の表示周期（秒）。
// _CosmicP2: x = リングの半径（Rs）、y = リングの幅（Rs）、z = 乱流の強さ、w = 厳密に曲げる範囲の半径 core（Rs、外側は境界の球まで滑らかに弱める）。
// 深度はブラックホールの中心の深度を書きます（境界の球の面の深度だと、球の中にある隣の天体を影・円盤で上書きするため）。
// ・広げたレンズ（_CosmicP3.x = 1、CosmicScale の梯子のクエーサー型だけ。2026-10-06 のユーザーの判断 A「物理に近づける」）:
//   境界の球 150 Rs・厳密に曲げる範囲 75 Rs（円盤の外縁の 3 倍・1.5 倍）。上の 15 Rs・6 Rs は光子リング型（M87・いて座A*）の値で、
//   CosmicEvents の OJ 287 などは従来どおり境界 75 Rs（見た目の広がりの 1.5 倍）・6 Rs（CosmicScaleLooks.For）。
//   遠くの歩幅を半径に比例させ（mode 3 と同じ。歩数 280 のまま足りる）、始点のずらしも曲がりを入れて積分します（直進で飛ばすと曲がりを落とす）。
//   置き換えは、先に描いた天体（描画の順位 _CosmicP3.y より前）のレンズが曲げる光線（その中心への最接近距離が境界の 0.85 倍未満）ではせず、
//   0.85〜1 倍で曲がりを 0 から戻します（_CosmicLensNeighbors、CosmicScaleDirector.LensNeighbors。目に依らない物体空間の値）。
//   俯瞰（観察者の目）では殻を _CosmicP3.z 倍（従来の 75 Rs）に縮めます（主カメラを包む殻がドームにならないよう、CosmicShellPass.hlsl）。
#ifndef COSMIC_BLACK_HOLE_SHADING_INCLUDED
#define COSMIC_BLACK_HOLE_SHADING_INCLUDED

// 先に描くレンズの天体（CosmicScaleDirector.LensNeighbors）: [自分の順位 × 8 + 相手の順位] = (相手の中心, 相手の境界の球の半径)。
// 自分の中心からの差を Logical の世界の軸のまま、自分の物体（単位直径の球）の大きさで割った値（回転の約束に依らないよう、光線の側を世界の軸へ戻す）。
float4 _CosmicLensNeighbors[64];
// x = 描画の順位ごとの表示の重み（0 は描いていない。global を書かない Scene では既定値 0 で、守る相手が無い）。
float4 _CosmicLensFade[8];

// 物体空間のベクトルを、長さを保ったまま Logical の世界の軸へ（星空を引く CosmicBackground と同じ変換）。
float3 CosmicLensWorldAxes(float3 vectorOS)
{
    float len = length(vectorOS);
    if (!(len > 0.0)) return float3(0, 0, 0);
    float3 direction = vectorOS / len;
    return (OmniRadialUsesLogicalTransform() ? OmniRadialObjectToLogicalDirection(direction) : normalize(TransformObjectToWorldDir(direction))) * len;
}

// 広げたレンズの曲がりの倍率（1 = そのまま）: 光線（物体空間の目から向き direction）が、先に描いた天体の中心へ最も近づく距離が
// その境界の球の 0.85 倍未満なら 0、1 倍まででそのまま戻します（相手のレンズが曲げる光線だけを守る）。効き方は相手の表示の重み。
// C# の CosmicScaleDirector.LensGuardScale と同じ式（目と向きは世界の軸へ戻してから比べる）。
float CosmicLensGuardScale(float3 originOS, float3 directionOS, int rank)
{
    float scale = 1.0;
    float3 originWorld = CosmicLensWorldAxes(originOS);
    float3 directionWorld = CosmicLensWorldAxes(directionOS);
    [loop] for (int g = 0; g < min(rank, 8); g++)
    {
        float weight = _CosmicLensFade[g].x;
        if (!(weight > 0.0)) continue;
        float4 neighbor = _CosmicLensNeighbors[rank * 8 + g];
        if (!(neighbor.w > 0.0)) continue;
        float3 toCenter = neighbor.xyz - originWorld;
        float along = max(dot(toCenter, directionWorld), 0.0);
        float closest = length(toCenter - directionWorld * along);
        scale *= lerp(1.0, smoothstep(0.85, 1.0, closest / neighbor.w), weight);
    }
    return scale;
}

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Assets/VirtualizedGeometrySample/MoonTriple/OmniCosmic/Common/CosmicSkyFunctions.hlsl"

#define COSMIC_BH_STEPS 280
// 影の見かけの半径（衝突径数の臨界値 √27/2 Rs）。これより小さい衝突径数で入ってくる光線は必ず地平面へ落ちます。
#define COSMIC_BH_CRITICAL_IMPACT 2.5980762
// 円盤の見せ方（映画のブラックホールのように、光る濃い円盤）: 中央の面を垂直に横切るときの光学的厚さ、放射の倍率、
// 色温度の倍率（黒体の色を橙〜白へ寄せる）、ドップラーの色（近づく側を青、遠ざかる側を赤）の強さ。
#define COSMIC_DISK_OPTICAL_DEPTH 6.0
#define COSMIC_DISK_EMISSION_GAIN 5.0
#define COSMIC_DISK_COLOR_TEMPERATURE_SCALE 0.5
// ドップラーの色は「ごく弱く」よりやや強く（2026-10-05 のユーザーの判断。以前は 0.8 で、半径の白〜赤より目立っていた）。
#define COSMIC_DISK_DOPPLER_TINT 0.4
// 円盤の明るさの半径の減り方（r^−この値）。以前の profile³（r^−2.25）では、外縁を影の 20 倍にすると外側が見えなかった。
#define COSMIC_DISK_BRIGHTNESS_SLOPE 1.3
// 表示の色温度 = 最高温度 × profile^この値（中心は白、外縁は赤）。
#define COSMIC_DISK_COLOR_RADIAL_POWER 2.0

// 円盤の乱流の明るさ（0.3..1.6 程度）。点 x（Rs）を内側ほど速く回して標本化します。
float CosmicDiskTurbulence(float3 x, float r, float t, float innerPeriod, float rin, float footprint, float seed, int octaves)
{
    float value = 0;
    [unroll] for (int j = 0; j < 2; j++)
    {
        float tau, weight, epoch;
        float period = innerPeriod * 3.0;
        CosmicFlowPhase(t, period, j, tau, weight, epoch);
        float omega = 6.2831853 / innerPeriod * pow(max(r / rin, 1.0), -1.5);
        // 物質は +x で +z へ回ります（CosmicRedshift の接線と同じ向き）。CosmicRotateY(·, +a) は +x を −z へ回すので、
        // 物質の動き RotY(−ωt) の逆 RotY(+ωt) で標本化します（逆にすると、模様がドップラーと逆向きに流れて見えた）。
        float3 p = CosmicRotateY(x, omega * tau * period);
        float3 domain = p * 0.9 + (CosmicHash3(int3((int)epoch, j, (int)seed)) - 0.5) * 5.0;
        float n = CosmicFbmTex(domain, octaves, footprint, 2.1, 0.55);
        // 筋: 半径方向の縞をノイズで大きくゆがめ、縞の強さもまばらにします（整った同心円の線に見せない）。
        float rings = 0.5 + 0.5 * sin(r * 3.1 + n * 7.0);
        float ringStrength = saturate(0.5 + 0.9 * CosmicFbmTex(domain * 0.37 + 17.0, 2, footprint * 0.37, 2.0, 0.5));
        value += (0.55 + 0.7 * (0.5 + 0.5 * n) * (1.0 - 0.6 * ringStrength + 0.6 * ringStrength * rings)) * weight;
    }
    return value;
}

// 巨大な円盤（mode 0）の模様のノイズ（−1..1 程度）。点 x（Rs）を円盤と一緒に回し（内側ほど速い。CosmicDiskTurbulence と同じ 2 位相の周期リセット）、
// 角度と半径の対数の座標で読みます。azimuthal は 1 周の模様の数の目安、radial は半径が e 倍になる間の模様の数、模様の大きさは半径に比例します。
// warp は半径の座標のずらし（模様の数の単位。雲のノイズを渡すと、流れの筋が雲に沿って曲がり、規則的な同心円にならない）。
float CosmicQuasarDiskNoise(float3 x, float r, float t, float innerPeriod, float rin, float footprint, float seed, float azimuthal, float radial, int octaves, float warp)
{
    float value = 0;
    [unroll] for (int j = 0; j < 2; j++)
    {
        float tau, weight, epoch;
        float period = innerPeriod * 3.0;
        CosmicFlowPhase(t, period, j, tau, weight, epoch);
        float omega = 6.2831853 / innerPeriod * pow(max(r / rin, 1.0), -1.5);
        // 物質の動き RotY(−ωt) の逆 RotY(+ωt) で標本化します（CosmicDiskTurbulence と同じ向き）。
        float3 p = CosmicRotateY(x, omega * tau * period);
        float2 around = p.xz / max(r, 1e-3);
        float3 domain = float3(around.x * azimuthal * 0.16, log(max(r, rin) / rin) * radial + p.y / max(r, 1e-3) * radial + warp, around.y * azimuthal * 0.16)
            + (CosmicHash3(int3((int)epoch, j, (int)seed)) - 0.5) * 7.0;
        // 画素の大きさ（Rs）を模様の座標へ（半径方向の倍率が大きい方）。
        float scale = max(azimuthal * 0.16, radial) / max(r, 1e-3);
        value += CosmicFbmTex(domain, octaves, footprint * scale, 2.1, 0.55) * weight;
    }
    return value;
}

// 速度 β（局所静止観測者から見たケプラー円運動）と、光子の向き（観測者へ）から、ドップラー因子 × 重力赤方偏移 g。
float CosmicRedshift(float3 x, float r, float3 towardObserver)
{
    float beta = min(sqrt(0.5 / max(r - 1.0, 0.05)), 0.95);
    float gamma = rsqrt(1.0 - beta * beta);
    float3 tangent = normalize(float3(-x.z, 0, x.x));
    float delta = 1.0 / (gamma * (1.0 - beta * dot(tangent, towardObserver)));
    return delta * sqrt(saturate(1.0 - 1.0 / r));
}

// 背景: 曲がった光線の向きの星空（CosmicSky の関数、画面の解像度のまま鋭い）。
float3 CosmicBackground(float3 directionOS, float pixelAngle)
{
    float3 world = OmniRadialUsesLogicalTransform() ? OmniRadialObjectToLogicalDirection(directionOS) : normalize(TransformObjectToWorldDir(directionOS));
    return CosmicSkyColor(world, pixelAngle);
}

// mode 2: 相対論的ジェット。自転軸（物体の ±y）に沿って細く絞られた青白いビームを、ねじれ合う筋の束として描き、
// ねじれと塊（ノット）の模様を外へ流します（参考: 降着円盤からジェットが噴き出す想像図の、らせん状にねじれた細いビーム）。
// ・レンズでは曲げません（ジェットの大部分は曲がりの弱い遠方。根元も近似として直進させます）。
// ・後ろ側は、影（衝突径数が見かけの半径 √27/2 Rs より小さい光線の、最接近点より先）と円盤（赤道面を内縁〜外縁で横切った先）で隠します。
// ・外側は外形（円錐）までの距離だけ進み（球の追跡）、中は幅に比例した歩幅で積分します。
// ・1 画素より細い区間は、幅を画素の大きさまで広げ、明るさを 半径 / 幅 で下げて光の総量を保ちます（遠景でちらつかせない）。
// ・相対論的ビーミング: 近づく側が明るく遠ざかる側が暗い（δ^2.5、真横で 1 になるよう正規化）。
// _CosmicP0: x = 長さ（Rs、境界の球の半径）、y = 根元の半径（Rs）、z = 半径の伸び（Rs あたり）、w = 2。
// _CosmicP1: x = 明るさ、y = 模様の流れの速さ（Rs/秒）、z = ねじれの1周の長さ（Rs）、w = 筋の数（4 まで）。
// _CosmicP2: x, y = 円盤の内縁・外縁（Rs）、z = 円盤の不透明度（0 で円盤なし）、w = 影の見かけの半径（Rs）。_CosmicP3: x = ビーミングの速さ β。
#define COSMIC_JET_STEPS 96
#define COSMIC_JET_OBSERVER_STEPS 40
float4 CosmicJetShade(CosmicRay ray)
{
    float jetLength = _CosmicP0.x;
    float scale = jetLength / 0.5;
    float baseRadius = _CosmicP0.y, slope = _CosmicP0.z;
    float3 o = ray.origin * scale;
    float3 d = ray.direction;
    float t0 = ray.enter * scale, t1 = ray.exit * scale;
    // 軸のまわりの円柱（先端の半径、遠景では数画素）で区間を絞ります。外れた画素はここで終わります。
    float a = dot(d.xz, d.xz);
    float b = dot(o.xz, d.xz);
    float tAxis = a > 1e-8 ? clamp(-b / a, t0, t1) : t0;
    float bound = max(baseRadius + slope * jetLength, 2.0 * ray.pixelAngle * max(tAxis, 1e-3)) * 1.8;
    float c = dot(o.xz, o.xz) - bound * bound;
    float tA = t0, tB = t1;
    if (a > 1e-8)
    {
        float disc = b * b - a * c;
        if (disc <= 0.0) return 0;
        float root = sqrt(disc);
        tA = max(tA, (-b - root) / a);
        tB = min(tB, (-b + root) / a);
    }
    else if (c > 0.0) return 0;
    if (tB <= tA) return 0;

    float tClosest = -dot(o, d);
    float impact = length(o + d * tClosest);
    float shadowFrom = impact < _CosmicP2.w ? tClosest - sqrt(max(_CosmicP2.w * _CosmicP2.w - impact * impact, 0.0)) : 1e30;
    float diskFrom = 1e30, diskCover = 0;
    if (_CosmicP2.z > 0 && abs(d.y) > 1e-6)
    {
        float tDisk = -o.y / d.y;
        float rhoDisk = length((o + d * tDisk).xz);
        if (tDisk > 0)
        {
            diskFrom = tDisk;
            diskCover = _CosmicP2.z * smoothstep(_CosmicP2.x * 0.9, _CosmicP2.x * 1.1, rhoDisk) * (1.0 - smoothstep(_CosmicP2.y * 0.85, _CosmicP2.y, rhoDisk));
        }
    }

    float time = CosmicSeconds();
    float speed = _CosmicP1.y, pitch = _CosmicP1.z;
    int strands = clamp((int)(_CosmicP1.w + 0.5), 1, 4);
    float beta = _CosmicP3.x;
    float gamma = rsqrt(1.0 - beta * beta);
    float beamingNormal = pow(gamma, 2.5);
    float2 pixel = floor(ray.screenUV * _ScaledScreenParams.xy);
    float jitter = frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
    float t = tA;
    bool started = false;
    float3 color = 0;
    int stepCount = CosmicIsObserverPass() ? COSMIC_JET_OBSERVER_STEPS : COSMIC_JET_STEPS;
    [loop] for (int i = 0; i < stepCount; i++)
    {
        if (t >= tB || t >= shadowFrom) break;
        float3 p = o + d * t;
        float height = abs(p.y);
        float radius = baseRadius + slope * min(height, jetLength);
        float width = max(radius, ray.pixelAngle * t * 0.8);
        float outside = max(length(p.xz) - 1.8 * width, height - jetLength);
        if (outside > 0.0)
        {
            t += max(outside * 0.8, 0.1 * width);
            continue;
        }
        float dt = 0.12 * width;
        // 中へ入った最初の1歩だけ、画素ごとにずらします（等間隔の標本の縞を消す）。
        if (!started) { started = true; t += dt * jitter; p = o + d * t; height = abs(p.y); }
        float2 xz = p.xz / width;
        float q2 = dot(xz, xz);
        // 模様は外へ流れます（ねじれの角度と塊の位置が、根元からの距離 − 速さ × 時間で決まる）。
        float s = height - speed * time;
        float twist = 6.2831853 * s / pitch;
        // 太い筋（外側をねじれて回る）と、逆向きに速くねじれる細い繊維（内側）を編み合わせ、組みひものような鋭い縞にします。
        float bundle = 0, braid = 0;
        [unroll(4)] for (int k = 0; k < strands; k++)
        {
            float angle = twist + 6.2831853 * k / strands;
            float2 gap = xz - float2(cos(angle), sin(angle)) * 0.45;
            bundle += exp(-dot(gap, gap) / 0.018);
            float inner = -2.0 * twist + 6.2831853 * k / strands + 1.0;
            float2 thin = xz - float2(cos(inner), sin(inner)) * 0.22;
            braid += exp(-dot(thin, thin) / 0.006);
        }
        float core = exp(-q2 / 0.012);
        float halo = exp(-q2 / 0.6) * 0.1;
        float knots = 0.6 + 0.8 * pow(0.5 + 0.5 * cos(6.2831853 * s / (pitch * 2.7)), 4.0);
        // 根元（影の外）から立ち上げ、先へ行くほど淡く、端で消えます。
        float along = smoothstep(2.8, 5.0, height) * (1.0 - smoothstep(0.6 * jetLength, jetLength, height)) * (0.4 + 0.6 * exp(-height / (0.3 * jetLength)));
        float cosine = (p.y >= 0 ? -d.y : d.y);
        float doppler = pow(1.0 / (gamma * (1.0 - beta * cosine)), 2.5) * beamingNormal;
        float3 emission = float3(0.55, 0.72, 1.0) * (bundle * 2.2 + braid * 1.4) * knots + float3(0.9, 0.95, 1.0) * core * 3.0 * knots + float3(0.35, 0.5, 1.0) * halo;
        float occlusion = t > diskFrom ? 1.0 - diskCover : 1.0;
        // 単位長さあたりの放射 ∝ 1 / 幅（面の明るさを幅によらず一定に）。1 画素未満では 半径 / 幅 を掛けて光の総量を保ちます。
        color += emission * (along * doppler * occlusion * (radius / width) / width * dt);
        t += dt;
    }
    return float4(color * (_CosmicP1.x * CosmicEmission()), 0);
}

// 中性子星の磁軸（物体空間の単位ベクトル）: 自転軸（物体の y）から tilt 傾き、表示上の周期 period（秒）で y のまわりを回ります。
// 磁力線の印（CosmicMicroMarks.hlsl の種類 4）も同じ式で回します。
float3 CosmicMagneticAxis(float tilt, float period, float t)
{
    float spin = 6.2831853 * t / max(period, 0.1);
    return float3(sin(tilt) * cos(spin), cos(tilt), sin(tilt) * sin(spin));
}

// 閃光の角の幅（rad）: 磁軸と視線の向きの角がこの程度の間だけ光ります（表示上の周期 2 秒で約 0.1 秒）。
// 0.18 では暈が周期の 2 割ほど画面の左上（HUD の文字）まで白く覆いました。
#define COSMIC_PULSAR_FLASH_WIDTH 0.12

// mode 4: パルサーのビーム（電波。実際には目に見えない放射を図示する演出、2026-10-06 のユーザーの判断）。磁軸に沿って両極から出る細い円錐で、
// 自転とともに灯台のように回ります。模様（パルスの塊）は外へ流れます。ビームがこちらを向くと視線がビームの中を長く通るので、強く光ります（閃光）。
// ・単位は星の半径 R。後ろ側は星の見かけの円板（衝突径数 < _CosmicP2.w）の先で隠します（光の曲がりは無視）。
// ・外側は外形（円錐）までの距離だけ進み、中は幅に比例した歩幅（ビームの中を長く通る視線は、区間を歩数で割った歩幅に広げる）。
// ・1 画素より細い区間は、ジェットと同じく幅を画素まで広げ、明るさを 半径 / 幅 で下げます。
// ・ビームを軸に近い向きから見ると、視線がビームの中を長く通って 1/sin（軸と視線の角）倍に明るくなり、自転の周期の約 4 割の間
//   星のまわりが白く飛んだので、sin を掛けて横から見たときと同じ明るさにします（こちらを向く瞬間の強い光は、表面の閃光で出す）。
// ・カメラの近くの区間（星までの距離の 15〜60% より手前）は弱めます。主役のカメラは星の半径の約 4 倍にいてビームの中に入るので、
//   そのままでは磁軸がこちらを向くたびに、どの向きの視線もカメラのまわりのビームを通って画面全体が白く飛んだ（2026-10-06 の撮影）。
// _CosmicP0: x = 長さ（R、境界の球の半径）、y = 根元の半径（R）、z = 半径の伸び（R あたり）、w = 4。
// _CosmicP1: x = 明るさ、y = 模様の流れの速さ（R/秒）。_CosmicP2: w = 星の見かけの半径（R）。
// _CosmicP3: x = 磁軸の傾き（rad）、y = Rs ÷ R、z = 自転の表示上の周期（秒）、w = 閃光の強さ（未使用。閃光は視線とビームの重なりから出る）。
#define COSMIC_BEAM_STEPS 72
#define COSMIC_BEAM_OBSERVER_STEPS 28
#define COSMIC_BEAM_PULSE_SPACING 3.0
float4 CosmicPulsarBeamShade(CosmicRay ray)
{
    float beamLength = _CosmicP0.x;
    float scale = beamLength / 0.5;
    float baseRadius = _CosmicP0.y, slope = _CosmicP0.z;
    float time = CosmicSeconds();
    // 磁軸を y にした座標へ回します。
    float3 axis = CosmicMagneticAxis(_CosmicP3.x, _CosmicP3.z, time);
    float3 helper = abs(axis.y) < 0.9 ? float3(0, 1, 0) : float3(1, 0, 0);
    float3 e0 = normalize(cross(helper, axis));
    float3 e2 = cross(axis, e0);
    float3 originR = ray.origin * scale;
    float3 o = float3(dot(originR, e0), dot(originR, axis), dot(originR, e2));
    float3 d = float3(dot(ray.direction, e0), dot(ray.direction, axis), dot(ray.direction, e2));
    float t0 = ray.enter * scale, t1 = ray.exit * scale;
    // 磁軸のまわりの円柱（先端の半径）で区間を絞ります。
    float a = dot(d.xz, d.xz);
    float b = dot(o.xz, d.xz);
    float tAxis = a > 1e-8 ? clamp(-b / a, t0, t1) : t0;
    float bound = max(baseRadius + slope * beamLength, 2.0 * ray.pixelAngle * max(tAxis, 1e-3)) * 1.8;
    float c = dot(o.xz, o.xz) - bound * bound;
    float tA = t0, tB = t1;
    if (a > 1e-8)
    {
        float disc = b * b - a * c;
        if (disc <= 0.0) return 0;
        float root = sqrt(disc);
        tA = max(tA, (-b - root) / a);
        tB = min(tB, (-b + root) / a);
    }
    else if (c > 0.0) return 0;
    if (tB <= tA) return 0;

    float tClosest = -dot(o, d);
    float impact = length(o + d * tClosest);
    float shadowFrom = impact < _CosmicP2.w ? tClosest - sqrt(max(_CosmicP2.w * _CosmicP2.w - impact * impact, 0.0)) : 1e30;
    float speed = _CosmicP1.y;
    float eyeDistance = max(length(o), 1e-3);
    float side = max(sqrt(saturate(1.0 - d.y * d.y)), 0.08);
    float2 pixel = floor(ray.screenUV * _ScaledScreenParams.xy);
    float jitter = frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
    int stepCount = CosmicIsObserverPass() ? COSMIC_BEAM_OBSERVER_STEPS : COSMIC_BEAM_STEPS;
    float minStep = (tB - tA) / stepCount;
    float t = tA;
    bool started = false;
    float3 color = 0;
    [loop] for (int i = 0; i < stepCount; i++)
    {
        if (t >= tB || t >= shadowFrom) break;
        float3 p = o + d * t;
        float height = abs(p.y);
        float radius = baseRadius + slope * min(height, beamLength);
        float width = max(radius, ray.pixelAngle * t * 0.8);
        float outside = max(length(p.xz) - 1.8 * width, height - beamLength);
        if (outside > 0.0)
        {
            t += max(outside * 0.8, 0.1 * width);
            continue;
        }
        float dt = max(0.15 * width, minStep);
        if (!started) { started = true; t += dt * jitter; p = o + d * t; height = abs(p.y); }
        float2 xz = p.xz / width;
        float q2 = dot(xz, xz);
        // パルスの塊: 根元から外へ流れる明るい区間（間隔 COSMIC_BEAM_PULSE_SPACING R）。
        float s = height - speed * time;
        float pulses = 0.45 + 0.9 * pow(0.5 + 0.5 * cos(6.2831853 * s / COSMIC_BEAM_PULSE_SPACING), 3.0);
        float core = exp(-q2 / 0.05);
        float glow = exp(-q2 / 0.35);
        float halo = exp(-q2 / 1.5) * 0.12;
        float along = smoothstep(1.0, 1.8, height) * (1.0 - smoothstep(0.55 * beamLength, beamLength, height)) * (0.35 + 0.65 * exp(-height / (0.35 * beamLength)))
            * smoothstep(0.15, 0.6, t / eyeDistance);
        float3 emission = float3(0.95, 0.95, 1.0) * core * 1.6 * pulses + float3(0.45, 0.55, 1.0) * glow * pulses + float3(0.55, 0.35, 1.0) * halo;
        color += emission * (along * (radius / width) / width * dt);
        t += dt;
    }
    return float4(color * (side * _CosmicP1.x * CosmicEmission()), 0);
}

float4 CosmicShellShade(CosmicRay ray)
{
    // mode 2 だけがジェット（mode 3 の中性子星をジェットとして描いていた）。mode 4 はパルサーのビーム。
    // 表示の重みはここで掛けます（COSMIC_SHELL_SHADE_APPLIES_FADE。広げたレンズ以外は従来どおり結果に掛けるだけ）。
    float fade = CosmicFade();
    if (abs(_CosmicP0.w - 2.0) < 0.5) return CosmicJetShade(ray) * fade;
    if (abs(_CosmicP0.w - 4.0) < 0.5) return CosmicPulsarBeamShade(ray) * fade;
    float boundary = _CosmicP0.x;
    float scale = boundary / 0.5;
    float rin = _CosmicP0.y, rout = _CosmicP0.z;
    float core = _CosmicP2.w > 0 ? _CosmicP2.w : boundary;
    uint mode = (uint)(_CosmicP0.w + 0.5);
    // 広げたレンズ（CosmicScale の梯子のクエーサー型）。CosmicEvents の OJ 287 などは _CosmicP3 = 0 で従来どおり。
    bool wide = mode == 0 && _CosmicP3.x > 0.5;
    float peakTemperature = _CosmicP1.x;
    float brightness = _CosmicP1.y * CosmicEmission();
    float t = CosmicSeconds();
    float seed = _CosmicBody.z;

    float3 x = (ray.origin + ray.direction * ray.enter) * scale;
    float3 v = ray.direction;
    float3 startDirection = v;
    bool entering = dot(x, v) < 0.0;
    // 始点を画素ごとに歩幅の範囲でずらします（揃った歩幅による同心円の縞を、細かいノイズへ変える）。
    // 物体空間ではなく画面の座標の白色ノイズにします（物体空間のずらしは同心円の縞、規則的なノイズは格子模様になる）。
    float2 pixelCoord = floor(ray.screenUV * _ScaledScreenParams.xy);
    float startJitter = CosmicHash3(int3((int)pixelCoord.x, (int)pixelCoord.y, 977)).x;
    float h2;
    {
        float r0 = length(x);
        if (wide)
        {
            // 広げたレンズは最初の歩幅が大きい（r = 150 Rs で約 12 Rs）ので、ずらす分も曲がりを入れて 1 歩積分します（h は保存量）。
            h2 = dot(cross(x, v), cross(x, v));
            float firstStep = startJitter * clamp(0.08 * r0 * r0 / (r0 + 2.0), 0.02, max(2.0, 0.08 * r0));
            float3 a0 = -1.5 * h2 * x / (r0 * r0 * r0 * r0 * r0) * (1.0 - smoothstep(core, boundary, r0));
            v += a0 * firstStep;
            x += v * firstStep;
        }
        else
        {
            x += v * startJitter * clamp(0.08 * r0 * r0 / (r0 + 2.0), 0.02, 2.0);
            h2 = dot(cross(x, v), cross(x, v));
        }
    }
    float impact = length(cross(x, v)) / boundary;
    float3 emission = 0;
    float transmittance = 1;
    bool captured = false;
    bool surfaceHit = false;
    float pixelRs = ray.pixelAngle * length(ray.origin * scale);
    // 中性子星（mode 3）: 磁軸、閃光（磁軸が視線の向きを通る瞬間ほど強い）、楕円体の y の倍率（極の半径 = 赤道半径 × (1 − 扁平率)）。
    float3 magnetic = float3(0, 1, 0);
    float flash = 0;
    float polarScale = 1;
    if (mode == 3)
    {
        magnetic = CosmicMagneticAxis(_CosmicP2.x, _CosmicP1.w, t);
        float alignment = acos(saturate(abs(dot(magnetic, normalize(ray.origin)))));
        flash = _CosmicP3.w * exp(-pow(alignment / COSMIC_PULSAR_FLASH_WIDTH, 2.0));
        polarScale = 1.0 / max(1.0 - _CosmicP3.x, 0.5);
    }
    int stepCount = CosmicIsObserverPass() ? COSMIC_BH_STEPS / 3 : COSMIC_BH_STEPS;
    [loop] for (int i = 0; i < stepCount; i++)
    {
        float r = length(x);
        // 歩幅は中心の近くほど細かく、遠くは 2 Rs まで。降着円盤のある中性子星は境界の球が約 200 Rs と大きいので、遠くは半径に比例して広げます
        // （円盤は面を横切る所で足すので、歩幅で飛び越えません。曲がりは core の外で弱めてあります）。
        // 広げたレンズ（境界 150 Rs）も同じく遠くは半径に比例させます（上限 2 Rs のままでは歩数 280 が足りず、俯瞰で円盤まで届かなかった）。
        float dt = clamp(0.08 * r * r / (r + 2.0), 0.02, mode == 3 || wide ? max(2.0, 0.08 * r) : 2.0);
        // 体積の円盤（mode 0）の中と近くでは、厚さに合わせて歩幅を細かくします（薄い層を飛び越えないため）。
        float rhoHere = length(x.xz);
        float diskHalf = 0.06 * rhoHere + 0.05;
        if (mode == 0 && abs(x.y) < diskHalf * 4.0 && rhoHere > rin * 0.85 && rhoHere < rout * 1.1) dt = min(dt, max(diskHalf * 0.45, 0.025));
        // 中心から core までは厳密な曲がり、その外は曲がりを滑らかに弱めて境界の球でゼロにします
        // （実際の曲がりは 2Rs/b で遠くまで続くため、境界で急に歪みが始まって見えないよう連続的に減衰させる近似）。
        float taper = 1.0 - smoothstep(core, boundary, r);
        float3 a = -1.5 * h2 * x / (r * r * r * r * r) * taper;
        float3 vNext = v + a * dt;
        float3 xNext = x + vNext * dt;
        if (mode == 0 && transmittance > 0.01)
        {
            // 体積の円盤: 外側ほど厚くなる（フレアした）ガスの層。密度はケプラー回転で流れる乱流（内側ほど速い）で塊と筋になり、
            // 曲がった光線に沿ってたどるので、円盤の裏側の像も光子リングの近くの像も同じ積分から出ます。
            // 源関数 S（色）と光学的厚さで積分します（emission += T·S·(1 − e^(−α·dt))）。
            float3 m = (x + xNext) * 0.5;
            float rho = length(m.xz);
            float baseThickness = 0.025 * rho + 0.05;
            if (abs(m.y) < baseThickness * 3.5 && rho > rin * 0.9 && rho < rout * 1.05)
            {
                float footprint = max(pixelRs * 0.9, baseThickness * 0.5);
                // 粗い雲（大きな塊）: 濃い所ほど厚く（上下へふくらむ）、隙間は薄く透けます。外縁も雲でちぎれた形にします。
                // 流れ（周方向に長い稜線）: 雲の上の明るい筋。半径の座標を雲でゆがめ、雲に沿って曲がる渦にします。
                float cloudNoise = CosmicQuasarDiskNoise(m, rho, t, _CosmicP1.w, rin, footprint, seed + 13.0, 2.5, 3.0, 3, 0.0);
                float cloud = smoothstep(-0.2, 0.45, cloudNoise);
                float streamNoise = CosmicQuasarDiskNoise(m, rho, t, _CosmicP1.w, rin, footprint, seed, 3.0, 11.0, 4, cloudNoise * 1.6);
                float streams = 0.35 + 1.1 * pow(saturate(1.0 - abs(streamNoise) * 2.2), 3.0);
                float thickness = baseThickness * (0.7 + 1.0 * cloud);
                float vertical = exp(-pow(m.y / thickness, 2.0));
                float raggedOut = rout * (0.78 + 0.27 * cloud);
                float edge = smoothstep(raggedOut, raggedOut * 0.8, rho) * smoothstep(rin * 0.95, rin * 1.1, rho);
                float profile = pow(rho / rin, -0.75) * pow(saturate(1.0 - sqrt(rin / rho)), 0.25) / 0.488;
                // 明るさの半径の分布（内縁の近くで 1 前後、外へ r^−COSMIC_DISK_BRIGHTNESS_SLOPE）。
                float falloff = pow(rho / rin, -COSMIC_DISK_BRIGHTNESS_SLOPE) * pow(saturate(1.0 - sqrt(rin / rho)), 0.25) / 0.45;
                float g = CosmicRedshift(m, rho, -normalize(vNext));
                // 濃さ: 雲の隙間は 25% まで薄く、流れの筋で少し濃く。
                float density = vertical * edge * lerp(0.12, 1.8, cloud) * (0.5 + 0.5 * streams);
                float temperature = peakTemperature * pow(saturate(profile), COSMIC_DISK_COLOR_RADIAL_POWER) * g;
                float3 chroma = CosmicBlackbodyChroma(temperature * COSMIC_DISK_COLOR_TEMPERATURE_SCALE);
                // ドップラー: 物理の色温度の変化（g）を強めて、近づく側を青、遠ざかる側を赤で見せます。
                float shift = saturate((g - 1.0) * 1.6 + 0.5);
                float3 tint = lerp(float3(1.0, 0.3, 0.12), float3(0.3, 0.5, 1.0), shift);
                chroma = lerp(chroma, tint, COSMIC_DISK_DOPPLER_TINT * saturate(abs(g - 1.0) * 3.0));
                // 明るさのドップラー（物理は g⁴）は g³ に弱めます。近づく側が白へ飛んで青が見えなくなるため。
                float3 source = chroma * falloff * pow(g, 3.0) * streams * lerp(0.5, 1.4, cloud);
                // 中央の面を垂直に横切るときの光学的厚さ COSMIC_DISK_OPTICAL_DEPTH（濃い円盤。後ろの星は透けません）。
                float alpha = density * COSMIC_DISK_OPTICAL_DEPTH / (thickness * 1.7725);
                float stepOpacity = 1.0 - exp(-alpha * dt);
                emission += transmittance * source * stepOpacity;
                transmittance *= 1.0 - stepOpacity;
            }
        }
        // 円盤で十分に不透明になった光線は打ち切ります（後ろは見えないので、歩数を光子リングの近くの光線へ回す）。
        if (transmittance < 0.003) { x = xNext; v = vNext; break; }
        if (mode == 1)
        {
            // 光子リング型: リング半径のまわりの厚いトーラス状の高温ガス（光学的に薄い）。
            float rr = length(xNext);
            float rho = length(xNext.xz);
            float shell = exp(-pow((rho - _CosmicP2.x) / _CosmicP2.y, 2.0)) * exp(-pow(xNext.y / (0.45 * rho + 0.3), 2.0));
            if (shell > 1e-3 && rr > 1.05)
            {
                float g = CosmicRedshift(xNext, rr, -normalize(vNext));
                // 体積の放射は歩数ぶん評価するので、オクターブを減らします（光学的に薄く、細部は平均されて見えにくい）。
                float turbulence = CosmicDiskTurbulence(xNext, rr, t, _CosmicP1.w, _CosmicP2.x, pixelRs * 0.9, seed, 2);
                float3 color = CosmicBlackbodyChroma(peakTemperature * g) * pow(g, 3.0) * shell * turbulence * (0.6 + _CosmicP2.z * 0.4);
                emission += transmittance * color * dt * 0.9;
            }
        }
        if (mode == 3)
        {
            // 降着円盤: この歩みで赤道面（y = 0）を横切ったら、その点の円盤の光を足し、不透明度の分だけ後ろを隠します。
            if (rout > 0.0 && x.y * xNext.y < 0.0 && transmittance > 0.01)
            {
                float3 m = lerp(x, xNext, x.y / (x.y - xNext.y));
                float rho = length(m.xz);
                float diskInner = _CosmicP3.y;
                if (rho > diskInner * 0.9 && rho < rout * 1.05)
                {
                    float footprint = max(pixelRs * 0.9, 0.01 * rho);
                    // 粗い雲と、雲に沿って曲がる周方向の流れ（巨大な円盤と同じ模様。内縁の 1 周は自転と同じ表示上の周期 = 共回転半径の近く）。
                    float cloudNoise = CosmicQuasarDiskNoise(m, rho, t, _CosmicP1.w, diskInner, footprint, seed + 13.0, 2.5, 3.0, 3, 0.0);
                    float cloud = smoothstep(-0.2, 0.45, cloudNoise);
                    float streamNoise = CosmicQuasarDiskNoise(m, rho, t, _CosmicP1.w, diskInner, footprint, seed, 3.0, 11.0, 3, cloudNoise * 1.6);
                    float streams = 0.35 + 1.1 * pow(saturate(1.0 - abs(streamNoise) * 2.2), 3.0);
                    float raggedOut = rout * (0.78 + 0.27 * cloud);
                    float edge = smoothstep(raggedOut, raggedOut * 0.8, rho) * smoothstep(diskInner * 0.9, diskInner * 1.15, rho);
                    float profile = pow(rho / diskInner, -0.75) * pow(saturate(1.0 - sqrt(diskInner / rho)), 0.25) / 0.488;
                    float falloff = pow(rho / diskInner, -COSMIC_DISK_BRIGHTNESS_SLOPE) * pow(saturate(1.0 - sqrt(diskInner / rho)), 0.25) / 0.45;
                    float g = CosmicRedshift(m, rho, -normalize(vNext));
                    // 色は巨大な円盤（mode 0）と同じ規約: 表示の色温度を半径の分布の COSMIC_DISK_COLOR_RADIAL_POWER 乗にし、中心は白く外縁は赤くします。
                    float temperature = _CosmicP3.z * pow(saturate(profile), COSMIC_DISK_COLOR_RADIAL_POWER) * g;
                    float3 source = CosmicBlackbodyChroma(temperature * COSMIC_DISK_COLOR_TEMPERATURE_SCALE) * falloff * pow(g, 3.0) * streams * lerp(0.5, 1.4, cloud);
                    float cover = edge * lerp(0.55, 0.97, cloud);
                    // 最後に全体へ表面の明るさ（_CosmicP1.y）を掛けるので、円盤の明るさとの比にします。
                    emission += transmittance * source * cover * (_CosmicP1.z / max(_CosmicP1.y, 1e-6));
                    transmittance *= 1.0 - cover;
                }
            }
            // 中性子星の表面（回転楕円体）: この歩みで表面の中へ入ったら、y を polarScale 倍した座標（球になる）で線分との交点を求め、表面の色を出します。
            float3 qNext = float3(xNext.x, xNext.y * polarScale, xNext.z);
            if (dot(qNext, qNext) < rin * rin)
            {
                float3 d = xNext - x;
                float3 q = float3(x.x, x.y * polarScale, x.z);
                float3 dq = qNext - q;
                float a2 = dot(dq, dq), b2 = dot(q, dq), c2 = dot(q, q) - rin * rin;
                float s = saturate((-b2 - sqrt(max(b2 * b2 - a2 * c2, 0.0))) / max(a2, 1e-8));
                float3 hit = x + d * s;
                float3 n = normalize(float3(hit.x, hit.y * polarScale * polarScale, hit.z));
                float g = sqrt(saturate(1.0 - 1.0 / length(hit)));
                float spotRadius = max(_CosmicP2.y, 1e-3);
                float north = acos(clamp(dot(n, magnetic), -1.0, 1.0)), south = acos(clamp(dot(n, -magnetic), -1.0, 1.0));
                float spots = exp(-pow(north / spotRadius, 2.0)) + exp(-pow(south / spotRadius, 2.0));
                // 表面の色: 色温度と強度に重力赤方偏移 g（強度は g⁴ を表示用に g³ に弱める。ブラックホールの円盤と同じ割り切り）。
                // 視線と法線の角度による周縁減光は弱く入れます（光が曲がるので縁でも表面がよく見える）。閃光の間は表面も明るくします。
                float mu = saturate(dot(n, -normalize(vNext)));
                float3 baseColor = CosmicBlackbodyChroma(peakTemperature * g) * pow(g, 3.0) * (0.75 + 0.25 * mu);
                float3 spotColor = CosmicBlackbodyChroma(peakTemperature * 1.6 * g) * pow(g, 3.0) * spots * _CosmicP2.z;
                emission += transmittance * (baseColor + spotColor) * (1.0 + 0.15 * flash);
                transmittance = 0.0;
                surfaceHit = true;
                x = hit; v = vNext;
                break;
            }
        }
        x = xNext; v = vNext;
        if (dot(x, x) < 1.0) { captured = true; break; }
        if (dot(x, x) > boundary * boundary * 1.0001 && dot(x, v) > 0.0) break;
    }
    // 歩数が尽きても、衝突径数が臨界値より小さく内向きに入った光線は必ず地平面へ落ちます（シュバルツシルトの厳密な条件、h は保存量）。
    // 円盤の近くで歩幅を細かくすると歩数が尽き、影の中に曲げた星空が出ていました。
    if (mode != 3 && !captured && entering && h2 < COSMIC_BH_CRITICAL_IMPACT * COSMIC_BH_CRITICAL_IMPACT) captured = true;
    // 不透明度は「光の曲がりが何画素か」で決めます。曲がりが1画素未満の所は透明にして後ろの本物の景色をそのまま見せ、
    // それより内側を曲げた星空で置き換えます。曲がりは中心から外へ連続的に小さくなるので、置き換えの境目は見えません。
    // （画面内の天体を不透明テクスチャから引いて曲げる方式は、手前の天体のずれた二重像を出したため使いません。）
    // acos(dot) は小さい角で丸め誤差が大きく（1 − dot の最小単位で約 3e-4 rad ≒ 0.3 画素）、曲がっていない光線にも
    // 偽の不透明度が出て境界の球の縁が直線の境目として見えたため、外積の大きさとの atan2 で求めます。
    if (mode == 3 && flash > 1e-3 && !surfaceHit)
    {
        // 閃光の暈: 見かけの円板（衝突径数 R/√(1 − 1/R)）のすぐ外に、外へ指数で減る青白い光を足します（円盤の後ろは隠れる）。
        float apparent = rin * rsqrt(max(1.0 - 1.0 / rin, 1e-3));
        float glow = flash * exp(-max(sqrt(h2) - apparent, 0.0) / (0.2 * apparent));
        emission += transmittance * float3(0.75, 0.85, 1.0) * glow * 0.5;
    }
    float rawDepth = SampleSceneDepth(ray.screenUV);
    // 観察者の目（ワイプ）では、物体空間の視線の復元が近似（曲がりを大きく誤り、背景とは別の星空を広く塗っていた）なので置き換えません。
    bool sky = rawDepth == UNITY_RAW_FAR_CLIP_VALUE && !CosmicIsObserverPass();
    float3 endDirection = normalize(v);
    if (wide)
    {
        // 現れる途中（表示の重み < 1）は曲がりを重みに比例させます（曲げた星空をアルファで重ねると、球の広い範囲で星空が二重になった）。
        // 先に描いた天体のレンズが曲げる光線では置き換えず、その縁で曲がりを 0 から戻します（境目で星が跳ばない）。
        float bendScale = fade;
        if (sky) bendScale *= CosmicLensGuardScale(ray.origin, startDirection, (int)(_CosmicP3.y + 0.5));
        if (bendScale < 1.0) endDirection = normalize(lerp(startDirection, endDirection, bendScale));
    }
    float bend = atan2(length(cross(endDirection, startDirection)), dot(endDirection, startDirection));
    float edge = 1.0 - smoothstep(0.92, 1.0, impact);
    // 広げたレンズの影は表示の重みで現れます（星空の置き換えは上で曲がりごと重みに比例させてある）。
    float coverage = captured || surfaceHit ? (wide ? fade : 1.0) : (sky ? smoothstep(0.4, 1.5, bend / max(ray.pixelAngle, 1e-6)) * edge : 0.0);
    float3 background = captured || surfaceHit || !sky ? 0 : CosmicBackground(endDirection, ray.pixelAngle);
    // 発光の倍率は体積の円盤（mode 0）だけに掛けます（光子リング型の mode 1 に掛けると、いて座A*・M87 のガスが白く飛んだ）。
    float gain = mode == 0 ? COSMIC_DISK_EMISSION_GAIN : 1.0;
    if (wide)
    {
        // 円盤の光と、円盤が後ろを隠す度合いは表示の重みで現れます。星空は重みを掛けずに置き換えます（重みは曲がりに入れてある）。
        float occlusion = (1.0 - transmittance) * fade;
        return float4(emission * brightness * gain * fade + background * (1.0 - occlusion) * coverage, max(coverage, occlusion));
    }
    float3 color = emission * brightness * gain + background * transmittance * coverage;
    return float4(color, max(coverage, 1.0 - transmittance)) * fade;
}

#endif
