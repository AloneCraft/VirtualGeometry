using System;

namespace Omni.PlanetSurface{
    /// <summary>天体固有の岩屑分布です。描画品質から独立して保存します。</summary>
    [Serializable]
    public sealed class SurfaceRockSettings{
        public int AlgorithmVersion=1;
        public double CellSpanMeters=4;
        public double DensityPerSquareMeter=0.04;
        public double MinimumDiameterMeters=0.04;
        public double MaximumDiameterMeters=0.8;

        public void Validate(){
            if(this.AlgorithmVersion!=1){throw new NotSupportedException("岩生成版が未対応です。");}
            if(!double.IsFinite(this.CellSpanMeters)||this.CellSpanMeters<=0||
                !double.IsFinite(this.DensityPerSquareMeter)||this.DensityPerSquareMeter<0||
                this.DensityPerSquareMeter*this.CellSpanMeters*this.CellSpanMeters>1||
                !double.IsFinite(this.MinimumDiameterMeters)||this.MinimumDiameterMeters<=0||
                !double.IsFinite(this.MaximumDiameterMeters)||this.MaximumDiameterMeters<this.MinimumDiameterMeters){
                throw new ArgumentOutOfRangeException(nameof(SurfaceRockSettings));
            }
        }
    }

    /// <summary>LODに依存しない岩のID・方向・実寸・形状seedです。地表高度は共通正本から別途評価します。</summary>
    public readonly struct SurfaceRockCandidate{
        public readonly SurfaceTileKey Key;
        public readonly SurfaceVector Direction;
        public readonly double DiameterMeters;
        public readonly double YawRadians;
        public readonly uint ShapeSeed;

        public SurfaceRockCandidate(SurfaceTileKey key,SurfaceVector direction,double diameter,double yaw,uint shapeSeed){
            this.Key=key;this.Direction=direction;this.DiameterMeters=diameter;this.YawRadians=yaw;this.ShapeSeed=shapeSeed;
        }
    }

    /// <summary>月面検証とEarthToMoonで共有する固定区画の岩配置正本です。</summary>
    public sealed class SurfaceRockField{
        private readonly uint seed;
        private readonly double density,minimumDiameter,maximumDiameter;
        public int Level{get;}
        public double RadiusMeters{get;}
        public string GenerationHash{get;}

        /// <summary>候補を移動せず、造成の重みと固定形状seedだけで岩を除外します。品質・探索順序には依存しません。</summary>
        public static bool IsAllowedBySurface(IPlanetSurfaceHeightSource source,SurfaceRockCandidate candidate){
            if(source==null){throw new ArgumentNullException(nameof(source));}
            if(source is not IProceduralSurfaceHeightSource procedural||!procedural.HasHeightOverrides){return true;}
            double weight=procedural.SampleHeightBlend(candidate.Direction).ScatterWeight;
            return Unit(Mix(candidate.ShapeSeed^0xc2b2ae35u))<weight;
        }

