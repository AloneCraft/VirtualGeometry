using System;
using Omni.BatchRendering;
using UnityEngine;
using UnityEngine.Rendering;
using L=Omni.Logical;

namespace Omni.PlanetSurface{
    public sealed partial class PlanetSurfaceLod{
        private const long RockCpuFixedBytes=102400;
        private SurfaceRockField rockField;
        private SurfaceRockQuery rockQuery;
        private SurfaceRockSelection rockSelection;
        private SurfaceRockMeshBuilder rockBuilder,rockWorkspace;
        private SurfaceVector requestedRockDirection,publishedRockDirection;
        private bool rocksRequested,rocksPublished;
        private readonly GameObject[] rockOwners=new GameObject[2];
        private readonly L.Transform[] rockPoses=new L.Transform[2];
        private readonly MeshFilter[] rockFilters=new MeshFilter[2];
        private readonly MeshRenderer[] rockRenderers=new MeshRenderer[2];
        private readonly Mesh[] rockMeshes=new Mesh[2];
        private int rockBackBuffer;
        private int rockPublishedBuffer=-1;
        private readonly RadialBatchInstanceHandle[] rockHandles=new RadialBatchInstanceHandle[2];
        public int RockCapacity{get;private set;}
        /// <summary>岩の型(opt-in、SurfaceRockMeshBuilder.ShapeTemplates)。Initializeより前に設定します。nullなら従来の20面体です。</summary>
        public Vector3[][] RockShapeTemplates{get;set;}
        /// <summary>
        /// 岩だけに1を設定する材質のfloatプロパティ名(opt-in)。設定すると岩は地表材質の複製を使い、シェーダーが岩の面を見分けられます。
        /// nullなら従来どおり地表と同じ材質です。Initializeより前に設定します。
        /// </summary>
        public string RockMaterialFlagProperty{get;set;}
        private Material rockMaterial;
        public int VisibleRockCount{get;private set;}
        public int CompletedRockUpdates{get;private set;}
        public int RockBufferPreparationCount{
            get{
                if(this.rockWorkspace==null){return 0;}
                return this.rockWorkspace.BufferPreparationCount;
            }
        }
        public double LastRockWorkMilliseconds{get;private set;}
        public string LastRockWorkStage{get;private set;}="idle";
        /// <summary>2つのMesh、CPU生成配列、候補と管理領域を含む保守的な予約です。実機計測値ではありません。</summary>
        public long RockCpuReservedBytes{get;private set;}
        public long RockGpuReservedBytes{get;private set;}

        /// <summary>MoonSurfaceVerificationのPlanetとEarthToMoonのMoonRootで、生成物だけを所有します。</summary>
        private void InitializeRocks(){
            this.RockCapacity=(int)Math.Min(this.m_Profile.MaximumVisibleRocks,
                Math.Min(Math.Max(0,this.m_Profile.CpuBudgetBytes/8-RockCpuFixedBytes)/32768,
                    Math.Max(0,this.m_Profile.GpuBudgetBytes/16-4096)/16384));
            if(this.RockCapacity==0){return;}
            this.rockField=new SurfaceRockField(this.m_Definition.RadiusMeters,this.m_Definition.Generation.Seed,this.m_Definition.Rocks);
            this.RockCpuReservedBytes=RockCpuFixedBytes+32768L*this.RockCapacity;
            this.RockGpuReservedBytes=4096+16384L*this.RockCapacity;
            // 容量依存の配列確保を、最初の地表接近フレームではなく初期化へ移します。
            this.rockWorkspace=new SurfaceRockMeshBuilder(this.RockCapacity,
                this.m_Definition.RadiusMeters,this.m_HeightSource){ShapeTemplates=this.RockShapeTemplates};
            this.rockSelection=new SurfaceRockSelection(new SurfaceVector(1,0,0),
                this.m_Definition.RadiusMeters,this.RockCapacity);
            Material material=this.m_Material;
            if(!string.IsNullOrEmpty(this.RockMaterialFlagProperty)&&material!=null){
                this.rockMaterial=new Material(material){name=material.name+" Rocks",hideFlags=HideFlags.DontSave};
                this.rockMaterial.SetFloat(this.RockMaterialFlagProperty,1);
                material=this.rockMaterial;
            }
            for(int i=0;i<2;i++){
                this.rockMeshes[i]=new Mesh{name="Nearby Rocks "+i,hideFlags=HideFlags.DontSave};
                this.rockMeshes[i].MarkDynamic();
                this.rockWorkspace.PrepareTarget(this.rockMeshes[i]);
                this.rockOwners[i]=new GameObject("PlanetSurface Nearby Rocks "+i){hideFlags=HideFlags.DontSave};
                this.rockOwners[i].transform.SetParent(this.transform,false);
                this.rockPoses[i]=this.rockOwners[i].AddComponent<L.Transform>();
                this.rockFilters[i]=this.rockOwners[i].AddComponent<MeshFilter>();
                this.rockFilters[i].sharedMesh=this.rockMeshes[i];
                this.rockRenderers[i]=this.rockOwners[i].AddComponent<MeshRenderer>();
                // 各Rendererは生涯同じMeshを参照します。二重バッファ公開はvisibilityだけを交換し、
                // 全residentのDrawReference再構築を定常的に発生させません。
                this.rockRenderers[i].sharedMaterial=material;this.rockRenderers[i].enabled=false;
                this.rockRenderers[i].shadowCastingMode=ShadowCastingMode.On;
                this.rockRenderers[i].receiveShadows=true;
            }
        }

