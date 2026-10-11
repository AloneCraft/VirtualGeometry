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
    /// 海岸線は地形との深度の交わりで決まる。
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
        public float WaveTime{get;set;}
        public bool AdvanceTime{get=>this.advanceTime;set=>this.advanceTime=value;}
        protected override float SelectionAmplitude=>this.amplitude;
        protected override bool UsesCellProperties=>true;
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
        }

        protected override void OnDisable(){
            this.slotPhases=null;this.slotPhaseKeys=null;this.slotPhaseValid=null;
            base.OnDisable();
        }

        private void Update(){
            if(this.advanceTime){this.WaveTime+=Time.deltaTime;}
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

        // 波の方向・形・時刻の位相(全 Cell 共通)を、配列に書く。
        private void FillWaveArrays(){
            for(int i=0;i<this.waves.Length;i++){
                Wave wave=this.waves[i];
                this.waveDirection[i]=new Vector4(wave.Direction.x,wave.Direction.y,wave.Direction.z,1f/wave.Wavelength);
                // 時刻の位相 ω·t は 2π で巻いてから渡す(float の t が大きくなっても sin の引数を小さく保つ)。
                float timePhase=Mathf.Repeat(wave.AngularFrequency*this.WaveTime,2f*Mathf.PI);
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
            this.terrain.BindHeightField(commands,shader);
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

        /// <summary>fragment の RadialOcean.shader が使う値(波、Cell の向きと quad の大きさ)を書く。</summary>
        protected override void WriteCellProperties(MaterialPropertyBlock block,int slot,in PlanetCellFrame frame){
            this.FillWaveArrays();
            this.FillWavePhases(slot,in frame);
            block.SetFloat("_WaveCount",this.waves.Length);
            block.SetVectorArray("_WaveDirection",this.waveDirection);
            block.SetVectorArray("_WaveShape",this.waveShape);
            block.SetVectorArray("_WavePhase",this.wavePhase);
            Vector3 up=frame.ApproximateOrigin.normalized;
            block.SetVector("_OceanUp",new Vector4(up.x,up.y,up.z,this.Radius));
            block.SetVector("_OceanCell",new Vector4(this.Selector.QuadArc(frame.Key.Level),0,0,0));
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
                float timePhase=Mathf.Repeat(wave.AngularFrequency*this.WaveTime,2f*Mathf.PI);
                float theta=2f*Mathf.PI*(fraction+Vector3.Dot(wave.Direction,sphere)/wave.Wavelength)-timePhase;
                Vector3 along=wave.Direction-direction*Vector3.Dot(wave.Direction,direction);
                offset+=direction*(wave.Amplitude*Mathf.Sin(theta))+along*(wave.Sharpness*wave.Amplitude*Mathf.Cos(theta));
            }
            if(!this.terrain.TryHeight(in frame,sphere,quadLength,out float height)){return false;}
            position=sphere+offset;depth=-height;
            return true;
        }
    }
}
