using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using VirtualizedGeometry;

namespace PlanetSpike{
    /// <summary>
    /// P3a の試験用の供給元。平面上に cellsPerSide×cellsPerSide 個の cell を並べ、sin 波の高さで頂点を GPU 生成する。
    /// 全 cell を常に描くので、選択と LOD は試さない(pool・生成・bounds・cull・描画の経路だけを確かめる)。
    /// </summary>
    [ExecuteAlways]
    public sealed class PlanetSpikeGridSource:VirtualGeometryClusterSource{
        [SerializeField] private ComputeShader generator;
        [SerializeField,Min(1)] private int cellsPerSide=4;
        [SerializeField,Min(0.01f)] private float cellSize=10;
        [SerializeField,Min(0)] private float skirtDepth=0.5f;
        private readonly List<int> slots=new List<int>();
        private readonly List<Vector2> origins=new List<Vector2>();
        private int kernel=-1;

        public ComputeShader Generator{get=>this.generator;set=>this.generator=value;}
        public int CellCount=>this.slots.Count;

        protected override VirtualGeometryGridLayout CreateLayout(){
            return new VirtualGeometryGridLayout(64,8);
        }

        protected override void OnEnable(){
            base.OnEnable();
            this.slots.Clear();this.origins.Clear();
            for(int y=0;y<this.cellsPerSide;y++){
                for(int x=0;x<this.cellsPerSide;x++){
                    if(!this.TryAllocateSlot(out int slot)){return;}
                    this.slots.Add(slot);this.origins.Add(new Vector2(x*this.cellSize,y*this.cellSize));
                    this.RequestGeneration(slot);
                }
            }
        }

        protected override void OnDisable(){
            this.slots.Clear();this.origins.Clear();
            base.OnDisable();
        }

        protected override void SelectSlots(in VirtualGeometryClusterView view,List<int> drawSlots){
            drawSlots.AddRange(this.slots);
        }

        protected override void RecordGeneration(CommandBuffer commands,GraphicsBuffer poolVertices,IReadOnlyList<int> generate){
            if(this.generator==null){throw new System.InvalidOperationException("generatorが設定されていません。");}
            if(this.kernel<0){this.kernel=this.generator.FindKernel("Generate");}
            VirtualGeometryGridLayout layout=this.Layout;
            commands.SetComputeIntParam(this.generator,"_CellQuads",layout.CellQuads);
            commands.SetComputeIntParam(this.generator,"_SlotVertexCount",layout.SlotVertexCount);
            commands.SetComputeIntParam(this.generator,"_GridVertexCount",layout.GridVertexCount);
            commands.SetComputeFloatParam(this.generator,"_CellSize",this.cellSize);
            commands.SetComputeFloatParam(this.generator,"_SkirtDepth",this.skirtDepth);
            commands.SetComputeBufferParam(this.generator,this.kernel,"_Vertices",poolVertices);
            for(int i=0;i<generate.Count;i++){
                int index=this.slots.IndexOf(generate[i]);
                if(index<0){continue;}
                commands.SetComputeIntParam(this.generator,"_Slot",generate[i]);
                commands.SetComputeVectorParam(this.generator,"_CellOrigin",this.origins[index]);
                commands.DispatchCompute(this.generator,this.kernel,(layout.SlotVertexCount+63)/64,1,1);
            }
        }
    }
}
