using System;
using UnityEngine;
using UnityEngine.Rendering;
using LogicalScalar=Omni.Logical.Scalar;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.World{
    /// <summary>
    /// 手続き生成の地形の Layer。高さは <see cref="PlanetHeightField"/>(GPU は PlanetHeight.hlsl)、頂点の生成は PlanetTerrainGenerate.compute。
    /// Cell の選択・生成・退避・配置は <see cref="PlanetLayerSource"/>。
    /// </summary>
    [ExecuteAlways]
    public sealed class PlanetTerrainSource:PlanetLayerSource{
        /// <summary>GPU の生成が受け取れる octave の数(PlanetHeight.hlsl の PLANET_HEIGHT_MAXIMUM_OCTAVES)。</summary>
        public const int MaximumOctaves=PlanetHeightField.MaximumOctaves;
        // CPU で地表の高さを求めるとき、点を含む Cell を探す階層(面の座標の float で十分に区別できる粗さ)。
        private const int SampleLevel=12;

        [SerializeField,Min(1)] private float radius=10000;
        [SerializeField,Min(0)] private float heightAmplitude=300;
        [SerializeField,Min(0.01f)] private float baseWavelength=4000;
        [SerializeField,Range(1,MaximumOctaves)] private int octaves=13;
        [SerializeField] private PlanetHeightSettings heightSettings=PlanetHeightSettings.Default;
        // 増幅の遮断波長は、実データの 1 画素の波長の何倍か(1/2 画素)。
        private const double CutoffTexels=0.5;
        // 実データの帯域より上の細部の増幅(実データがあるときだけ働く)。0 なら無効。遮断波長(実データの 1/2 画素の波長。CutoffTexels)での octave の振幅(m)と、Hurst 指数(波長が半分になるごとに振幅が 2^−H 倍)。
        [SerializeField,Min(0)] private float amplifyAmplitude=0f;
        [SerializeField,Range(0.3f,1f)] private float amplifyHurst=0.87f;
        // 中心の方向の地表の高さの絶対値がこれ以上の Cell は、原点を地表の高さまで持ち上げる(頂点の高さ方向の成分を小さく保つ)。0 なら高さが 0 でない全ての Cell。
        [SerializeField,Min(0)] private float heightShiftThreshold=1024f;

        // 実データの高さの格子(0: 全球、1: 局所の Override)。ある間は、高さに足す(PlanetRaster.hlsl)。
        private PlanetRasterLayer[] rasters=new PlanetRasterLayer[0];
        private Texture2D emptyRaster;
        private readonly PlanetRasterCell[] rasterCells=new PlanetRasterCell[2];
        private readonly int[] rasterIndex=new int[8];
        private readonly Vector4[] rasterFraction=new Vector4[2];
        private readonly int[] rasterSize=new int[8];
        private readonly Vector4[] rasterRegional=new Vector4[2];
        private readonly Vector4[] rasterPole=new Vector4[2];
        // 実データの格子の遮断波長(CutoffTexels × 1 画素の波長)の log2(m)。全球と局所。SetRasters と Configure で求め直す。
        private float cutoffGlobalLog2;
        private float cutoffRegionalLog2;
        // 色(アルベド)の格子(任意)。Cell ごとの値(原点の画素の位置)は slot ごとに覚える(Cell が変わったときだけ求め直す)。
        private PlanetColorLayer colorLayer;
        private PlanetColorCell[] colorCells;
        private PlanetCellKey[] colorKeys;
        private bool[] colorValid;
        private static readonly int ColorTextureId=Shader.PropertyToID("_PlanetColorTex");
        private static readonly int ColorDirectionId=Shader.PropertyToID("_PlanetColorDirection");
        private static readonly int ColorIndexId=Shader.PropertyToID("_PlanetColorIndex");
        private static readonly int ColorFractionId=Shader.PropertyToID("_PlanetColorFraction");
        private PlanetCellKey rasterCacheKey;
        private float rasterCacheQuad=-1f;
        private bool rasterCacheValid;

        private LogicalScalar inverseWavelength;
        private readonly int[] noiseLattice=new int[4*MaximumOctaves];
        private readonly Vector4[] noiseFraction=new Vector4[MaximumOctaves];
        private readonly PlanetHeightLattice sampleLattice=new PlanetHeightLattice();

        public override float Radius=>this.radius;
        public float HeightAmplitude=>this.heightAmplitude;
        public PlanetHeightSettings HeightSettings=>this.heightSettings;
        /// <summary>原点を持ち上げる高さのしきい値(m)。Cell の生成の前に設定する(有効なまま変えるときは作り直す)。</summary>
        public float HeightShiftThreshold{
            get=>this.heightShiftThreshold;
            set{
                if(float.IsNaN(value)||value<0f){value=0f;}
                if(value==this.heightShiftThreshold){return;}
                this.heightShiftThreshold=value;
                this.Rebuild();
            }
        }
        /// <summary>原点の高さを求めた回数と、そのうち持ち上げた回数(診断用)。</summary>
        public int HeightShiftEvaluations{get;private set;}
        public int HeightShiftApplied{get;private set;}
        /// <summary>原点の高さを求めるのにかかった CPU 時間の累計(ミリ秒、診断用)。</summary>
        public float HeightShiftMilliseconds{get;private set;}
        protected override float SelectionAmplitude=>this.heightAmplitude+this.RasterMaximumHeight+this.AmplifyMaximum;
        /// <summary>
        /// 実データの帯域より上の細部の増幅の振幅(遮断波長での octave の振幅、m。0 で無効。実データがあるときだけ働く)。変えると作り直す。
        /// 実データの 1/2 画素の波長より短い波長の octave を、Hurst 指数(<see cref="AmplifyHurst"/>)で振幅を減らしながら足す。
        /// </summary>
        public float AmplifyAmplitude{
            get=>this.amplifyAmplitude;
            set{
                if(float.IsNaN(value)||value<0f){value=0f;}
                if(value==this.amplifyAmplitude){return;}
                this.amplifyAmplitude=value;
                this.Rebuild();
            }
        }
        public float AmplifyHurst{
            get=>this.amplifyHurst;
            set{
                if(float.IsNaN(value)){value=0.87f;}
                value=Mathf.Clamp(value,0.3f,1f);
                if(value==this.amplifyHurst){return;}
                this.amplifyHurst=value;
                this.Rebuild();
            }
        }
        /// <summary>増幅の項の絶対値の上界(選択の bounds に足す)。実データが無いか振幅が 0 なら 0。</summary>
        public float AmplifyMaximum{
            get{
                if(this.rasters.Length==0){return 0f;}
                return PlanetHeightField.AmplifiedMaximum(this.amplifyAmplitude,this.amplifyHurst);
            }
        }
        /// <summary>実データの高さの |最大|(Cell の bounds に足す)。</summary>
        public float RasterMaximumHeight{
            get{
                float maximum=0;
                for(int i=0;i<this.rasters.Length;i++){maximum=Mathf.Max(maximum,this.rasters[i].MaximumAbsoluteHeight);}
                return maximum;
            }
        }
        public int RasterCount=>this.rasters.Length;

        /// <summary>検証用。全球の格子で、Cell の quad の長さに使う mip。</summary>
        public int RasterMip(in PlanetCellKey key){
            if(this.rasters.Length==0||this.Selector==null){return -1;}
            return this.rasters[0].MipFor(this.Selector.QuadArc(key.Level),this.radius);
        }
        protected override string GeneratorResource=>"VirtualGeometryWorld/PlanetTerrainGenerate";

        // Cell の中心の地表の高さ(quad の長さで帯域制限した、頂点(中心)と同じ式の値)が大きいとき、その高さだけ原点を持ち上げる。
        // 中心の頂点の高さは、原点を持ち上げたあとの高さ方向の成分 h−H がほぼ 0 になる値。float のまま使う(量子化しない)。
        protected override bool TryCellHeightShift(in PlanetCellFrame sphereFrame,out float shift){
            shift=0f;
            if(this.Selector==null){return true;}
            this.HeightShiftEvaluations++;
            long start=System.Diagnostics.Stopwatch.GetTimestamp();
            bool evaluated=this.TryHeight(in sphereFrame,Vector3.zero,this.Selector.QuadArc(sphereFrame.Key.Level),out float height);
            this.HeightShiftMilliseconds+=(float)((System.Diagnostics.Stopwatch.GetTimestamp()-start)*1000.0/System.Diagnostics.Stopwatch.Frequency);
            if(!evaluated||float.IsNaN(height)||float.IsInfinity(height)||Mathf.Abs(height)<this.heightShiftThreshold){return true;}
            shift=height;
            this.HeightShiftApplied++;
            return true;
        }

        /// <summary>検証用。形の設定を変える。有効なまま変えた場合は無効化して作り直す。</summary>
        public void Configure(float radius,float heightAmplitude,int maximumLevel,float targetPixels){
            this.radius=radius;this.heightAmplitude=heightAmplitude;
            this.RefreshCutoffs();
            this.ConfigureSelection(maximumLevel,targetPixels);
            this.Rebuild();
        }

        // 実データの格子の遮断波長の log2。波長 = CutoffTexels × 南北の 1 画素の間隔。実データの Nyquist の波長は 2 画素だが、1 画素に近い波長の octave を足すと、1 画素ごとの平均が 0 にならず、遠景(データそのもの)と近景がずれる。
        // 遮断波長を 2 画素にすると、1 画素ごとの平均の二乗平均の平方根が全体の 0.70 倍、1 画素にすると 0.50 倍、1/2 画素にすると 0.34 倍、1/4 画素にすると 0.26 倍(gradient noise は低い周波数にも広がる)。
        // 平均を 0 に近づける効果は 1/2 画素より先で小さいので、1/2 画素にした。
        private void RefreshCutoffs(){
            this.cutoffGlobalLog2=0f;this.cutoffRegionalLog2=0f;
            if(this.rasters.Length>0){this.cutoffGlobalLog2=(float)(Math.Log(CutoffTexels*this.rasters[0].TexelMeters(this.radius))/Math.Log(2.0));}
            this.cutoffRegionalLog2=this.cutoffGlobalLog2;
            if(this.rasters.Length>1){this.cutoffRegionalLog2=(float)(Math.Log(CutoffTexels*this.rasters[1].TexelMeters(this.radius))/Math.Log(2.0));}
        }

        /// <summary>
        /// 実データの高さの格子を設定する(global: 全球、regional: 局所の Override。どちらも null でよい)。有効なまま変えた場合は作り直す。
        /// 格子は呼び出し側が持ち(Dispose も)、ここでは texture と Cell ごとの値を GPU へ渡すだけ。
        /// 実データがあるときは、手続きの項(heightAmplitude、波長、octave)を、実データの帯域より上の細部に使う設定にする。
        /// </summary>
        public void SetRasters(PlanetRasterLayer global,PlanetRasterLayer regional){
            var list=new System.Collections.Generic.List<PlanetRasterLayer>();
            if(global!=null){list.Add(global);}
            if(regional!=null){
                if(global==null){throw new ArgumentException("局所の格子には全球の格子が必要です。");}
                list.Add(regional);
            }
            this.rasters=list.ToArray();
            this.rasterCacheValid=false;
            this.RefreshCutoffs();
            this.Rebuild();
        }

        /// <summary>色(アルベド)の格子(<see cref="PlanetColorLayer"/>)。null なら使わない。変えたら次の frame から、Cell ごとの値を求め直す(頂点は作り直さない)。</summary>
        public PlanetColorLayer ColorLayer{
            get=>this.colorLayer;
            set{
                this.colorLayer=value;
                if(this.colorValid!=null){Array.Clear(this.colorValid,0,this.colorValid.Length);}
            }
        }

        /// <summary>
        /// 色の格子の値(Cell の原点の方向と画素の位置)を draw の MaterialPropertyBlock に書く。shader は RadialMoonGrid.shader の _PlanetColorDirection、_PlanetColorIndex、_PlanetColorFraction、_PlanetColorTex。
        /// 色の格子が無いとき(または原点を求められないとき)は、_PlanetColorDirection.w = 0 を書く(前の値が残らないように)。
        /// </summary>
        protected override void WriteCellProperties(MaterialPropertyBlock block,int slot,in PlanetCellFrame frame){
            base.WriteCellProperties(block,slot,in frame);
            if(this.colorLayer==null||this.colorCells==null||slot<0||slot>=this.colorCells.Length){
                block.SetVector(ColorDirectionId,Vector4.zero);
                return;
            }
            if(!this.colorValid[slot]||!this.colorKeys[slot].Equals(frame.Key)){
                if(!this.colorLayer.TryCell(frame.SphereOrigin,out this.colorCells[slot])){
                    this.colorValid[slot]=false;
                    block.SetVector(ColorDirectionId,Vector4.zero);
                    return;
                }
                this.colorKeys[slot]=frame.Key;this.colorValid[slot]=true;
            }
            PlanetColorCell cell=this.colorCells[slot];
            block.SetTexture(ColorTextureId,this.colorLayer.Texture);
            block.SetVector(ColorDirectionId,new Vector4(cell.Direction.x,cell.Direction.y,cell.Direction.z,1f));
            block.SetVector(ColorIndexId,new Vector4(cell.ColumnIndex,cell.RowIndex,cell.Width,cell.Height));
            block.SetVector(ColorFractionId,new Vector4(cell.ColumnFraction,cell.RowFraction,cell.ColumnsPerRadian,cell.RowsPerRadian));
        }

        protected override void OnEnable(){
            this.RefreshCutoffs();
            if(!LogicalScalar.TryFromFloat(this.baseWavelength,out LogicalScalar wavelength)||!LogicalScalar.TryReciprocal(in wavelength,out this.inverseWavelength)){
                Debug.LogError("PlanetTerrainSource: 波長を Logical にできません。",this);
            }
            base.OnEnable();
            int capacity=this.PoolCapacity;
            this.colorCells=new PlanetColorCell[capacity];this.colorKeys=new PlanetCellKey[capacity];this.colorValid=new bool[capacity];
        }

        protected override void OnDisable(){
            this.colorCells=null;this.colorKeys=null;this.colorValid=null;
            base.OnDisable();
            if(this.emptyRaster!=null){
                if(Application.isPlaying){Destroy(this.emptyRaster);}else{DestroyImmediate(this.emptyRaster);}
                this.emptyRaster=null;
            }
        }

        /// <summary>
        /// 惑星のローカル座標の点 point(Logical)の真下(中心からの方向)の地表の高さ(R からの高さ、帯域制限なし)と、point の地表からの高さを CPU で求める(float で返す版)。
        /// <see cref="TrySampleSurface(in LogicalVector3,out LogicalScalar,out LogicalScalar)"/> の値を float にしたもので、|高さ| が 4 km で 0.5 mm、9 km で 1 mm の刻みになる。
        /// mm の精度で地面に物を置くには Logical 版を使う。
        /// </summary>
        public bool TrySampleSurface(in LogicalVector3 point,out float height,out float altitude){
            height=0;altitude=0;
            if(!this.TrySampleSurface(in point,out LogicalScalar exactHeight,out LogicalScalar exactAltitude)){return false;}
            return exactHeight.TryToFloat(out height)&&exactAltitude.TryToFloat(out altitude);
        }

        /// <summary>
        /// 惑星のローカル座標の点 point(Logical)の真下(中心からの方向)の地表の高さ(R からの高さ、帯域制限なし)と、point の地表からの高さを CPU で求める(Logical で返す版)。
        /// 手続きの高さは GPU の生成と同じ式(<see cref="PlanetHeightField"/>)で、点を含む Cell の球面上の原点(<see cref="PlanetCellFrame.SphereOrigin"/>。持ち上げた Origin ではない)からの相対位置で計算する(float)。
        /// 実データの高さは点自身の方向から倍精度で求め、足し合わせて Logical にする。高さ・地表からの高さの引き算は Logical なので、|高さ| が数 km でも float の刻みが入らない。
        /// 描いている頂点の高さとは、頂点の float の丸め(µm の桁。<see cref="PlanetCellFrame"/>)の範囲で一致する。
        /// </summary>
        public bool TrySampleSurface(in LogicalVector3 point,out LogicalScalar height,out LogicalScalar altitude){
            height=default;altitude=default;
            if(this.Selector==null||!point.TryToFloat3(out Unity.Mathematics.float3 approximate)){return false;}
            Vector3 direction=((Vector3)approximate).normalized;
            if(direction==Vector3.zero){return false;}
            PlanetCellKey key=PlanetCellGeometry.KeyAt(direction,Mathf.Min(SampleLevel,this.MaximumLevel));
            PlanetCellFrame frame=this.Selector.Frame(in key);
            // 高さ場は半径 R の球面上の原点(SphereOrigin)から測る(持ち上げた原点ではない)。
            if(!LogicalVector3.TrySubtract(in point,frame.SphereOrigin,out LogicalVector3 difference)||!difference.TryToFloat3(out Unity.Mathematics.float3 offset)){return false;}
            Vector3 sphere=PlanetCellGeometry.SphereOffset(frame.ApproximateSphereOrigin/this.radius,(Vector3)offset/this.radius,this.radius,out Vector3 _);
            if(!this.TryProceduralHeight(in frame,sphere,0f,out float procedural)){return false;}
            double total=procedural;
            // 実データの高さは、点自身の方向から倍精度で直接求める(Cell の原点からの差 sphere は float で、大きな Cell では 2cm ほど誤差が出る)。
            if(this.rasters.Length>0){
                if(!this.TryRasterHeightAt(in point,out double raster,out float regionalWeight)){return false;}
                total+=raster;
                total+=this.AmplifiedHeight(sphere,0f,regionalWeight);
            }
            if(!LogicalScalar.TryFromDouble(total,out height)||!LogicalVector3.TryLength(in point,out LogicalScalar distance)||
                !LogicalScalar.TrySubtract(in distance,this.Selector.LogicalRadius,out LogicalScalar aboveRadius)||!LogicalScalar.TrySubtract(in aboveRadius,in height,out altitude)){
                return false;
            }
            return true;
        }

        // 点(惑星のローカル座標、Logical)の方向の実データの高さ(mip 0、倍精度の直接の補間)。
        private bool TryRasterHeightAt(in LogicalVector3 point,out double height,out float regionalWeight){
            height=0;regionalWeight=0f;
            if(!point.X.TryToDouble(out double x)||!point.Y.TryToDouble(out double y)||!point.Z.TryToDouble(out double z)){return false;}
            double global=this.rasters[0].SampleDirection(x,y,z);
            double result=global;
            if(this.rasters.Length>1){
                double regional=this.rasters[1].SampleDirection(x,y,z,out double weight);
                result=global+(regional-global)*weight;
                regionalWeight=(float)weight;
            }
            height=result;
            return true;
        }

        /// <summary>
        /// 惑星のローカル座標の点 point(Logical)の方向の、実データの帯域より上の細部(増幅)の項だけ(R からの高さ、m)。増幅が無効か、実データが無いなら 0。検証と、増幅を除いた高さを求めるのに使う。
        /// </summary>
        public bool TryAmplifiedHeight(in LogicalVector3 point,out float height){
            height=0f;
            if(this.Selector==null||!point.TryToFloat3(out Unity.Mathematics.float3 approximate)){return false;}
            if(this.rasters.Length==0||!(this.amplifyAmplitude>0f)){return true;}
            Vector3 direction=((Vector3)approximate).normalized;
            if(direction==Vector3.zero){return false;}
            PlanetCellKey key=PlanetCellGeometry.KeyAt(direction,Mathf.Min(SampleLevel,this.MaximumLevel));
            PlanetCellFrame frame=this.Selector.Frame(in key);
            if(!LogicalVector3.TrySubtract(in point,frame.SphereOrigin,out LogicalVector3 difference)||!difference.TryToFloat3(out Unity.Mathematics.float3 offset)){return false;}
            Vector3 sphere=PlanetCellGeometry.SphereOffset(frame.ApproximateSphereOrigin/this.radius,(Vector3)offset/this.radius,this.radius,out Vector3 _);
            int count=Mathf.Clamp(this.octaves,1,MaximumOctaves);
            if(!this.sampleLattice.TryFill(in frame,in this.inverseWavelength,count)){return false;}
            if(!this.TryRasterHeightAt(in point,out double _,out float regionalWeight)){return false;}
            height=this.AmplifiedHeight(sphere,0f,regionalWeight);
            return true;
        }

        // 増幅の項(sampleLattice が Cell の格子の基点で埋まっているとき)。GPU の PlanetAmplifiedHeight と同じ式。
        private float AmplifiedHeight(Vector3 sphere,float quadLength,float regionalWeight){
            if(this.rasters.Length==0||!(this.amplifyAmplitude>0f)){return 0f;}
            float cutoff=this.cutoffGlobalLog2+(this.cutoffRegionalLog2-this.cutoffGlobalLog2)*regionalWeight;
            int count=Mathf.Clamp(this.octaves,1,MaximumOctaves);
            return PlanetHeightField.Amplified(this.amplifyAmplitude,this.amplifyHurst,cutoff,1f/this.baseWavelength,count,this.sampleLattice,sphere,quadLength);
        }

        /// <summary>Cell の(球面上の)原点 <see cref="PlanetCellFrame.SphereOrigin"/> からの相対位置 sphere(球面上の点)の高さ(R からの高さ。quadLength で帯域制限。0 なら全部)。</summary>
        public bool TryHeight(in PlanetCellFrame frame,Vector3 sphere,float quadLength,out float height){
            return this.TryHeight(in frame,sphere,quadLength,0f,out height);
        }

        /// <summary>
        /// 同じで、高さから shift(Cell の原点を持ち上げた距離)を引いた値を返す(GPU の PlanetHeight と同じ式。実データは補間の前に引く)。
        /// </summary>
        public bool TryHeight(in PlanetCellFrame frame,Vector3 sphere,float quadLength,float shift,out float height){
            if(!this.TryProceduralHeight(in frame,sphere,quadLength,out height)){return false;}
            if(this.rasters.Length>0){
                if(!this.TryRasterCells(in frame,quadLength)){return false;}
                height+=this.RasterHeight(sphere,shift,out float regionalWeight);
                height+=this.AmplifiedHeight(sphere,quadLength,regionalWeight);
            }else{
                height-=shift;
            }
            return true;
        }

        // 手続きの高さだけ(実データを足さない)。
        private bool TryProceduralHeight(in PlanetCellFrame frame,Vector3 sphere,float quadLength,out float height){
            height=0;
            int count=Mathf.Clamp(this.octaves,1,MaximumOctaves);
            if(!this.sampleLattice.TryFill(in frame,in this.inverseWavelength,count)){return false;}
            height=PlanetHeightField.Evaluate(in this.heightSettings,this.heightAmplitude,1f/this.baseWavelength,count,this.sampleLattice,sphere,quadLength);
            return true;
        }

        // frame と quadLength の Cell ごとの値を求める(同じ Cell・同じ quad の長さの間は覚えておく)。
        private bool TryRasterCells(in PlanetCellFrame frame,float quadLength){
            if(this.rasterCacheValid&&this.rasterCacheKey.Equals(frame.Key)&&this.rasterCacheQuad==quadLength){return true;}
            for(int i=0;i<this.rasters.Length;i++){
                if(!this.rasters[i].TryCell(frame.SphereOrigin,quadLength,this.radius,out this.rasterCells[i])){return false;}
            }
            this.rasterCacheKey=frame.Key;this.rasterCacheQuad=quadLength;this.rasterCacheValid=true;
            return true;
        }

        // 実データの高さから shift を引いた値(PlanetRaster.hlsl の PlanetRasterHeight と同じ式)。TryRasterCells の後に呼ぶ。
        private float RasterHeight(Vector3 sphere,float shift,out float regionalWeight){
            regionalWeight=0f;
            float height=this.rasters[0].Sample(in this.rasterCells[0],sphere,this.radius,shift,out float _);
            if(this.rasters.Length>1){
                float regional=this.rasters[1].Sample(in this.rasterCells[1],sphere,this.radius,shift,out float weight);
                height+=(regional-height)*weight;
                regionalWeight=weight;
            }
            return height;
        }

        /// <summary>
        /// 検証用。GPU の生成(PlanetTerrainGenerate.compute)と同じ式で、Cell の格子の (i, j) の地表の点(持ち上げた Cell の原点からの相対位置、帯域制限あり)を CPU で求める。
        /// </summary>
        public bool TryEvaluateVertexForDiagnostics(in PlanetCellKey key,int i,int j,out Vector3 position){
            position=default;
            if(this.Selector==null){return false;}
            PlanetCellFrame frame=this.Selector.Frame(in key);
            Vector3 sphere=PlanetCellGeometry.SpherePoint(in frame,CellQuads,i,j,this.radius,out Vector3 direction);
            if(!this.TryHeight(in frame,sphere,this.Selector.QuadArc(key.Level),frame.HeightShift,out float height)){return false;}
            position=PlanetCellGeometry.VertexPosition(sphere,direction,height,frame.HeightShift,this.radius);
            return true;
        }

        protected override void BindGeneration(CommandBuffer commands,ComputeShader shader,int kernel){
            this.BindHeightField(commands,shader,kernel);
        }

        protected override void BindCell(CommandBuffer commands,ComputeShader shader,int slot,in PlanetCellFrame frame){
            this.BindHeightCell(commands,shader,in frame);
        }

        /// <summary>PlanetHeight.hlsl の全 Cell に共通の値を設定する(海の生成が水深を求めるときにも使う)。</summary>
        public void BindHeightField(CommandBuffer commands,ComputeShader shader,int kernel){
            // 格子の texture(無いときは 1×1 の空の texture)と、格子の数。
            if(this.emptyRaster==null){
                this.emptyRaster=new Texture2D(1,1,TextureFormat.RFloat,false,true){name="PlanetRasterEmpty",hideFlags=HideFlags.HideAndDontSave};
                this.emptyRaster.SetPixelData(new float[]{0f},0);this.emptyRaster.Apply(false,false);
            }
            Texture2D first=this.emptyRaster;Texture2D second=this.emptyRaster;
            if(this.rasters.Length>0){first=this.rasters[0].GetTexture();}
            if(this.rasters.Length>1){second=this.rasters[1].GetTexture();}
            commands.SetComputeTextureParam(shader,kernel,"_PlanetRaster0",first);
            commands.SetComputeTextureParam(shader,kernel,"_PlanetRaster1",second);
            commands.SetComputeIntParam(shader,"_RasterLayerCount",this.rasters.Length);
            commands.SetComputeFloatParam(shader,"_HeightAmplitude",this.heightAmplitude);
            commands.SetComputeFloatParam(shader,"_InverseWavelength",1f/this.baseWavelength);
            commands.SetComputeIntParam(shader,"_Octaves",Mathf.Clamp(this.octaves,1,MaximumOctaves));
            PlanetHeightSettings height=this.heightSettings;
            commands.SetComputeVectorParam(shader,"_HeightShape",new Vector4(height.SeaLevel,height.ShoreWidth,height.OceanDepth,height.PlainsHeight));
            commands.SetComputeVectorParam(shader,"_HeightBands",new Vector4(height.MountainOctave,height.MountainAmplitude,height.DetailOctave,height.DetailAmplitude));
            float amplify=this.amplifyAmplitude;
            if(this.rasters.Length==0){amplify=0f;}
            commands.SetComputeVectorParam(shader,"_HeightAmplify",new Vector4(amplify,this.amplifyHurst,this.cutoffGlobalLog2,this.cutoffRegionalLog2));
        }

        /// <summary>PlanetHeight.hlsl の Cell ごとの値(octave の格子の基点。高さ 0 の球面上の原点 SphereOrigin から求める)を設定する。_HeightShift は基底の生成が設定する。</summary>
        public void BindHeightCell(CommandBuffer commands,ComputeShader shader,in PlanetCellFrame frame){
            if(this.rasters.Length>0&&this.TryRasterCells(in frame,this.Selector.QuadArc(frame.Key.Level))){
                for(int i=0;i<this.rasters.Length;i++){
                    PlanetRasterCell cell=this.rasterCells[i];
                    this.rasterIndex[i*4]=cell.ColumnIndex;this.rasterIndex[i*4+1]=cell.RowIndex;this.rasterIndex[i*4+2]=cell.Mip;this.rasterIndex[i*4+3]=cell.Wrap;
                    this.rasterFraction[i]=new Vector4(cell.ColumnFraction,cell.RowFraction,cell.ColumnsPerRadian,cell.RowsPerRadian);
                    this.rasterSize[i*4]=cell.Width;this.rasterSize[i*4+1]=cell.Height;this.rasterSize[i*4+2]=cell.Regional;this.rasterSize[i*4+3]=cell.BaseWidth;
                    this.rasterRegional[i]=new Vector4(cell.RegionalInset,cell.RegionalFeather,cell.MipScale,cell.BaseHeight);
                    this.rasterPole[i]=new Vector4(cell.PoleNorth,cell.PoleSouth,0f,0f);
                }
                commands.SetComputeVectorParam(shader,"_RasterDirection",this.rasterCells[0].Direction);
                commands.SetComputeIntParams(shader,"_RasterIndex",this.rasterIndex);
                commands.SetComputeVectorArrayParam(shader,"_RasterFraction",this.rasterFraction);
                commands.SetComputeIntParams(shader,"_RasterSize",this.rasterSize);
                commands.SetComputeVectorArrayParam(shader,"_RasterRegional",this.rasterRegional);
                commands.SetComputeVectorArrayParam(shader,"_RasterPole",this.rasterPole);
            }
            int count=Mathf.Clamp(this.octaves,1,MaximumOctaves);
            for(int octave=0;octave<count;octave++){
                frame.TryNoiseLattice(in this.inverseWavelength,octave,out Vector3Int lattice,out Vector3 fraction);
                this.noiseLattice[octave*4]=lattice.x;this.noiseLattice[octave*4+1]=lattice.y;this.noiseLattice[octave*4+2]=lattice.z;
                this.noiseFraction[octave]=fraction;
            }
            commands.SetComputeIntParams(shader,"_NoiseLattice",this.noiseLattice);
            commands.SetComputeVectorArrayParam(shader,"_NoiseFraction",this.noiseFraction);
        }
    }
}
