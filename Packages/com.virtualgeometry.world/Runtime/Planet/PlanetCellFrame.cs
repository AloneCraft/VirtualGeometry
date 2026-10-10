using UnityEngine;
using BigInteger=System.Numerics.BigInteger;
using LogicalIntRaw=Omni.Logical.IntRaw;
using LogicalMath=Omni.Logical.Math;
using LogicalScalar=Omni.Logical.Scalar;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.World{
    /// <summary>
    /// Cell の中心の値を Logical 精度(Omni の Q95.96 固定小数点)で持つ。
    /// GPU は Cell の中心からの相対量だけを float で計算し(PlanetTerrainGenerate.compute)、
    /// float にすると精度が足りない値(中心の正接、Cell の原点、ノイズの格子の基点)はここで求める。
    /// 中心の角度は π·n/2^(L+2)(n=2x+1−2^L)で、n/2^(L+2) を固定小数点で正確に作るので、階層は <see cref="MaximumLevel"/> までに限る。
    /// </summary>
    public readonly struct PlanetCellFrame{
        /// <summary>中心の角度の分数 n/2^(L+2) を小数部 96 ビットで正確に表せる最大の階層。</summary>
        public const int MaximumLevel=LogicalScalar.FractionalBits-2;
        // テイラー展開の項数。|角度|≤π/4 で、打ち切り誤差は 2^-96 より小さい。
        private const int SeriesTerms=14;

        public readonly PlanetCellKey Key;
        /// <summary>中心の面の座標の正接(等角写像の x0、y0)。</summary>
        public readonly LogicalScalar TangentU;
        public readonly LogicalScalar TangentV;
        /// <summary>惑星のローカル座標での Cell の原点 R·p0/|p0|(p0=n+x0·u+y0·v)。頂点はここからの相対位置。</summary>
        public readonly LogicalVector3 Origin;
        /// <summary>Origin の float の近似(選択・bounds・Unity の world 空間での配置用)。</summary>
        public readonly Vector3 ApproximateOrigin;
        /// <summary>TangentU、TangentV の float の近似(GPU の生成用)。</summary>
        public readonly Vector2 ApproximateTangent;

        private PlanetCellFrame(in PlanetCellKey key,in LogicalScalar tangentU,in LogicalScalar tangentV,in LogicalVector3 origin,Vector3 approximateOrigin,Vector2 approximateTangent){
            this.Key=key;this.TangentU=tangentU;this.TangentV=tangentV;this.Origin=origin;
            this.ApproximateOrigin=approximateOrigin;this.ApproximateTangent=approximateTangent;
        }

        public static bool TryCreate(in PlanetCellKey key,in LogicalScalar radius,out PlanetCellFrame frame){
            frame=default;
            if(key.Level>MaximumLevel){return false;}
            if(!TryCenterTangent(key.X,key.Level,out LogicalScalar x0)||!TryCenterTangent(key.Y,key.Level,out LogicalScalar y0)){return false;}
            Vector3Int n=CubeSphere.Normal(key.Face);Vector3Int u=CubeSphere.U(key.Face);Vector3Int v=CubeSphere.V(key.Face);
            if(!TryComponent(n.x,u.x,v.x,in x0,in y0,out LogicalScalar px)||
                !TryComponent(n.y,u.y,v.y,in x0,in y0,out LogicalScalar py)||
                !TryComponent(n.z,u.z,v.z,in x0,in y0,out LogicalScalar pz)){
                return false;
            }
            // |p0|² = 1 + x0² + y0²(3 以下)。R を掛けるのは正規化の後(R² を作らない)。
            if(!LogicalScalar.TryMultiply(in x0,in x0,out LogicalScalar xx)||!LogicalScalar.TryMultiply(in y0,in y0,out LogicalScalar yy)||
                !LogicalScalar.TryAdd(in xx,in yy,out LogicalScalar sum)||!LogicalScalar.TryAdd(LogicalScalar.One,in sum,out LogicalScalar lengthSquared)||
                !LogicalScalar.TryReciprocalSqrt(in lengthSquared,out LogicalScalar inverseLength)){
                return false;
            }
            if(!TryScale(in px,in inverseLength,in radius,out LogicalScalar ox)||!TryScale(in py,in inverseLength,in radius,out LogicalScalar oy)||
                !TryScale(in pz,in inverseLength,in radius,out LogicalScalar oz)){
                return false;
            }
            var origin=new LogicalVector3(in ox,in oy,in oz);
            if(!origin.TryToFloat3(out Unity.Mathematics.float3 approximate)||!x0.TryToFloat(out float tangentU)||!y0.TryToFloat(out float tangentV)){return false;}
            frame=new PlanetCellFrame(in key,in x0,in y0,in origin,approximate,new Vector2(tangentU,tangentV));
            return true;
        }

        /// <summary>
        /// 波長の逆数 inverseWavelength·2^octave の格子で、原点の格子番号(下位 32 ビット、負の無限大方向へ切り捨て)と端数(0〜1)を返す。
        /// 格子番号は 2^32 で巡回する(ノイズの hash は uint で巡回するので、値は変わらない)。
        /// </summary>
        public bool TryNoiseLattice(in LogicalScalar inverseWavelength,int octave,out Vector3Int lattice,out Vector3 fraction){
            lattice=default;fraction=default;
            if(!TryLattice(this.Origin.X,in inverseWavelength,octave,out int lx,out float fx)||
                !TryLattice(this.Origin.Y,in inverseWavelength,octave,out int ly,out float fy)||
                !TryLattice(this.Origin.Z,in inverseWavelength,octave,out int lz,out float fz)){
                return false;
            }
            lattice=new Vector3Int(lx,ly,lz);fraction=new Vector3(fx,fy,fz);
            return true;
        }

        /// <summary>
        /// 単位方向 direction・波長の逆数 inverseWavelength の平面波の、原点での位相(周期の端数、0〜1)。
        /// (direction·原点)/波長 を Logical で求めて端数だけを取るので、惑星が大きくても位相が潰れない。
        /// </summary>
        public bool TryWavePhase(in LogicalVector3 direction,in LogicalScalar inverseWavelength,out float fraction){
            fraction=0;
            if(!LogicalVector3.TryDot(in direction,this.Origin,out LogicalScalar along)||!LogicalScalar.TryMultiply(in along,in inverseWavelength,out LogicalScalar cycles)){
                return false;
            }
            // 2 の補数の raw の Limb1 の下位 32 ビットが、負の無限大方向へ切り捨てた後の端数の上位 32 ビット。
            fraction=(cycles.Raw.Limb1&0xffffffffUL)*(1f/4294967296f);
            return true;
        }

        private static bool TryLattice(in LogicalScalar coordinate,in LogicalScalar inverseWavelength,int octave,out int lattice,out float fraction){
            lattice=0;fraction=0;
            if(!LogicalScalar.TryMultiply(in coordinate,in inverseWavelength,out LogicalScalar scaled)||
                !LogicalScalar.TryScaleByPowerOfTwo(in scaled,octave,out LogicalScalar value)){
                return false;
            }
            // 2 の補数の raw は、整数部が負の無限大方向への切り捨てになる。Limb1 の上位 32 ビットが整数部の下位 32 ビット。
            LogicalIntRaw raw=value.Raw;
            lattice=unchecked((int)(uint)(raw.Limb1>>32));
            fraction=(raw.Limb1&0xffffffffUL)*(1f/4294967296f);
            return true;
        }

        // 中心の面の座標の正接 tan(π·n/2^(L+2))、n=2x+1−2^L。
        private static bool TryCenterTangent(in PlanetCellCoordinate coordinate,int level,out LogicalScalar tangent){
            tangent=default;
            BigInteger x=((BigInteger)coordinate.High<<64)|coordinate.Low;
            BigInteger numerator=2*x+1-(BigInteger.One<<level);
            if(!TryFromFraction(numerator,level+2,out LogicalScalar fraction)||
                !LogicalScalar.TryMultiply(in fraction,LogicalMath.Pi,out LogicalScalar angle)){
                return false;
            }
            return TryTangent(in angle,out tangent);
        }

        // numerator/2^shift(|値|<1)を、raw = numerator·2^(96−shift) で正確に作る。
        private static bool TryFromFraction(BigInteger numerator,int shift,out LogicalScalar value){
            value=default;
            int up=LogicalScalar.FractionalBits-shift;
            if(up<0){return false;}
            BigInteger raw=numerator<<up;
            if(raw.Sign<0){raw+=BigInteger.One<<192;}
            BigInteger mask=(BigInteger.One<<64)-1;
            ulong limb0=(ulong)(raw&mask);ulong limb1=(ulong)((raw>>64)&mask);ulong limb2=(ulong)((raw>>128)&mask);
            value=new LogicalScalar(new LogicalIntRaw(limb0,limb1,limb2));
            return true;
        }

        // |angle|≤π/4 の tan。sin と cos を Horner 形のテイラー展開で求めて割る(固定小数点の絶対誤差は数 2^-96)。
        private static bool TryTangent(in LogicalScalar angle,out LogicalScalar tangent){
            tangent=default;
            if(!LogicalScalar.TryMultiply(in angle,in angle,out LogicalScalar square)){return false;}
            LogicalScalar sine=LogicalScalar.One;LogicalScalar cosine=LogicalScalar.One;
            for(int k=SeriesTerms;k>=1;k--){
                if(!TryHornerStep(in square,in sine,(2*k)*(2*k+1),out LogicalScalar nextSine)||!TryHornerStep(in square,in cosine,(2*k-1)*(2*k),out LogicalScalar nextCosine)){return false;}
                sine=nextSine;cosine=nextCosine;
            }
            if(!LogicalScalar.TryMultiply(in angle,in sine,out LogicalScalar scaledSine)){return false;}
            return LogicalScalar.TryDivide(in scaledSine,in cosine,out tangent);
        }

        // 1 − square·value/divisor
        private static bool TryHornerStep(in LogicalScalar square,in LogicalScalar value,int divisor,out LogicalScalar result){
            result=default;
            if(!LogicalScalar.TryFromFloat(divisor,out LogicalScalar denominator)||
                !LogicalScalar.TryMultiply(in square,in value,out LogicalScalar product)||
                !LogicalScalar.TryDivide(in product,in denominator,out LogicalScalar term)){
                return false;
            }
            return LogicalScalar.TrySubtract(LogicalScalar.One,in term,out result);
        }

        // n_i + x0·u_i + y0·v_i(n、u、v の成分は −1、0、1)。
        private static bool TryComponent(int normal,int axisU,int axisV,in LogicalScalar x0,in LogicalScalar y0,out LogicalScalar result){
            result=default;
            // 入力と出力に同じ変数を渡さない(出力の書き込みが入力を壊さないように)。
            if(!TryAddSigned(LogicalScalar.Zero,LogicalScalar.One,normal,out LogicalScalar first)){return false;}
            if(!TryAddSigned(in first,in x0,axisU,out LogicalScalar second)){return false;}
            return TryAddSigned(in second,in y0,axisV,out result);
        }

        private static bool TryAddSigned(in LogicalScalar total,in LogicalScalar value,int sign,out LogicalScalar result){
            if(sign>0){return LogicalScalar.TryAdd(in total,in value,out result);}
            if(sign<0){return LogicalScalar.TrySubtract(in total,in value,out result);}
            result=total;
            return true;
        }

        private static bool TryScale(in LogicalScalar component,in LogicalScalar inverseLength,in LogicalScalar radius,out LogicalScalar result){
            result=default;
            return LogicalScalar.TryMultiply(in component,in inverseLength,out LogicalScalar unit)&&LogicalScalar.TryMultiply(in unit,in radius,out result);
        }
    }
}
