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

        private LogicalScalar inverseWavelength;
        private readonly int[] noiseLattice=new int[4*MaximumOctaves];
        private readonly Vector4[] noiseFraction=new Vector4[MaximumOctaves];
        private readonly PlanetHeightLattice sampleLattice=new PlanetHeightLattice();

        public override float Radius=>this.radius;
        public float HeightAmplitude=>this.heightAmplitude;
        public PlanetHeightSettings HeightSettings=>this.heightSettings;
        protected override float SelectionAmplitude=>this.heightAmplitude;
        protected override string GeneratorResource=>"VirtualGeometryWorld/PlanetTerrainGenerate";

        /// <summary>検証用。形の設定を変える。有効なまま変えた場合は無効化して作り直す。</summary>
        public void Configure(float radius,float heightAmplitude,int maximumLevel,float targetPixels){
            this.radius=radius;this.heightAmplitude=heightAmplitude;
            this.ConfigureSelection(maximumLevel,targetPixels);
            this.Rebuild();
        }

        protected override void OnEnable(){
            if(!LogicalScalar.TryFromFloat(this.baseWavelength,out LogicalScalar wavelength)||!LogicalScalar.TryReciprocal(in wavelength,out this.inverseWavelength)){
                Debug.LogError("PlanetTerrainSource: 波長を Logical にできません。",this);
            }
            base.OnEnable();
        }

        /// <summary>
        /// 惑星のローカル座標の点 point(Logical)の真下(中心からの方向)の地表の高さ(R からの高さ、帯域制限なし)と、point の地表からの高さを CPU で求める。
        /// GPU の生成と同じ式(<see cref="PlanetHeightField"/>)で、点を含む Cell の原点からの相対位置で計算する。
        /// </summary>
        public bool TrySampleSurface(in LogicalVector3 point,out float height,out float altitude){
            height=0;altitude=0;
            if(this.Selector==null||!point.TryToFloat3(out Unity.Mathematics.float3 approximate)){return false;}
            Vector3 direction=((Vector3)approximate).normalized;
            if(direction==Vector3.zero){return false;}
            PlanetCellKey key=PlanetCellGeometry.KeyAt(direction,Mathf.Min(SampleLevel,this.MaximumLevel));
            PlanetCellFrame frame=this.Selector.Frame(in key);
            if(!LogicalVector3.TrySubtract(in point,frame.Origin,out LogicalVector3 difference)||!difference.TryToFloat3(out Unity.Mathematics.float3 offset)){return false;}
            Vector3 sphere=PlanetCellGeometry.SphereOffset(frame.ApproximateOrigin/this.radius,(Vector3)offset/this.radius,this.radius,out Vector3 _);
            if(!this.TryHeight(in frame,sphere,0f,out height)){return false;}
            if(!LogicalVector3.TryLength(in point,out LogicalScalar distance)||!LogicalScalar.TrySubtract(in distance,this.Selector.LogicalRadius,out LogicalScalar aboveRadius)||
                !aboveRadius.TryToFloat(out float above)){
                return false;
            }
            altitude=above-height;
            return true;
        }

        /// <summary>Cell の原点からの相対位置 sphere(球面上の点)の高さ(quadLength で帯域制限。0 なら全部)。</summary>
        public bool TryHeight(in PlanetCellFrame frame,Vector3 sphere,float quadLength,out float height){
            height=0;
            int count=Mathf.Clamp(this.octaves,1,MaximumOctaves);
            if(!this.sampleLattice.TryFill(in frame,in this.inverseWavelength,count)){return false;}
            height=PlanetHeightField.Evaluate(in this.heightSettings,this.heightAmplitude,1f/this.baseWavelength,count,this.sampleLattice,sphere,quadLength);
            return true;
        }

        /// <summary>
        /// 検証用。GPU の生成(PlanetTerrainGenerate.compute)と同じ式で、Cell の格子の (i, j) の地表の点(Cell の原点からの相対位置、帯域制限あり)を CPU で求める。
        /// </summary>
        public bool TryEvaluateVertexForDiagnostics(in PlanetCellKey key,int i,int j,out Vector3 position){
            position=default;
            if(this.Selector==null){return false;}
            PlanetCellFrame frame=this.Selector.Frame(in key);
            Vector3 sphere=PlanetCellGeometry.SpherePoint(in frame,CellQuads,i,j,this.radius,out Vector3 direction);
            if(!this.TryHeight(in frame,sphere,this.Selector.QuadArc(key.Level),out float height)){return false;}
            position=sphere+direction*height;
            return true;
        }

        protected override void BindGeneration(CommandBuffer commands,ComputeShader shader,int kernel){
            this.BindHeightField(commands,shader);
        }

        protected override void BindCell(CommandBuffer commands,ComputeShader shader,in PlanetCellFrame frame){
            this.BindHeightCell(commands,shader,in frame);
        }

        /// <summary>PlanetHeight.hlsl の全 Cell に共通の値を設定する(海の生成が水深を求めるときにも使う)。</summary>
        public void BindHeightField(CommandBuffer commands,ComputeShader shader){
            commands.SetComputeFloatParam(shader,"_HeightAmplitude",this.heightAmplitude);
            commands.SetComputeFloatParam(shader,"_InverseWavelength",1f/this.baseWavelength);
            commands.SetComputeIntParam(shader,"_Octaves",Mathf.Clamp(this.octaves,1,MaximumOctaves));
            PlanetHeightSettings height=this.heightSettings;
            commands.SetComputeVectorParam(shader,"_HeightShape",new Vector4(height.SeaLevel,height.ShoreWidth,height.OceanDepth,height.PlainsHeight));
            commands.SetComputeVectorParam(shader,"_HeightBands",new Vector4(height.MountainOctave,height.MountainAmplitude,height.DetailOctave,height.DetailAmplitude));
        }

        /// <summary>PlanetHeight.hlsl の Cell ごとの値(octave の格子の基点)を設定する。</summary>
        public void BindHeightCell(CommandBuffer commands,ComputeShader shader,in PlanetCellFrame frame){
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
