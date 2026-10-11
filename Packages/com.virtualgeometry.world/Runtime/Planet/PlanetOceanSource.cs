using System;
using UnityEngine;
using UnityEngine.Rendering;
using LogicalScalar=Omni.Logical.Scalar;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.World{
    /// <summary>
    /// 海の Layer。海面(半径 R の球)を、地形と同じ Cell の仕組みで描き、Gerstner 波で動かす。頂点の生成は PlanetOceanGenerate.compute。
    /// 波は Pierson–Moskowitz スペクトルから決めた成分の和(方向は 3 次元の単位ベクトル、位相は Cell の原点で Logical から求める)。
    /// 帯域制限: 波長が 2·quadLength より短い成分は変位に入れない(頂点の間隔で表せない帯域。P5b で法線と粗さへ移す)。
    /// 波の入る階層の Cell は、描く frame ごとに作り直す(粗い Cell は静止した球面)。
    /// 水深(海面からの地形の深さ)は地形の高さの Field から求め、UV の x に入れる(P5b の shader で使う)。
    /// 海岸線は、fragment が補間した水深(UV の x)の clip で決まる(RadialOcean.shader。深度の交わりには頼らない)。
    /// </summary>
    [ExecuteAlways]
    public sealed class PlanetOceanSource:PlanetLayerSource{
        /// <summary>GPU の生成が受け取れる波の成分の数(PlanetOceanGenerate.compute の MAXIMUM_WAVES)。</summary>
        public const int MaximumWaves=32;
        private const float Gravity=9.81f;

        [SerializeField] private PlanetTerrainSource terrain;
        /// <summary>海面から高さ 19.5m の風速(m/s)。スペクトルの山の波長は約 2πg/(0.877g/U)²。</summary>
        [SerializeField,Min(0.1f)] private float windSpeed=10f;
        [SerializeField,Range(1,MaximumWaves)] private int waveCount=24;
        [SerializeField,Min(0.01f)] private float shortestWavelength=0.5f;
        /// <summary>Gerstner の尖り(0 で正弦波、1 で波頭が尖る直前)。</summary>
        [SerializeField,Range(0,1)] private float steepness=0.5f;
        [SerializeField] private int seed=1;
        /// <summary>false なら <see cref="WaveTime"/> を進めない(検証で時刻を固定する)。</summary>
        [SerializeField] private bool advanceTime=true;
        /// <summary>
        /// true なら、全頂点が陸(水深 &lt; 0)の Cell は描かない。fragment の clip(depth) が全画素を捨てるので、画像は変わらない(厳密)。
        /// 前提: (1) 材質が uv.x &lt; 0 の画素を clip する(RadialOcean.shader。材質の shader が RadialOcean でないときは、この設定を無視して描く)、
        /// (2) 地形の高さの Field(SetRasters、Configure、増幅の設定)を有効なまま変えたら <see cref="Refresh"/> で海を作り直す(水深は Cell を割り当てたときだけ縮約する)。
        /// 水深は生成時に GPU で縮約して読み戻す(数 frame 後に届くまでは描く)。
        /// </summary>
        [SerializeField] private bool skipLandCells=true;

        private Wave[] waves=Array.Empty<Wave>();
        private float amplitude;
        private float longestWavelength;
        private readonly Vector4[] waveDirection=new Vector4[MaximumWaves];
        private readonly Vector4[] waveShape=new Vector4[MaximumWaves];
        private readonly Vector4[] wavePhase=new Vector4[MaximumWaves];
        // slot ごとの、波の位相の端数(Cell の原点での値)。Cell が変わったときだけ求め直す(Logical の演算が波の数だけ要る)。
        private float[] slotPhases;
        private PlanetCellKey[] slotPhaseKeys;
        private bool[] slotPhaseValid;
        // slot の Cell の水深の縮約(全頂点が陸か)。landVersion は、縮約を要求した Cell の世代(slot を別の Cell に使い直すと合わなくなる)。
        private const byte LandUnknown=0;
        private const byte LandMixed=1;
        private const byte LandOnly=2;
        private GraphicsBuffer nonNegativeCounts;
        private byte[] landState;
        private int[] landVersion;
        private readonly System.Collections.Generic.List<int> reducedSlots=new System.Collections.Generic.List<int>();
        private int reduceKernel=-1;

        private struct Wave{
            public Vector3 Direction;
            public LogicalVector3 LogicalDirection;
            public float Wavelength;
            public LogicalScalar InverseWavelength;
            public float Amplitude;
            public float AngularFrequency;
            public float Sharpness;
        }

        public PlanetTerrainSource Terrain{get=>this.terrain;set=>this.terrain=value;}
        public override float Radius{get{if(this.terrain==null){return 0;}return this.terrain.Radius;}}
        /// <summary>波の変位の最大(上下と水平の和の上界)。</summary>
        public float WaveAmplitude=>this.amplitude;
        public float LongestWavelength=>this.longestWavelength;
        public int WaveCount=>this.waves.Length;
        /// <summary>
        /// 波の時刻(秒)。倍精度で持つ。float で持つと、5×10^5 秒(約 6 日)を超えると frame ごとの加算(約 0.016 秒)が丸めで消えて波が止まり、
        /// 位相 ω·t の積(ω は最大 11 rad/s)も 10^5 秒で 0.1 rad 以上狂う。位相は <see cref="TimePhase"/> が倍精度で 2π に巻いてから float にして渡す。
        /// </summary>
        public double WaveTime{get;set;}
        public bool AdvanceTime{get=>this.advanceTime;set=>this.advanceTime=value;}
        /// <summary>全頂点が陸の Cell を描かない(<see cref="PlanetLayerSource.LastSkippedCells"/> が数)。画像は変わらない。</summary>
        public bool SkipLandCells{get=>this.skipLandCells;set=>this.skipLandCells=value;}

        /// <summary>
        /// 海を作り直す(Cell の頂点と、水深の縮約の結果を捨てて、生成し直す)。地形の高さの Field(<see cref="PlanetTerrainSource.SetRasters"/>、Configure、増幅の設定)を
        /// 有効なまま変えたら呼ぶ。海は地形の変更を購読しないので、呼ばないと、陸だけと判定済みの Cell が古い水深のまま描かれない。
        /// </summary>
        public void Refresh(){
            this.Rebuild();
        }
        protected override float SelectionAmplitude=>this.amplitude;
        protected override string GeneratorResource=>"VirtualGeometryWorld/PlanetOceanGenerate";

        /// <summary>検証用。波と選択の設定を変える。有効なまま変えた場合は無効化して作り直す。</summary>
        public void Configure(float windSpeed,int waveCount,float shortestWavelength,int maximumLevel,float targetPixels){
            this.windSpeed=windSpeed;this.waveCount=Mathf.Clamp(waveCount,1,MaximumWaves);this.shortestWavelength=shortestWavelength;
            this.ConfigureSelection(maximumLevel,targetPixels);
            this.Rebuild();
        }

        protected override void OnEnable(){
            if(this.terrain==null){
                Debug.LogError("PlanetOceanSource: 地形(PlanetTerrainSource)が設定されていません。",this);
                return;
            }
            this.BuildWaves();
            base.OnEnable();
            int capacity=this.PoolCapacity;
            this.slotPhases=new float[capacity*MaximumWaves];
            this.slotPhaseKeys=new PlanetCellKey[capacity];
            this.slotPhaseValid=new bool[capacity];
            this.landState=new byte[capacity];this.landVersion=new int[capacity];
            for(int i=0;i<capacity;i++){this.landVersion[i]=-1;}
            this.reducedSlots.Clear();this.reduceKernel=-1;
            if(this.Layout!=null){this.nonNegativeCounts=new GraphicsBuffer(GraphicsBuffer.Target.Structured,capacity,4);}
        }

        protected override void OnDisable(){
            this.slotPhases=null;this.slotPhaseKeys=null;this.slotPhaseValid=null;
            this.landState=null;this.landVersion=null;this.reducedSlots.Clear();
            this.nonNegativeCounts?.Dispose();this.nonNegativeCounts=null;
            base.OnDisable();
        }

        private void Update(){
            if(this.advanceTime){this.WaveTime+=Time.deltaTime;}
        }

        /// <summary>時刻の位相 ω·t を、倍精度で [0, 2π) に巻いた値(float)。ω は float の値そのものを使う(GPU と CPU が同じ ω を使う)。</summary>
        public static float TimePhase(float angularFrequency,double time){
            double phase=Math.IEEERemainder((double)angularFrequency*time,2.0*Math.PI);
            if(phase<0){phase+=2.0*Math.PI;}
            return (float)phase;
        }

        // Pierson–Moskowitz スペクトルを、山の波長の 2 倍から shortestWavelength まで対数で等間隔に waveCount 個に分ける。
        private void BuildWaves(){
            int count=Mathf.Clamp(this.waveCount,1,MaximumWaves);
            float peakFrequency=0.877f*Gravity/this.windSpeed;
            float peakWavelength=2f*Mathf.PI*Gravity/(peakFrequency*peakFrequency);
            float longest=Mathf.Max(peakWavelength*2f,this.shortestWavelength);
            this.waves=new Wave[count];
            var random=new System.Random(this.seed);
            float total=0;
            for(int i=0;i<count;i++){
                float t=0;
                if(count>1){t=(float)i/(count-1);}
                float wavelength=longest*Mathf.Pow(this.shortestWavelength/longest,t);
                float k=2f*Mathf.PI/wavelength;
                float omega=Mathf.Sqrt(Gravity*k);
                // 隣の成分との中点までの角周波数の幅。
                float wider=longest*Mathf.Pow(this.shortestWavelength/longest,Mathf.Max(0,t-0.5f/Mathf.Max(1,count-1)));
                float narrower=longest*Mathf.Pow(this.shortestWavelength/longest,Mathf.Min(1,t+0.5f/Mathf.Max(1,count-1)));
                float band=Mathf.Sqrt(Gravity*2f*Mathf.PI/narrower)-Mathf.Sqrt(Gravity*2f*Mathf.PI/wider);
                float spectrum=0.0081f*Gravity*Gravity/Mathf.Pow(omega,5f)*Mathf.Exp(-0.74f*Mathf.Pow(Gravity/(this.windSpeed*omega),4f));
                float waveAmplitude=Mathf.Sqrt(2f*spectrum*Mathf.Max(band,0f));
                Vector3 direction=RandomDirection(random);
                float sharpness=0;
                if(waveAmplitude>0){sharpness=Mathf.Clamp01(this.steepness/(k*waveAmplitude*count));}
                LogicalVector3.TryFromFloat3(direction,out LogicalVector3 logicalDirection);
                LogicalScalar.TryFromFloat(wavelength,out LogicalScalar logicalWavelength);
                LogicalScalar.TryReciprocal(in logicalWavelength,out LogicalScalar inverse);
                this.waves[i]=new Wave{Direction=direction,LogicalDirection=logicalDirection,Wavelength=wavelength,InverseWavelength=inverse,
                    Amplitude=waveAmplitude,AngularFrequency=omega,Sharpness=sharpness};
                total+=waveAmplitude*(1f+sharpness);
            }
            this.amplitude=total;
            this.longestWavelength=longest;
        }

        private static Vector3 RandomDirection(System.Random random){
            for(int attempt=0;attempt<64;attempt++){
                var v=new Vector3((float)random.NextDouble()*2f-1f,(float)random.NextDouble()*2f-1f,(float)random.NextDouble()*2f-1f);
                float length=v.magnitude;
                if(length>0.1f&&length<=1f){return v/length;}
            }
            return Vector3.up;
        }

        /// <summary>この階層の Cell で、帯域制限で変位から落とす(波長が 2·quadLength より短い)波があるか。</summary>
        public bool DropsWaves(int level){
            return this.Selector!=null&&this.shortestWavelength<2f*this.Selector.QuadArc(level);
        }

        /// <summary>この階層の Cell に、変位に入る(波長が 2·quadLength 以上の)波があるか。</summary>
        public bool IsAnimated(int level){
            return this.Selector!=null&&this.longestWavelength>=2f*this.Selector.QuadArc(level);
        }

        protected override void OnCellDrawn(int slot,in PlanetCellFrame frame){
            if(this.IsAnimated(frame.Key.Level)){this.RequestRegeneration(slot);}
        }

        // 水深は時刻に依らないので、Cell を新しく生成したときだけ縮約する(作り直しでは求め直さない)。
        protected override void RecordAfterCellGeneration(CommandBuffer commands,ComputeShader shader,GraphicsBuffer poolVertices,int slot){
            if(this.nonNegativeCounts==null||this.landVersion==null){return;}
            int version=this.SlotVersion(slot);
            if(this.landVersion[slot]==version){return;}
            if(this.reduceKernel<0){this.reduceKernel=shader.FindKernel("ReduceDepth");}
            commands.SetComputeBufferParam(shader,this.reduceKernel,"_Vertices",poolVertices);
            commands.SetComputeBufferParam(shader,this.reduceKernel,"_NonNegativeCounts",this.nonNegativeCounts);
            commands.DispatchCompute(shader,this.reduceKernel,1,1,1);
            this.landVersion[slot]=version;this.landState[slot]=LandUnknown;
            this.reducedSlots.Add(slot);
        }

        // 縮約した slot の結果(水深が 0 以上の頂点の数)を読み戻す(数 frame 後に届く)。
        protected override void OnSlotsGenerated(System.Collections.Generic.IReadOnlyList<int> slots){
            base.OnSlotsGenerated(slots);
            if(this.nonNegativeCounts==null||!SystemInfo.supportsAsyncGPUReadback){this.reducedSlots.Clear();return;}
            for(int i=0;i<this.reducedSlots.Count;i++){
                int slot=this.reducedSlots[i];
                int version=this.SlotVersion(slot);
                AsyncGPUReadback.Request(this.nonNegativeCounts,4,slot*4,request=>this.ReceiveLandCount(request,slot,version));
            }
            this.reducedSlots.Clear();
        }

        private void ReceiveLandCount(AsyncGPUReadbackRequest request,int slot,int version){
            if(request.hasError||this==null||this.landVersion==null||this.landVersion[slot]!=version||this.SlotVersion(slot)!=version){return;}
            uint count=request.GetData<uint>()[0];
            this.landState[slot]=LandMixed;
            if(count==0){this.landState[slot]=LandOnly;}
        }

        protected override bool SkipDraw(int slot){
            return this.skipLandCells&&this.MaterialClipsLand()&&this.landState!=null&&this.landState[slot]==LandOnly&&this.landVersion[slot]==this.SlotVersion(slot);
        }

        // 飛ばしても画像が変わらないのは、材質が uv.x < 0 の画素を clip する(RadialOcean.shader)ときだけ。材質を取り替えたときだけ、shader の名前を調べ直す。
        private Material clipMaterial;
        private bool clipMaterialResult;

        private bool MaterialClipsLand(){
            Material current=this.Material;
            if(current==null){return false;}
            if(!ReferenceEquals(current,this.clipMaterial)){
                this.clipMaterial=current;
                this.clipMaterialResult=current.shader!=null&&current.shader.name.EndsWith("RadialOcean",StringComparison.Ordinal);
            }
            return this.clipMaterialResult;
        }

        /// <summary>検証用。Cell が生成済みで、水深の縮約の結果が届いているとき true。landOnly は、全頂点が陸(描かない Cell)か。</summary>
        public bool TryGetLandOnlyForDiagnostics(in PlanetCellKey key,out bool landOnly){
            landOnly=false;
            if(!this.TryGetSlotForDiagnostics(in key,out int slot)||this.landState==null||this.landVersion[slot]!=this.SlotVersion(slot)||this.landState[slot]==LandUnknown){return false;}
            landOnly=this.landState[slot]==LandOnly;
            return true;
        }

        /// <summary>検証用。Cell の格子の (i, j) の水深(海面からの地形の深さ。負なら陸)を CPU で求める(UV の x と同じ式)。</summary>
        public bool TryEvaluateDepthForDiagnostics(in PlanetCellKey key,int i,int j,out float depth){
            depth=0;
            if(this.Selector==null){return false;}
            PlanetCellFrame frame=this.Selector.Frame(in key);
            Vector3 sphere=PlanetCellGeometry.SpherePoint(in frame,CellQuads,i,j,this.Radius,out Vector3 _);
            if(!this.terrain.TryHeight(in frame,sphere,this.Selector.QuadArc(key.Level),out float height)){return false;}
            depth=-height;
            return true;
        }

        // 波の方向・形・時刻の位相(全 Cell 共通)を、配列に書く。
        private void FillWaveArrays(){
            for(int i=0;i<this.waves.Length;i++){
                Wave wave=this.waves[i];
                this.waveDirection[i]=new Vector4(wave.Direction.x,wave.Direction.y,wave.Direction.z,1f/wave.Wavelength);
                // 時刻の位相 ω·t は、倍精度で 2π に巻いてから float にして渡す(t が大きくなっても sin の引数を小さく保つ)。
                float timePhase=TimePhase(wave.AngularFrequency,this.WaveTime);
                this.waveShape[i]=new Vector4(wave.Amplitude,wave.Sharpness,timePhase,wave.Wavelength);
            }
        }

        // slot の Cell の、波の位相の端数を waves の数だけ wavePhase に書く(slot ごとに cache)。
        private void FillWavePhases(int slot,in PlanetCellFrame frame){
            if(!this.slotPhaseValid[slot]||!this.slotPhaseKeys[slot].Equals(frame.Key)){
                for(int i=0;i<this.waves.Length;i++){
                    frame.TryWavePhase(in this.waves[i].LogicalDirection,in this.waves[i].InverseWavelength,out float fraction);
                    this.slotPhases[slot*MaximumWaves+i]=fraction;
                }
                this.slotPhaseKeys[slot]=frame.Key;this.slotPhaseValid[slot]=true;
            }
            for(int i=0;i<this.waves.Length;i++){this.wavePhase[i]=new Vector4(this.slotPhases[slot*MaximumWaves+i],0,0,0);}
        }

        protected override void BindGeneration(CommandBuffer commands,ComputeShader shader,int kernel){
            this.terrain.BindHeightField(commands,shader,kernel);
            this.FillWaveArrays();
            commands.SetComputeFloatParam(shader,"_WaveCount",this.waves.Length);
            commands.SetComputeVectorArrayParam(shader,"_WaveDirection",this.waveDirection);
            commands.SetComputeVectorArrayParam(shader,"_WaveShape",this.waveShape);
        }

        protected override void BindCell(CommandBuffer commands,ComputeShader shader,int slot,in PlanetCellFrame frame){
            this.terrain.BindHeightCell(commands,shader,in frame);
            this.FillWavePhases(slot,in frame);
            commands.SetComputeVectorArrayParam(shader,"_WavePhase",this.wavePhase);
        }

        /// <summary>fragment の RadialOcean.shader が使う値(波。Cell の向きと quad の大きさは基底が書く)を書く。</summary>
        protected override void WriteCellProperties(MaterialPropertyBlock block,int slot,in PlanetCellFrame frame){
            base.WriteCellProperties(block,slot,in frame);
            this.FillWaveArrays();
            this.FillWavePhases(slot,in frame);
            block.SetFloat("_WaveCount",this.waves.Length);
            block.SetVectorArray("_WaveDirection",this.waveDirection);
            block.SetVectorArray("_WaveShape",this.waveShape);
            block.SetVectorArray("_WavePhase",this.wavePhase);
        }

        /// <summary>検証用。波の成分(倍精度の参照を別に書くための値)。</summary>
        public readonly struct WaveInfo{
            public readonly Vector3 Direction;
            public readonly float Wavelength;
            public readonly float Amplitude;
            public readonly float AngularFrequency;
            public readonly float Sharpness;

            public WaveInfo(Vector3 direction,float wavelength,float amplitude,float angularFrequency,float sharpness){
                this.Direction=direction;this.Wavelength=wavelength;this.Amplitude=amplitude;this.AngularFrequency=angularFrequency;this.Sharpness=sharpness;
            }
        }

        /// <summary>検証用。波 index の成分。</summary>
        public WaveInfo GetWave(int index){
            Wave wave=this.waves[index];
            return new WaveInfo(wave.Direction,wave.Wavelength,wave.Amplitude,wave.AngularFrequency,wave.Sharpness);
        }

        /// <summary>検証用。波 index の、Cell の原点での位相の端数(0〜1。shader へ渡す値と同じ)。</summary>
        public bool TryWavePhaseFraction(in PlanetCellFrame frame,int index,out float fraction){
            return frame.TryWavePhase(in this.waves[index].LogicalDirection,in this.waves[index].InverseWavelength,out fraction);
        }

        /// <summary>
        /// 検証用。PlanetOcean.hlsl の uniform(_WaveCount、_WaveDirection、_WaveShape、_WavePhase)を、compute shader に書く(fragment の MaterialPropertyBlock と同じ値)。
        /// </summary>
        public bool BindWavesForDiagnostics(ComputeShader shader,in PlanetCellKey key){
            if(this.Selector==null){return false;}
            PlanetCellFrame frame=this.Selector.Frame(in key);
            this.FillWaveArrays();
            for(int i=0;i<this.waves.Length;i++){
                if(!this.TryWavePhaseFraction(in frame,i,out float fraction)){return false;}
                this.wavePhase[i]=new Vector4(fraction,0,0,0);
            }
            shader.SetFloat("_WaveCount",this.waves.Length);
            shader.SetVectorArray("_WaveDirection",this.waveDirection);
            shader.SetVectorArray("_WaveShape",this.waveShape);
            shader.SetVectorArray("_WavePhase",this.wavePhase);
            return true;
        }

        /// <summary>
        /// 検証用。GPU の生成(PlanetOceanGenerate.compute)と同じ式で、Cell の格子の (i, j) の海面の点(Cell の原点からの相対位置)と水深を CPU で求める。
        /// bandLimited が false なら、帯域制限をせずに全成分を足す(帯域制限が効いているかを比べるため)。
        /// </summary>
        public bool TryEvaluateVertexForDiagnostics(in PlanetCellKey key,int i,int j,bool bandLimited,out Vector3 position,out float depth){
            position=default;depth=0;
            if(this.Selector==null){return false;}
            PlanetCellFrame frame=this.Selector.Frame(in key);
            Vector3 sphere=PlanetCellGeometry.SpherePoint(in frame,CellQuads,i,j,this.Radius,out Vector3 direction);
            float quadLength=this.Selector.QuadArc(key.Level);
            Vector3 offset=Vector3.zero;
            for(int w=0;w<this.waves.Length;w++){
                Wave wave=this.waves[w];
                if(bandLimited&&wave.Wavelength<2f*quadLength){continue;}
                frame.TryWavePhase(in wave.LogicalDirection,in wave.InverseWavelength,out float fraction);
                float timePhase=TimePhase(wave.AngularFrequency,this.WaveTime);
                float theta=2f*Mathf.PI*(fraction+Vector3.Dot(wave.Direction,sphere)/wave.Wavelength)-timePhase;
                Vector3 along=wave.Direction-direction*Vector3.Dot(wave.Direction,direction);
                offset+=direction*(wave.Amplitude*Mathf.Sin(theta))+along*(wave.Sharpness*wave.Amplitude*Mathf.Cos(theta));
            }
            if(!this.terrain.TryHeight(in frame,sphere,quadLength,out float height)){return false;}
            position=sphere+offset;depth=-height;
            return true;
        }

        /// <summary>
        /// 検証用。<see cref="TryEvaluateVertexForDiagnostics"/>(GPU と同じ float の式)の波の変位を、時刻の位相と三角関数を倍精度で評価した位置(Cell の原点からの相対位置)。
        /// GPU と CPU が同じ float の式で同じ誤り(長時間後の位相の狂い)をしても、これと比べれば分かる。位相は ω·t を倍精度で巻く(<see cref="TimePhase"/> は使わず、別に書く)。
        /// </summary>
        public bool TryEvaluateVertexInDouble(in PlanetCellKey key,int i,int j,bool bandLimited,out Vector3 position){
            position=default;
            if(this.Selector==null){return false;}
            PlanetCellFrame frame=this.Selector.Frame(in key);
            Vector3 sphere=PlanetCellGeometry.SpherePoint(in frame,CellQuads,i,j,this.Radius,out Vector3 direction);
            float quadLength=this.Selector.QuadArc(key.Level);
            double offsetX=0,offsetY=0,offsetZ=0;
            for(int w=0;w<this.waves.Length;w++){
                Wave wave=this.waves[w];
                if(bandLimited&&wave.Wavelength<2f*quadLength){continue;}
                frame.TryWavePhase(in wave.LogicalDirection,in wave.InverseWavelength,out float fraction);
                double product=(double)wave.AngularFrequency*this.WaveTime;
                double timePhase=product-2.0*Math.PI*Math.Floor(product/(2.0*Math.PI));
                double theta=2.0*Math.PI*((double)fraction+(double)Vector3.Dot(wave.Direction,sphere)/wave.Wavelength)-timePhase;
                double sine=Math.Sin(theta);double cosine=Math.Cos(theta);
                Vector3 along=wave.Direction-direction*Vector3.Dot(wave.Direction,direction);
                double vertical=wave.Amplitude*sine;double horizontal=(double)wave.Sharpness*wave.Amplitude*cosine;
                offsetX+=direction.x*vertical+along.x*horizontal;
                offsetY+=direction.y*vertical+along.y*horizontal;
                offsetZ+=direction.z*vertical+along.z*horizontal;
            }
            position=sphere+new Vector3((float)offsetX,(float)offsetY,(float)offsetZ);
            return true;
        }
    }
}
