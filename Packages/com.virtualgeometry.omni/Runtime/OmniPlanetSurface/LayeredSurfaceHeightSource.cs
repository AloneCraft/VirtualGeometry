using System;
using System.Text;
using System.Security.Cryptography;

namespace Omni.PlanetSurface{
    /// <summary>自然標高へ適用する affine 合成です。施設の岩除外は標高とは別の重みで扱います。</summary>
    public readonly struct SurfaceHeightBlend{
        public readonly double NaturalWeight,OffsetHeightMeters,ScatterWeight;
        public SurfaceHeightBlend(double naturalWeight,double offsetHeightMeters,double scatterWeight=1){
            if(!double.IsFinite(naturalWeight)||naturalWeight<0||naturalWeight>1||
                !double.IsFinite(offsetHeightMeters)||!double.IsFinite(scatterWeight)||scatterWeight<0||scatterWeight>1){
                throw new ArgumentOutOfRangeException(nameof(naturalWeight));
            }
            this.NaturalWeight=naturalWeight;this.OffsetHeightMeters=offsetHeightMeters;this.ScatterWeight=scatterWeight;
        }
        public static SurfaceHeightBlend Identity=>new(1,0);
        public double Apply(double naturalHeight)=>this.NaturalWeight*naturalHeight+this.OffsetHeightMeters;
    }

    /// <summary>局所実測・施設造成の不変レイヤー。内容変更時は必ず CacheIdentity を変更します。</summary>
    public interface ISurfaceHeightOverride{
        string CacheIdentity{get;}
        double MaximumAbsoluteHeightMeters{get;}
        SurfaceHeightBlend Sample(SurfaceVector direction);
    }

    /// <summary>CPU標高とGPU法線に、同じ自然正本と明示的な上書きを渡します。</summary>
    public interface IProceduralSurfaceHeightSource:IBandLimitedSurfaceHeightSource{
        ConstrainedSurfaceEvaluator NaturalSource{get;}
        bool HasHeightOverrides{get;}
        SurfaceHeightBlend SampleHeightBlend(SurfaceVector direction);
        SurfaceDerivedKey CreateDerivedKey(string sourceHash,SurfaceTileKey region,int resolution);
    }

    /// <summary>任意天体の局所矩形を球面座標で評価します。EarthToMoon の局所標高と施設造成に使用します。</summary>
    public sealed class LocalSurfaceHeightOverride:ISurfaceHeightOverride{
        private readonly double radius,halfWidth,halfHeight,feather;
        private readonly SurfaceVector center,east,north;
        private readonly Func<double,double,double> sample;
        private readonly bool clearRocks;
        public string CacheIdentity{get;}
        public double MaximumAbsoluteHeightMeters{get;}

        /// <summary>寸法・標高はメートル。sample と contentHash は寿命中不変の同じデータを表す必要があります。</summary>
        public LocalSurfaceHeightOverride(double radius,SurfaceVector center,SurfaceVector east,
            double halfWidth,double halfHeight,double feather,Func<double,double,double> sample,
            double maximumAbsoluteHeightMeters,string contentHash,bool clearRocks){
            if(!double.IsFinite(radius)||radius<=0||!double.IsFinite(halfWidth)||halfWidth<=0||
                !double.IsFinite(halfHeight)||halfHeight<=0||Math.Max(halfWidth,halfHeight)>=radius*Math.PI/2||
                !double.IsFinite(feather)||feather<=0||feather>Math.Min(halfWidth,halfHeight)||
                !double.IsFinite(maximumAbsoluteHeightMeters)||maximumAbsoluteHeightMeters<0){
                throw new ArgumentOutOfRangeException(nameof(radius));
            }
            if(string.IsNullOrWhiteSpace(contentHash)){throw new ArgumentException("局所データの内容hashが必要です。",nameof(contentHash));}
            this.center=NormalizeDirection(center);
            east=NormalizeDirection(east);
            SurfaceVector tangent=east-this.center*Dot(east,this.center);
            if(tangent.Length<1e-12){throw new ArgumentOutOfRangeException(nameof(east),"中心軸と東軸は独立である必要があります。");}
            this.east=NormalizeDirection(tangent);
            this.north=SurfaceVector.Cross(this.east,this.center);
            this.radius=radius;this.halfWidth=halfWidth;this.halfHeight=halfHeight;this.feather=feather;
            this.sample=sample??throw new ArgumentNullException(nameof(sample));this.clearRocks=clearRocks;
            this.MaximumAbsoluteHeightMeters=maximumAbsoluteHeightMeters;
            this.CacheIdentity=FormattableString.Invariant($"local-v1|{contentHash.Length}:{contentHash}|r={radius:R}|c={this.center.X:R},{this.center.Y:R},{this.center.Z:R}|e={this.east.X:R},{this.east.Y:R},{this.east.Z:R}|w={halfWidth:R}|h={halfHeight:R}|f={feather:R}|max={maximumAbsoluteHeightMeters:R}|clear={clearRocks}");
        }

