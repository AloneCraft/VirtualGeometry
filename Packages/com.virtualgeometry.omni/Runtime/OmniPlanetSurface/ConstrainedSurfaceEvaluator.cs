using System;
using System.Collections.Generic;

namespace Omni.PlanetSurface{
    /// <summary>同じ正本を帯域制限してメッシュへ供給する追加契約です。</summary>
    public interface IBandLimitedSurfaceHeightSource:IPlanetSurfaceHeightSource{
        double SampleHeightMeters(SurfaceVector direction,double spacingMeters);
    }
    /// <summary>
    /// 頂点法線に、形状とは別の高さを使う源の追加契約です(opt-in)。形状には入れるが陰影は画素ごとに別に付ける細部
    /// (EarthToMoonの月面の生成クレーター)を、頂点法線へ二重に入れないために使います。
    /// 実装しない源では、SurfacePatchBuilderは従来どおり形状と同じ高さから法線を求めます。
    /// </summary>
    public interface ISurfaceShadingHeightSource:IBandLimitedSurfaceHeightSource{
        /// <summary>頂点法線に使う高さ(m)です。spacingMetersは形状と同じメッシュ間隔です。</summary>
        double SampleShadingHeightMeters(SurfaceVector direction,double spacingMeters);
    }
    /// <summary>天体固有の生成設定です。品質設定とは独立し、月面以外にも同じ生成器を使えます。</summary>
    [Serializable]
    public sealed class SurfaceGenerationSettings{
        // 天体固有seedはPlanetSurfaceDefinitionへ保存し、共通型は中立値から始める。
        public int Seed=1;
        public int AlgorithmVersion=1;
        public double LargestDetailMeters=64;
        public double ReliefRatio=0.035;
        public int BandCount=6;

        /// <summary>対応していない版や危険なスケールを実行前に拒否します。</summary>
        public void Validate(){
            if(this.AlgorithmVersion!=1){throw new NotSupportedException("地形生成版が未対応です。");}
            if(!double.IsFinite(this.LargestDetailMeters)||this.LargestDetailMeters<=0||
                !double.IsFinite(this.ReliefRatio)||this.ReliefRatio<0||this.ReliefRatio>0.2||
                this.BandCount<1||this.BandCount>8){
                throw new ArgumentOutOfRangeException(nameof(SurfaceGenerationSettings));
            }
        }
    }

    /// <summary>実寸位置の標高、球面接線方向の勾配、反射色倍率、粗さを返します。</summary>
    public readonly struct SurfaceEvaluation{
        public readonly double HeightMeters;
        public readonly SurfaceVector Gradient;
        public readonly double AlbedoMultiplier;
        public readonly double Roughness;
        public SurfaceEvaluation(double height,SurfaceVector gradient,double albedo,double roughness){
            this.HeightMeters=height;this.Gradient=gradient;
            this.AlbedoMultiplier=albedo;this.Roughness=roughness;
        }
    }

    /// <summary>
    /// 任意の球形天体に使える自然地形正本です。B+P-I(S(P))を帯域別に評価します。
    /// カメラ・Cube面・キャッシュ順序は生成関数へ渡しません。施設造成は外側で適用します。
    /// </summary>
    public sealed class ConstrainedSurfaceEvaluator:IProceduralSurfaceHeightSource{
        private sealed class Band{
            internal double Scale;
            internal double Amplitude;
            internal uint Seed;
            internal SphericalHeightGrid Correction;
        }

        private readonly SphericalHeightGrid basis;
        private readonly Band[] bands;
        private readonly double radius;
        private readonly int width;
        private readonly int height;
        private readonly BoundedSurfaceCache<long,double> anchors;
        private readonly int generationSeed,generationVersion;
        private readonly string generationHash;

        /// <summary>標高配列は共有し、補正のサンプルだけを上限付きでキャッシュします。</summary>
        /// <param name="anchorCacheBytes">補正サンプルキャッシュの管理上限バイトです。</param>
        public ConstrainedSurfaceEvaluator(float[] values,int width,int height,double radiusMeters,
            SurfaceGenerationSettings settings,long anchorCacheBytes=4*1024*1024)
            :this(new SphericalHeightGrid(values,width,height),radiusMeters,settings,anchorCacheBytes){
        }