        /// <summary>CPU生成と探索は最大0.2ms枠、転送は別フレームに分離し、LOD全体の時計で打ち切ります。</summary>
        private bool UpdateRockWork(SurfaceVector direction,double altitude){
            this.LastRockWorkStage="visibility";
            if(this.rockField==null){return false;}
            double range=this.m_Profile.RockDrawDistanceMeters;
            if(altitude>range){
                this.SetRocksVisible(false);this.CancelRockWork();return false;
            }
            if(this.rocksPublished&&((direction-this.publishedRockDirection)*this.m_Definition.RadiusMeters).Length>range){
                this.SetRocksVisible(false);
            }
            double moved=((direction-this.requestedRockDirection)*this.m_Definition.RadiusMeters).Length;
            if(SurfaceRockUpdatePolicy.ShouldRestart(this.rocksRequested,this.rockQuery!=null||this.rockBuilder!=null,moved,range)){
                this.LastRockWorkStage="request";
                this.requestedRockDirection=direction;this.rocksRequested=true;this.rockBuilder=null;
                this.rockQuery=this.rockField.Query(direction,range);
                this.rockSelection.Reset(direction);
            }
            double end=Math.Min(this.updateTimer.Elapsed.TotalMilliseconds+0.2,
                this.m_Profile.UpdateBudgetMilliseconds-AtomicWorkReserveMilliseconds);
            while(this.updateTimer.Elapsed.TotalMilliseconds<end){
                if(this.rockQuery!=null){
                    if(!this.rockQuery.IsComplete){
                        this.LastRockWorkStage="query";
                        if(this.rockQuery.Step(out var rock)&&SurfaceRockField.IsAllowedBySurface(this.m_HeightSource,rock)){
                            this.rockSelection.Consider(rock);
                        }
                        continue;
                    }
                    this.LastRockWorkStage="allocate";
                    double height=this.m_HeightSource.SampleHeightMeters(this.requestedRockDirection.X,
                        this.requestedRockDirection.Y,this.requestedRockDirection.Z);
                    this.LastRockWorkStage="reset";
                    this.rockBuilder=this.rockWorkspace;
                    this.rockBuilder.ResetFromSelection(this.rockSelection,
                        this.requestedRockDirection*(this.m_Definition.RadiusMeters+height));
                    this.rockQuery=null;return false;
                }
                if(this.rockBuilder==null){this.LastRockWorkStage="idle";return false;}
                if(!this.rockBuilder.IsComplete){this.LastRockWorkStage="build";this.rockBuilder.Step();continue;}
                if(this.updateTimer.Elapsed.TotalMilliseconds+AtomicWorkReserveMilliseconds>
                    this.m_Profile.UpdateBudgetMilliseconds){return false;}
                Mesh mesh=this.rockMeshes[this.rockBackBuffer];
                if(!this.rockBuilder.IsUploadComplete){
                    this.rockBuilder.UploadStep(mesh);
                    this.LastRockWorkStage=this.rockBuilder.LastUploadStage;
                    // 完成前は旧Meshを継続します。小さな転送後は共有時計の残りをLODへ返し、
                    // 約100回の分割転送の間、地形の追従そのものを停止させません。
                    return this.LastRockWorkStage!="upload-vertices"&&this.LastRockWorkStage!="upload-indices";
                }
                this.LastRockWorkStage="publish";
                int published=this.rockBackBuffer;
                SurfaceVector origin=this.rockBuilder.Origin;
                this.rockPoses[published].LocalPosition=Logical(origin.X,origin.Y,origin.Z);
                this.rockPoses[published].SyncUnityTransform();
                this.VisibleRockCount=this.rockBuilder.CompletedRocks;
                for(int i=0;i<2;i++){
                    bool visible=i==published&&this.VisibleRockCount>0;
                    this.rockRenderers[i].enabled=visible;
                    if(!this.rockHandles[i].IsValid&&visible){
                        this.rockHandles[i]=RadialBatchRendererRegistry.Register(this.rockRenderers[i]);
                    }else if(this.rockHandles[i].IsValid){
                        RadialBatchDirtyFlags flags=RadialBatchDirtyFlags.Visibility;
                        if(i==published){flags|=RadialBatchDirtyFlags.MeshVertexData|RadialBatchDirtyFlags.Transform;}
                        RadialBatchRendererRegistry.MarkDirty(this.rockHandles[i],flags);
                    }
                }
                this.rockPublishedBuffer=this.VisibleRockCount>0?published:-1;
                this.publishedRockDirection=this.requestedRockDirection;this.rocksPublished=true;
                this.CompletedRockUpdates++;
                this.rockBackBuffer=1-this.rockBackBuffer;this.rockBuilder=null;return true;
            }
            return false;
        }