        /// <summary>矩形外・裏側は恒等変換です。内外の境界では smoothstep の傾斜を0にします。</summary>
        public SurfaceHeightBlend Sample(SurfaceVector direction){
            direction=NormalizeDirection(direction);
            double forward=Dot(direction,this.center);
            if(forward<=0){return SurfaceHeightBlend.Identity;}
            double e=Dot(direction,this.east),n=Dot(direction,this.north);
            double x=Math.Atan2(e,forward)*this.radius;
            double z=Math.Atan2(n,Math.Sqrt(e*e+forward*forward))*this.radius;
            double t=Math.Clamp(Math.Min(this.halfWidth-Math.Abs(x),this.halfHeight-Math.Abs(z))/this.feather,0,1);
            if(t<=0){return SurfaceHeightBlend.Identity;}
            double weight=t*t*(3-2*t),height=this.sample(x,z);
            if(!double.IsFinite(height)||Math.Abs(height)>this.MaximumAbsoluteHeightMeters){
                throw new InvalidOperationException("局所標高が宣言した有限範囲を超えています。");
            }
            double scatter=1;if(this.clearRocks){scatter=1-weight;}
            return new SurfaceHeightBlend(1-weight,weight*height,scatter);
        }
        private static double Dot(SurfaceVector a,SurfaceVector b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
        /// <summary>ゼロ・非有限値を補間やキャッシュ識別子へ混入させません。</summary>
        internal static SurfaceVector NormalizeDirection(SurfaceVector value){
            double length=value.Length;
            if(!double.IsFinite(length)||length<=0){throw new ArgumentOutOfRangeException(nameof(value));}
            return value/length;
        }
    }