        /// <summary>TextAssetの読み取り専用ビューを使う場合、66MBの標高コピーを避けられます。</summary>
        public ConstrainedSurfaceEvaluator(SphericalHeightGrid grid,double radiusMeters,
            SurfaceGenerationSettings settings,long anchorCacheBytes=4*1024*1024){
            if(settings==null){throw new ArgumentNullException(nameof(settings));}
            settings.Validate();
            if(!double.IsFinite(radiusMeters)||radiusMeters<=0){throw new ArgumentOutOfRangeException(nameof(radiusMeters));}
            this.radius=radiusMeters;
            this.basis=grid??throw new ArgumentNullException(nameof(grid));
            this.width=grid.Width;
            this.height=grid.Height;
            this.generationSeed=settings.Seed;this.generationVersion=settings.AlgorithmVersion;
            string canonical=FormattableString.Invariant($"surface-v{settings.AlgorithmVersion}|radius={radiusMeters:R}|width={grid.Width}|height={grid.Height}|largest={settings.LargestDetailMeters:R}|relief={settings.ReliefRatio:R}|bands={settings.BandCount}|seed={settings.Seed}");
            using(var sha=System.Security.Cryptography.SHA256.Create()){
                this.generationHash=BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(canonical))).Replace("-","").ToLowerInvariant();
            }
            this.anchors=new BoundedSurfaceCache<long,double>(anchorCacheBytes);
            this.bands=new Band[settings.BandCount];
            // 元データの南北サンプル間隔より十分短い帯域だけを追加します。
            double largest=Math.Min(settings.LargestDetailMeters,Math.PI*radiusMeters/this.height*0.25);
            double detailBound=0;
            for(int i=0;i<this.bands.Length;i++){
                var band=new Band{
                    Scale=largest/Math.Pow(4,i),
                    Seed=unchecked((uint)settings.Seed+(uint)i*0x9e3779b9u)
                };
                band.Amplitude=band.Scale*settings.ReliefRatio;
                this.bands[i]=band;
                int bandIndex=i;
                band.Correction=new SphericalHeightGrid(this.width,this.height,
                    (x,y)=>this.Anchor(bandIndex,x,y),band.Amplitude*27);
                detailBound+=band.Amplitude*81;
            }
            this.MaximumDetailHeightMeters=detailBound;
            this.MaximumAbsoluteHeightMeters=this.MaximumBaseHeightMeters+detailBound;
        }

        /// <inheritdoc/>
        public double MaximumAbsoluteHeightMeters{get;}
        /// <summary>局所実測で置換する基準補間の絶対標高上限メートルです。</summary>
        public double MaximumBaseHeightMeters=>this.basis.MaximumAbsoluteHeightMeters;
        /// <summary>全生成帯域と制約補正の絶対変位上限メートルです。地域置換の後にも加算されます。</summary>
        public double MaximumDetailHeightMeters{get;}
        /// <summary>上限管理に使う補正キャッシュの予約バイトです。</summary>
        public long AnchorCacheBytes=>this.anchors.UsedBytes;
        /// <summary>メッシュとGPU法線で共有する帯域数です。</summary>
        public int BandCount=>this.bands.Length;
        public double RadiusMeters=>this.radius;
        /// <summary>自然地形には上書きレイヤーがありません。</summary>
        public ConstrainedSurfaceEvaluator NaturalSource=>this;
        public bool HasHeightOverrides=>false;
        public SurfaceHeightBlend SampleHeightBlend(SurfaceVector direction)=>SurfaceHeightBlend.Identity;

        /// <summary>評価器作成時の不変設定から最終法線cacheのキーを作ります。後の設定オブジェクト変更に影響されません。</summary>
        public SurfaceDerivedKey CreateDerivedKey(string sourceHash,SurfaceTileKey region,int resolution)=>
            new(sourceHash,this.generationHash,this.generationSeed,this.generationVersion,region,resolution,-1);

        /// <summary>タイル中心だけの整数IDと、法線へ渡す残差帯域の重みを返します。</summary>
        public SurfaceDetailBand PrepareDetailBand(SurfaceVector direction,int bandIndex,double meshSpacing,double normalSpacing,
            bool fullSurfaceNormals=false){
            ValidateDetailSpacing(meshSpacing,normalSpacing);
            if(bandIndex<0||bandIndex>=this.bands.Length){throw new ArgumentOutOfRangeException(nameof(bandIndex));}
            Band band=this.bands[bandIndex];
            SurfaceVector q=direction.Normalized*(this.radius/band.Scale);
            int x=checked((int)Math.Floor(q.X)),y=checked((int)Math.Floor(q.Y)),z=checked((int)Math.Floor(q.Z));
            double weight=Math.Max(0,BandWeight(band.Scale,normalSpacing)-BandWeight(band.Scale,meshSpacing));
            if(fullSurfaceNormals){weight=BandWeight(band.Scale,normalSpacing);}
            return new SurfaceDetailBand{
                CellX=x,CellY=y,CellZ=z,Seed=band.Seed,
                LocalAmplitude=new UnityEngine.Vector4((float)(q.X-x),(float)(q.Y-y),(float)(q.Z-z),(float)band.Amplitude),
                Parameters=new UnityEngine.Vector4((float)(1/band.Scale),(float)weight,0,0)
            };
        }

        /// <summary>残差の低周波補正だけを合計します。微細なFeatureの評価はGPUへ任せます。</summary>
        public double SampleDetailCorrection(SurfaceVector direction,double meshSpacing,double normalSpacing){
            ValidateDetailSpacing(meshSpacing,normalSpacing);direction=direction.Normalized;
            double correction=0;
            foreach(Band band in this.bands){
                double weight=Math.Max(0,BandWeight(band.Scale,normalSpacing)-BandWeight(band.Scale,meshSpacing));
                if(weight<=0){continue;}
                correction+=weight*band.Correction.SampleHeightMeters(direction.X,direction.Y,direction.Z);
            }
            return correction;
        }

        /// <summary>タイル生成要求専用の補間キャッシュです。並列要求で同じdelegateを共有しません。</summary>
        public Func<SurfaceVector,double> CreateDetailCorrectionSampler(double meshSpacing,double normalSpacing,
            bool fullSurfaceNormals=false,double referenceHeight=0){
            ValidateDetailSpacing(meshSpacing,normalSpacing);
            if(!double.IsFinite(referenceHeight)){throw new ArgumentOutOfRangeException(nameof(referenceHeight));}
            var samplers=new Func<double,double,double>[this.bands.Length];
            var weights=new double[this.bands.Length];
            Func<double,double,double> sampleBase=null;
            if(fullSurfaceNormals){sampleBase=this.basis.CreateCachedSampler();}
            for(int i=0;i<this.bands.Length;i++){
                Band band=this.bands[i];
                weights[i]=Math.Max(0,BandWeight(band.Scale,normalSpacing)-BandWeight(band.Scale,meshSpacing));
                if(fullSurfaceNormals){weights[i]=BandWeight(band.Scale,normalSpacing);}
                if(weights[i]>0){samplers[i]=band.Correction.CreateCachedSampler();}
            }
            return direction=>{
                if(!double.IsFinite(direction.X)||!double.IsFinite(direction.Y)||!double.IsFinite(direction.Z)||direction.Length==0){
                    throw new ArgumentOutOfRangeException(nameof(direction));
                }
                double column=(Math.Atan2(direction.X,direction.Y)/(2*Math.PI)+0.5)*this.width-0.5;
                double row=(0.5-Math.Atan2(direction.Z,Math.Sqrt(direction.X*direction.X+direction.Y*direction.Y))/Math.PI)*this.height-0.5;
                double correction=0;
                // GPUが -correction+P を計算すると、基準標高との差分も同時に復元されます。
                if(sampleBase!=null){correction=referenceHeight-sampleBase(column,row);}
                for(int i=0;i<samplers.Length;i++){
                    if(samplers[i]!=null){correction+=weights[i]*samplers[i](column,row);}
                }
                return correction;
            };
        }

        /// <summary>生成地形を含まない基準補間です。GPU高さを局所基準からの差分で保存するときに使用します。</summary>
        public double SampleBaseHeightMeters(SurfaceVector direction)=>
            this.basis.SampleHeightMeters(direction.X,direction.Y,direction.Z);

        private static void ValidateDetailSpacing(double meshSpacing,double normalSpacing){
            if(!double.IsFinite(meshSpacing)||!double.IsFinite(normalSpacing)||meshSpacing<=0||
                normalSpacing<0||normalSpacing>meshSpacing){throw new ArgumentOutOfRangeException(nameof(normalSpacing));}
        }
        /// <summary>再生成の再現性検証やメモリ圧迫時に派生サンプルを解放します。</summary>
        public void ClearCache()=>this.anchors.Clear();

        /// <summary>GPU法線用の残差帯域を準備します。正本の補正と整数位置IDを維持します。</summary>
        public SurfaceDetailPoint PrepareDetailPoint(SurfaceVector direction,int bandIndex,double meshSpacingMeters){
            if(bandIndex<0||bandIndex>=this.bands.Length||!double.IsFinite(meshSpacingMeters)||meshSpacingMeters<0){
                throw new ArgumentOutOfRangeException(nameof(bandIndex));
            }
            direction=direction.Normalized;
            Band band=this.bands[bandIndex];
            SurfaceVector q=direction*(this.radius/band.Scale);
            int x=checked((int)Math.Floor(q.X)),y=checked((int)Math.Floor(q.Y)),z=checked((int)Math.Floor(q.Z));
            return new SurfaceDetailPoint{
                CellX=x,CellY=y,CellZ=z,LocalX=(float)(q.X-x),LocalY=(float)(q.Y-y),LocalZ=(float)(q.Z-z),
                Correction=(float)band.Correction.SampleHeightMeters(direction.X,direction.Y,direction.Z),
                Amplitude=(float)band.Amplitude,Weight=(float)(1-BandWeight(band.Scale,meshSpacingMeters)),Seed=band.Seed
            };
        }

        /// <inheritdoc/>
        public double SampleHeightMeters(double unitX,double unitY,double unitZ){
            return this.SampleHeightMeters(new SurfaceVector(unitX,unitY,unitZ),0);
        }

        /// <summary>
        /// メッシュ間隔メートルに応じた帯域制限付き標高です。0は全帯域の地表問い合わせです。
        /// 帯域の重みを変えても特徴の位置は変わらず、元標高は保持されます。
        /// </summary>
        public double SampleHeightMeters(SurfaceVector direction,double spacingMeters){
            if(!double.IsFinite(spacingMeters)||spacingMeters<0){throw new ArgumentOutOfRangeException(nameof(spacingMeters));}
            direction=direction.Normalized;
            double result=this.basis.SampleHeightMeters(direction.X,direction.Y,direction.Z);
            SurfaceVector position=direction*this.radius;
            foreach(Band band in this.bands){
                double weight=BandWeight(band.Scale,spacingMeters);
                if(weight<=0){continue;}
                double correction=band.Correction.SampleHeightMeters(direction.X,direction.Y,direction.Z);
                result+=weight*(Feature(position,band.Scale,band.Amplitude,band.Seed)-correction);
            }
            return result;
        }

        /// <summary>実寸位置と評価間隔から、境界外側も同じ関数で評価した勾配を返します。</summary>
        public SurfaceEvaluation Evaluate(SurfaceVector positionMeters,double spacingMeters){
            SurfaceVector direction=positionMeters.Normalized;
            double value=this.SampleHeightMeters(direction,spacingMeters);
            SurfaceVector reference=new SurfaceVector(0,0,1);
            if(Math.Abs(direction.Z)>0.9){reference=new SurfaceVector(0,1,0);}
            SurfaceVector tangent=SurfaceVector.Cross(reference,direction).Normalized;
            SurfaceVector bitangent=SurfaceVector.Cross(direction,tangent);
            double delta=Math.Max(0.01,spacingMeters*0.5);
            SurfaceVector p=direction*this.radius;
            double dx=(this.SampleHeightMeters(p+tangent*delta,spacingMeters)-
                this.SampleHeightMeters(p-tangent*delta,spacingMeters))/(2*delta);
            double dy=(this.SampleHeightMeters(p+bitangent*delta,spacingMeters)-
                this.SampleHeightMeters(p-bitangent*delta,spacingMeters))/(2*delta);
            SurfaceVector gradient=tangent*dx+bitangent*dy;
            double slope=gradient.Length;
            return new SurfaceEvaluation(value,gradient,1-0.03*Math.Clamp(slope,0,1),
                Math.Clamp(0.94-0.1*slope,0.65,0.98));
        }

        private double Anchor(int bandIndex,int x,int y){
            long key=((long)bandIndex*this.height+y)*this.width+x;
            if(this.anchors.TryGet(key,out double cached)){return cached;}
            Band band=this.bands[bandIndex];
            SurfaceVector position=this.basis.SampleDirection(x,y)*this.radius;
            double value=Feature(position,band.Scale,band.Amplitude,band.Seed);
            // Dictionary/LinkedList管理分を含む保守的な1エントリ予約です。
            this.anchors.Put(key,value,192);
            return value;
        }

        /// <summary>帯域境界を連続化します。残差法線側では1-weightを用います。</summary>
        public static double BandWeight(double scale,double spacing){
            if(spacing<=0){return 1;}
            double t=Math.Clamp((scale/spacing-2)/2,0,1);
            return t*t*(3-2*t);
        }

        /// <summary>
        /// 整数セルIDとセル内の小座標で決まるコンパクトなクレーター場です。
        /// Int64 GPU演算なしで移植できるuintハッシュを使います。
        /// </summary>
        public static double Feature(SurfaceVector position,double scale,double amplitude,uint seed){
            SurfaceVector q=position/scale;
            int ix=checked((int)Math.Floor(q.X));
            int iy=checked((int)Math.Floor(q.Y));
            int iz=checked((int)Math.Floor(q.Z));
            double result=0;
            for(int z=-1;z<=1;z++){
                for(int y=-1;y<=1;y++){
                    for(int x=-1;x<=1;x++){
                        uint hash=Hash(ix+x,iy+y,iz+z,seed);
                        double cx=x+Unit(hash);
                        double cy=y+Unit(Mix(hash+0x68bc21ebu));
                        double cz=z+Unit(Mix(hash+0x02e5be93u));
                        double dx=q.X-ix-cx,dy=q.Y-iy-cy,dz=q.Z-iz-cz;
                        double radius=0.22+0.18*Unit(Mix(hash+0x967a889bu));
                        double r=Math.Sqrt(dx*dx+dy*dy+dz*dz)/radius;
                        if(r>=1){continue;}
                        // 縁と堆積の外端は高さ・傾斜とも0。内部は滑らかな窪地です。
                        double r2=r*r;
                        double envelope=(1-r2)*(1-r2);
                        double bowl=envelope*(5*r2-1);
                        result+=amplitude*bowl;
                    }
                }
            }
            return result;
        }

        private static double Unit(uint value)=>(value&0x00ffffffu)/16777216.0;
        private static uint Hash(int x,int y,int z,uint seed){
            unchecked{return Mix((uint)x*0x8da6b343u^(uint)y*0xd8163841u^(uint)z*0xcb1ab31fu^seed);}
        }
        private static uint Mix(uint x){
            unchecked{x^=x>>16;x*=0x7feb352du;x^=x>>15;x*=0x846ca68bu;return x^(x>>16);}
        }
    }

    /// <summary>月面の派生データを使用頻度順で解放する、バイト予算付きLRUです。</summary>
    public sealed class BoundedSurfaceCache<TKey,TValue>{
        private sealed class Entry{
            internal TKey Key;
            internal TValue Value;
            internal long Bytes;
        }
        private readonly Dictionary<TKey,LinkedListNode<Entry>> entries=new();
        private readonly LinkedList<Entry> order=new();
        private readonly Action<TValue> release;
        public long BudgetBytes{get;}
        public long UsedBytes{get;private set;}
        public int Count=>this.entries.Count;

        /// <summary>releaseにはGPUタイル等の解放処理を指定できます。</summary>
        public BoundedSurfaceCache(long budgetBytes,Action<TValue> release=null){
            if(budgetBytes<0){throw new ArgumentOutOfRangeException(nameof(budgetBytes));}
            this.BudgetBytes=budgetBytes;this.release=release;
        }
        /// <summary>参照した要素を最近使用へ移動します。</summary>
        public bool TryGet(TKey key,out TValue value){
            if(this.entries.TryGetValue(key,out var node)){
                this.order.Remove(node);this.order.AddFirst(node);value=node.Value.Value;return true;
            }
            value=default;return false;
        }
        /// <summary>上限を超える要素は所有権を取らずfalseを返します。</summary>
        public bool Put(TKey key,TValue value,long bytes){
            if(bytes<=0){throw new ArgumentOutOfRangeException(nameof(bytes));}
            if(bytes>this.BudgetBytes){return false;}
            if(this.entries.TryGetValue(key,out var previous)){this.Remove(previous);}
            while(this.UsedBytes>this.BudgetBytes-bytes){this.Remove(this.order.Last);}
            var node=this.order.AddFirst(new Entry{Key=key,Value=value,Bytes=bytes});
            this.entries.Add(key,node);this.UsedBytes+=bytes;return true;
        }
        /// <summary>正本を残し、全派生物を解放します。</summary>
        public void Clear(){while(this.order.Last!=null){this.Remove(this.order.Last);}}
        private void Remove(LinkedListNode<Entry> node){
            this.entries.Remove(node.Value.Key);this.order.Remove(node);
            this.UsedBytes-=node.Value.Bytes;this.release?.Invoke(node.Value.Value);
        }
    }
}