        public SurfaceRockField(double radiusMeters,int seed,SurfaceRockSettings settings){
            if(settings==null){throw new ArgumentNullException(nameof(settings));}
            settings.Validate();
            if(!double.IsFinite(radiusMeters)||radiusMeters<=0){throw new ArgumentOutOfRangeException(nameof(radiusMeters));}
            double requested=Math.Ceiling(Math.Log(radiusMeters,2)+1-Math.Log(settings.CellSpanMeters,2));
            if(requested>30){throw new ArgumentOutOfRangeException(nameof(radiusMeters),"岩の整数区画IDの範囲を超えています。");}
            this.RadiusMeters=radiusMeters;
            this.Level=Math.Max(0,(int)requested);
            this.seed=unchecked((uint)seed);this.density=settings.DensityPerSquareMeter;
            this.minimumDiameter=settings.MinimumDiameterMeters;this.maximumDiameter=settings.MaximumDiameterMeters;
            string canonical=FormattableString.Invariant($"rock-v{settings.AlgorithmVersion}|radius={radiusMeters:R}|seed={seed}|cell={settings.CellSpanMeters:R}|density={this.density:R}|min={this.minimumDiameter:R}|max={this.maximumDiameter:R}");
            using(var sha=System.Security.Cryptography.SHA256.Create()){
                this.GenerationHash=BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(canonical))).Replace("-","").ToLowerInvariant();
            }
        }

        /// <summary>固定区画の候補を評価します。空区画はfalseです。</summary>
        public bool TryGet(SurfaceTileKey key,out SurfaceRockCandidate candidate){
            if(key.Face<0||key.Face>5||key.Level!=this.Level||key.X<0||key.Y<0||
                key.X>=(1<<this.Level)||key.Y>=(1<<this.Level)){
                throw new ArgumentOutOfRangeException(nameof(key));
            }
            candidate=default;
            uint hash;
            unchecked{hash=Mix((uint)key.X*0x8da6b343u^(uint)key.Y*0xd8163841u^
                (uint)key.Face*0xcb1ab31fu^(uint)key.Level*0x9e3779b9u^this.seed);}
            double span=2.0/(1<<this.Level);
            double a=-1+span*(key.X+Unit(Mix(hash^0x68bc21ebu)));
            double b=-1+span*(key.Y+Unit(Mix(hash^0x02e5be93u)));
            // Cube投影の面積縮小を採用率で補い、面の隅だけ岩が密集するのを防ぎます。
            double cellMeters=this.RadiusMeters*span;
            double probability=this.density*cellMeters*cellMeters/Math.Pow(1+a*a+b*b,1.5);
            if(Unit(Mix(hash^0x967a889bu))>=probability){return false;}
            double size=Unit(Mix(hash^0xa511e9b3u));
            double diameter=this.minimumDiameter+(this.maximumDiameter-this.minimumDiameter)*size*size*size;
            candidate=new SurfaceRockCandidate(key,SurfaceGeometry.CubeDirection(key.Face,a,b),diameter,
                2*Math.PI*Unit(Mix(hash^0x63d83595u)),Mix(hash^0xb5297a4du));
            return true;
        }

        /// <summary>球面上の弦距離メートル内を少しずつ探索します。方向は非ゼロの有限値が必要です。</summary>
        public SurfaceRockQuery Query(SurfaceVector direction,double distanceMeters){
            if(!double.IsFinite(direction.X)||!double.IsFinite(direction.Y)||!double.IsFinite(direction.Z)||
                !double.IsFinite(direction.Length)||direction.Length<=0||
                !double.IsFinite(distanceMeters)||distanceMeters<0){throw new ArgumentOutOfRangeException(nameof(direction));}
            return new SurfaceRockQuery(this,direction.Normalized,distanceMeters);
        }

        // 両端を除く値にしてCube面の所有境界そのものへ候補を置きません。
        private static double Unit(uint value)=>((value&0x00ffffffu)+0.5)/16777216.0;
        private static uint Mix(uint value){
            unchecked{value^=value>>16;value*=0x7feb352du;value^=value>>15;value*=0x846ca68bu;return value^(value>>16);}
        }
    }

    /// <summary>一度に1区画だけ処理する近傍探索です。フレーム予算の境界で中断できます。</summary>
    public sealed class SurfaceRockQuery{
        private readonly SurfaceRockField field;
        private readonly SurfaceVector direction;
        private readonly double distance;
        private readonly SurfaceTileKey[] stack;
        private int count;
        public bool IsComplete=>this.count==0;
        public int VisitedNodes{get;private set;}

        internal SurfaceRockQuery(SurfaceRockField field,SurfaceVector direction,double distance){
            this.field=field;this.direction=direction;this.distance=distance;
            // 深さ優先走査なので全球の区画数ではなく最大深さだけに比例します。
            this.stack=new SurfaceTileKey[6+3*field.Level];
            for(int face=5;face>=0;face--){this.stack[this.count++]=new SurfaceTileKey(face,0,0,0);}
        }

        /// <summary>候補が得られた場合だけtrueです。空区画でも1step進み、完了はIsCompleteで確認します。</summary>
        public bool Step(out SurfaceRockCandidate candidate){
            candidate=default;
            if(this.IsComplete){return false;}
            SurfaceTileKey key=this.stack[--this.count];this.VisitedNodes++;
            double span=2.0/(1<<key.Level);
            SurfaceVector center=SurfaceGeometry.CubeDirection(key.Face,-1+span*(key.X+0.5),-1+span*(key.Y+0.5));
            // Cube面内では正規化の微分ノルム<=1。半対角線は球面上の弦距離の保守的上限です。
            double bound=this.field.RadiusMeters*span*Math.Sqrt(0.5);
            if(((center-this.direction)*this.field.RadiusMeters).Length>this.distance+bound){return false;}
            if(key.Level<this.field.Level){
                for(int child=3;child>=0;child--){this.stack[this.count++]=key.Child(child%2,child/2);}
                return false;
            }
            if(!this.field.TryGet(key,out var rock)||
                ((rock.Direction-this.direction)*this.field.RadiusMeters).Length>this.distance){return false;}
            candidate=rock;return true;
        }
    }
}