    /// <summary>基準標高の置換後に生成細部を加え、最後に施設造成を合成します。EarthToMoonの共通地形です。</summary>
    public sealed class LayeredSurfaceHeightSource:IProceduralSurfaceHeightSource{
        private readonly ISurfaceHeightOverride[] baseLayers,fullHeightLayers;
        private readonly string layerHash;
        public ConstrainedSurfaceEvaluator NaturalSource{get;}
        public bool HasHeightOverrides=>this.baseLayers.Length>0||this.fullHeightLayers.Length>0;
        public double MaximumAbsoluteHeightMeters{get;}
        /// <summary>従来の全標高上書き契約です。生成細部を残す局所実測には3引数の形式を使用します。</summary>
        public LayeredSurfaceHeightSource(ConstrainedSurfaceEvaluator natural,params ISurfaceHeightOverride[] layers)
            :this(natural,Array.Empty<ISurfaceHeightOverride>(),layers){
        }
        /// <summary>baseLayersは低周波だけ、fullHeightLayersは生成細部込みの高さを順序付きで置換します。null配列は禁止です。</summary>
        public LayeredSurfaceHeightSource(ConstrainedSurfaceEvaluator natural,ISurfaceHeightOverride[] baseLayers,
            ISurfaceHeightOverride[] fullHeightLayers){
            this.NaturalSource=natural??throw new ArgumentNullException(nameof(natural));
            this.baseLayers=(ISurfaceHeightOverride[])(baseLayers??throw new ArgumentNullException(nameof(baseLayers))).Clone();
            this.fullHeightLayers=(ISurfaceHeightOverride[])(fullHeightLayers??throw new ArgumentNullException(nameof(fullHeightLayers))).Clone();
            var canonical=new StringBuilder("layers-v2|base|");
            double bound=AppendLayers(this.baseLayers,canonical,natural.MaximumBaseHeightMeters)+natural.MaximumDetailHeightMeters;
            canonical.Append("|full|");
            bound=AppendLayers(this.fullHeightLayers,canonical,bound);
            this.MaximumAbsoluteHeightMeters=bound;this.layerHash=Hash(canonical.ToString());
        }
        private static double AppendLayers(ISurfaceHeightOverride[] layers,StringBuilder canonical,double bound){
            canonical.Append(layers.Length).Append('|');
            foreach(var layer in layers){
                if(layer==null||string.IsNullOrWhiteSpace(layer.CacheIdentity)||
                    !double.IsFinite(layer.MaximumAbsoluteHeightMeters)||layer.MaximumAbsoluteHeightMeters<0){
                    throw new ArgumentException("レイヤーと不変識別子・有限標高範囲が必要です。",nameof(layers));
                }
                canonical.Append(layer.CacheIdentity.Length).Append(':').Append(layer.CacheIdentity);
                bound=Math.Max(bound,layer.MaximumAbsoluteHeightMeters);
            }
            return bound;
        }
        /// <summary>CPUとGPUが共用する最終affine式。低周波の置換はoffsetへ折り込み、細部の重みを消しません。</summary>
        public SurfaceHeightBlend SampleHeightBlend(SurfaceVector direction){
            direction=LocalSurfaceHeightOverride.NormalizeDirection(direction);
            var basis=SampleLayers(this.baseLayers,direction);
            var full=SampleLayers(this.fullHeightLayers,direction);
            double offset=full.OffsetHeightMeters;
            if(full.NaturalWeight>0&&(basis.NaturalWeight!=1||basis.OffsetHeightMeters!=0)){
                double baseHeight=this.NaturalSource.SampleBaseHeightMeters(direction);
                offset+=full.NaturalWeight*((basis.NaturalWeight-1)*baseHeight+basis.OffsetHeightMeters);
            }
            return new SurfaceHeightBlend(full.NaturalWeight,offset,basis.ScatterWeight*full.ScatterWeight);
        }
        private static SurfaceHeightBlend SampleLayers(ISurfaceHeightOverride[] layers,SurfaceVector direction){
            double w=1,o=0,s=1;
            foreach(var layer in layers){
                var blend=layer.Sample(direction);
                w*=blend.NaturalWeight;o=blend.Apply(o);s*=blend.ScatterWeight;
            }
            return new SurfaceHeightBlend(w,o,s);
        }
        public double SampleHeightMeters(double unitX,double unitY,double unitZ)=>
            this.SampleHeightMeters(new SurfaceVector(unitX,unitY,unitZ),0);
        public double SampleHeightMeters(SurfaceVector direction,double spacingMeters){
            if(!double.IsFinite(spacingMeters)||spacingMeters<0){throw new ArgumentOutOfRangeException(nameof(spacingMeters));}
            var blend=this.SampleHeightBlend(direction);
            if(blend.NaturalWeight==0){return blend.OffsetHeightMeters;}
            return blend.Apply(this.NaturalSource.SampleHeightMeters(direction,spacingMeters));
        }
        /// <summary>低周波/全標高の役割・順序・内容・範囲をキーに含め、異なる造成の法線を再利用させません。</summary>
        public SurfaceDerivedKey CreateDerivedKey(string sourceHash,SurfaceTileKey region,int resolution){
            var key=this.NaturalSource.CreateDerivedKey(sourceHash,region,resolution);
            if(!this.HasHeightOverrides){return key;}
            return new SurfaceDerivedKey(key.SourceHash,Hash(key.GenerationHash+"|"+this.layerHash),
                key.Seed,key.Version,key.Region,key.Resolution,key.Band);
        }
        private static string Hash(string value){
            using(var sha=SHA256.Create()){
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","").ToLowerInvariant();
            }
        }
    }
}