        private void SetRocksVisible(bool visible){
            for(int i=0;i<2;i++){
                bool next=visible&&i==this.rockPublishedBuffer&&this.VisibleRockCount>0;
                if(this.rockRenderers[i]==null||this.rockRenderers[i].enabled==next){continue;}
                this.rockRenderers[i].enabled=next;
                if(this.rockHandles[i].IsValid){RadialBatchRendererRegistry.MarkDirty(this.rockHandles[i],RadialBatchDirtyFlags.Visibility);}
            }
            if(!visible){this.VisibleRockCount=0;this.rockPublishedBuffer=-1;}
        }
        private void CancelRockWork(){
            this.rockQuery=null;this.rockBuilder=null;this.rocksRequested=false;
        }
        private void ShutdownRocks(){
            this.CancelRockWork();
            for(int i=0;i<2;i++){
                if(this.rockHandles[i].IsValid){RadialBatchRendererRegistry.Unregister(this.rockHandles[i]);this.rockHandles[i]=default;}
                if(this.rockOwners[i]!=null){DestroyOwned(this.rockOwners[i]);this.rockOwners[i]=null;}
                if(this.rockMeshes[i]!=null){DestroyOwned(this.rockMeshes[i]);this.rockMeshes[i]=null;}
                this.rockPoses[i]=null;this.rockFilters[i]=null;this.rockRenderers[i]=null;
            }
            if(this.rockMaterial!=null){DestroyOwned(this.rockMaterial);this.rockMaterial=null;}
            this.rockField=null;
            this.rockWorkspace=null;this.rockSelection=null;
            this.RockCapacity=0;this.VisibleRockCount=0;this.RockCpuReservedBytes=0;this.RockGpuReservedBytes=0;
            this.rocksPublished=false;this.rockBackBuffer=0;this.rockPublishedBuffer=-1;
            this.CompletedRockUpdates=0;this.LastRockWorkMilliseconds=0;this.LastRockWorkStage="idle";
        }
    }
}
