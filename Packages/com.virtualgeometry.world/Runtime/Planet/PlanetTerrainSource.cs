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

        // 実データの高さの格子(0: 全球、1: 局所の Override)。ある間は、高さに足す(PlanetRaster.hlsl)。
        private PlanetRasterLayer[] rasters=new PlanetRasterLayer[0];
        private Texture2D emptyRaster;
        private readonly PlanetRasterCell[] rasterCells=new PlanetRasterCell[2];
        private readonly int[] rasterIndex=new int[8];
        private readonly Vector4[] rasterFraction=new Vector4[2];
        private readonly int[] rasterSize=new int[8];
        private readonly Vector4[] rasterRegional=new Vector4[2];
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
        protected override float SelectionAmplitude=>this.heightAmplitude+this.RasterMaximumHeight;
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

        /// <summary>検証用。形の設定を変える。有効なまま変えた場合は無効化して作り直す。</summary>
        public void Configure(float radius,float heightAmplitude,int maximumLevel,float targetPixels){
            this.radius=radius;this.heightAmplitude=heightAmplitude;
            this.ConfigureSelection(maximumLevel,targetPixels);
            this.Rebuild();
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
            this.Rebuild();
        }

        protected override void OnEnable(){
            if(!LogicalScalar.TryFromFloat(this.baseWavelength,out LogicalScalar wavelength)||!LogicalScalar.TryReciprocal(in wavelength,out this.inverseWavelength)){
                Debug.LogError("PlanetTerrainSource: 波長を Logical にできません。",this);
            }
            base.OnEnable();
        }

        protected override void OnDisable(){
            base.OnDisable();
            if(this.emptyRaster!=null){
                if(Application.isPlaying){Destroy(this.emptyRaster);}else{DestroyImmediate(this.emptyRaster);}
                this.emptyRaster=null;
            }
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
            if(!this.TryProceduralHeight(in frame,sphere,0f,out height)){return false;}
            // 実データの高さは、点自身の方向から倍精度で直接求める(Cell の原点からの差 sphere は float で、大きな Cell では 2cm ほど誤差が出る)。
            if(this.rasters.Length>0){
                if(!this.TryRasterHeightAt(in point,out float raster)){return false;}
                height+=raster;
            }
            if(!LogicalVector3.TryLength(in point,out LogicalScalar distance)||!LogicalScalar.TrySubtract(in distance,this.Selector.LogicalRadius,out LogicalScalar aboveRadius)||
                !aboveRadius.TryToFloat(out float above)){
                return false;
            }
            altitude=above-height;
            return true;
        }

        // 点(惑星のローカル座標、Logical)の方向の実データの高さ(mip 0、倍精度の直接の補間)。
        private bool TryRasterHeightAt(in LogicalVector3 point,out float height){
            height=0;
            if(!point.X.TryToDouble(out double x)||!point.Y.TryToDouble(out double y)||!point.Z.TryToDouble(out double z)){return false;}
            double global=this.rasters[0].SampleDirection(x,y,z);
            double result=global;
            if(this.rasters.Length>1){
                double regional=this.rasters[1].SampleDirection(x,y,z,out double weight);
                result=global+(regional-global)*weight;
            }
            height=(float)result;
            return true;
        }

        /// <summary>Cell の原点からの相対位置 sphere(球面上の点)の高さ(quadLength で帯域制限。0 なら全部)。</summary>
        public bool TryHeight(in PlanetCellFrame frame,Vector3 sphere,float quadLength,out float height){
            if(!this.TryProceduralHeight(in frame,sphere,quadLength,out height)){return false;}
            if(this.rasters.Length>0){
                if(!this.TryRasterCells(in frame,quadLength)){return false;}
                height+=this.RasterHeight(sphere);
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
                if(!this.rasters[i].TryCell(frame.Origin,quadLength,this.radius,out this.rasterCells[i])){return false;}
            }
            this.rasterCacheKey=frame.Key;this.rasterCacheQuad=quadLength;this.rasterCacheValid=true;
            return true;
        }

        // 実データの高さ(PlanetRaster.hlsl の PlanetRasterHeight と同じ式)。TryRasterCells の後に呼ぶ。
        private float RasterHeight(Vector3 sphere){
            float height=this.rasters[0].Sample(in this.rasterCells[0],sphere,this.radius,out float _);
            if(this.rasters.Length>1){
                float regional=this.rasters[1].Sample(in this.rasterCells[1],sphere,this.radius,out float weight);
                height+=(regional-height)*weight;
            }
            return height;
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
        }

        /// <summary>PlanetHeight.hlsl の Cell ごとの値(octave の格子の基点)を設定する。</summary>
        public void BindHeightCell(CommandBuffer commands,ComputeShader shader,in PlanetCellFrame frame){
            if(this.rasters.Length>0&&this.TryRasterCells(in frame,this.Selector.QuadArc(frame.Key.Level))){
                for(int i=0;i<this.rasters.Length;i++){
                    PlanetRasterCell cell=this.rasterCells[i];
                    this.rasterIndex[i*4]=cell.ColumnIndex;this.rasterIndex[i*4+1]=cell.RowIndex;this.rasterIndex[i*4+2]=cell.Mip;this.rasterIndex[i*4+3]=cell.Wrap;
                    this.rasterFraction[i]=new Vector4(cell.ColumnFraction,cell.RowFraction,cell.ColumnsPerRadian,cell.RowsPerRadian);
                    this.rasterSize[i*4]=cell.Width;this.rasterSize[i*4+1]=cell.Height;this.rasterSize[i*4+2]=cell.Regional;this.rasterSize[i*4+3]=cell.BaseWidth;
                    this.rasterRegional[i]=new Vector4(cell.RegionalInset,cell.RegionalFeather,cell.MipScale,cell.BaseHeight);
                }
                commands.SetComputeVectorParam(shader,"_RasterDirection",this.rasterCells[0].Direction);
                commands.SetComputeIntParams(shader,"_RasterIndex",this.rasterIndex);
                commands.SetComputeVectorArrayParam(shader,"_RasterFraction",this.rasterFraction);
                commands.SetComputeIntParams(shader,"_RasterSize",this.rasterSize);
                commands.SetComputeVectorArrayParam(shader,"_RasterRegional",this.rasterRegional);
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
