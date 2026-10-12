using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;
using VirtualizedGeometry;

namespace VirtualGeometry.VirtualObjects{
    /// <summary>VG メッシュの表現の設定。</summary>
    [Serializable]
    public sealed class VirtualGeometryRepresentationSettings:IRepresentationSettings,IVirtualObjectDependencySource{
        [Tooltip("VirtualGeometryRenderer を持つプレハブ(モデルの取り込みで Virtual Geometry を有効にしたもの)。")]
        public GameObject Prefab;
        [Tooltip("GPU の memory の見積もり(byte)。")]
        public long GpuBytes=8*1024*1024;
        [Tooltip("CPU の memory の見積もり(byte)。")]
        public long CpuBytes=1024*1024;

        public void CollectDependencies(VirtualObjectDependencies dependencies){
            dependencies.AddPrefab(this.Prefab);
        }
    }

    /// <summary>
    /// VG メッシュ(<see cref="VirtualGeometryRenderer"/>)で見せる表現(中景)。プレハブを置き、VG が実際に描いたら見えたとする
    /// (読み込みは表示で部品が有効になってから始まり、その間は粗い表現が残る)。
    /// 代替 Mesh を描ける配置(<see cref="VirtualGeometryRenderer.ProxyFallback"/>)では、代替 Mesh が出た時点でも見えたとする。
    /// Omni の配置は、VG の部品に Omni の差し込み口を付けて代替 Mesh を止める(VirtualGeometry.Omni の OmniObjectPlacement)。
    /// </summary>
    [VirtualObjectRepresentation("vgmesh",typeof(VirtualGeometryRepresentationSettings))]
    public sealed class VirtualGeometryRepresentation:GameObjectRepresentation{
        private VirtualGeometryRenderer[] renderers=Array.Empty<VirtualGeometryRenderer>();
        private int shownFrame=-1;

        public VirtualGeometryRepresentation(in RepresentationRequest request):base(in request){
        }

        private VirtualGeometryRepresentationSettings Settings=>this.Band.Settings as VirtualGeometryRepresentationSettings;

        /// <summary>この表現の VG の部品(準備が済むまで空)。</summary>
        public IReadOnlyList<VirtualGeometryRenderer> Renderers=>this.renderers;

        public override RepresentationCost EstimateCost(){
            VirtualGeometryRepresentationSettings settings=this.Settings;
            if(settings==null){return default;}
            return new RepresentationCost(settings.GpuBytes,settings.CpuBytes,0.1f,0.5f);
        }

        protected override void OnBeginPrepare(VirtualObjectContext context,CancellationToken cancellation){
            VirtualGeometryRepresentationSettings settings=this.Settings;
            if(settings==null||settings.Prefab==null){throw new InvalidOperationException("VG メッシュの表現にプレハブがありません。");}
            this.Instance=UnityEngine.Object.Instantiate(settings.Prefab,context.Pool,false);
            this.Instance.hideFlags=HideFlags.DontSave;
            this.renderers=this.Instance.GetComponentsInChildren<VirtualGeometryRenderer>(true);
            if(this.renderers.Length==0){throw new InvalidOperationException("VG メッシュの表現のプレハブに VirtualGeometryRenderer がありません。");}
        }

        protected override void OnActivate(VirtualObjectContext context){
            base.OnActivate(context);
            this.shownFrame=Time.frameCount;
        }

        protected override bool OnIsVisible(){
            if(this.shownFrame<0){return false;}
            bool fallbackShown=base.OnIsVisible();
            for(int i=0;i<this.renderers.Length;i++){
                VirtualGeometryRenderer renderer=this.renderers[i];
                if(renderer==null){return false;}
                if(renderer.LastVirtualDrawFrame>=this.shownFrame){continue;}
                if(renderer.ProxyFallback&&renderer.IsFallbackVisible&&fallbackShown){continue;}
                return false;
            }
            return true;
        }

        protected override void OnDeactivate(VirtualObjectContext context){
            base.OnDeactivate(context);
            this.shownFrame=-1;
        }

        protected override void OnRelease(VirtualObjectContext context){
            this.renderers=Array.Empty<VirtualGeometryRenderer>();
            base.OnRelease(context);
        }
    }

    /// <summary>親の部分のメッシュの表現の設定。</summary>
    [Serializable]
    public sealed class PartMeshRepresentationSettings:IRepresentationSettings,IVirtualObjectDependencySource{
        public Material Material;
        [Tooltip("部分の三角形を、平均の法線の向きに押し出す距離(m、局所)。0 なら親の面と同じ位置。")]
        public float Offset;

        public void CollectDependencies(VirtualObjectDependencies dependencies){
            dependencies.AddMaterial(this.Material);
        }
    }

    /// <summary>
    /// 親の粗いメッシュの、この子の番号の部分(<see cref="MeshClusterPartition"/>)の三角形を、そのまま 1 つのメッシュにして見せる表現(「型 + 番号」の子の基本形)。
    /// 子の原点は部分の中心なので、頂点は親の局所から部分の中心を引いた位置。親の部分を全部の子が覆うので、親を外しても穴にならない。
    /// 番号ごとの違い(材質など)は、割り当ての規則で型を変えて表す。
    /// </summary>
    [VirtualObjectRepresentation("partmesh",typeof(PartMeshRepresentationSettings))]
    public sealed class PartMeshRepresentation:GameObjectRepresentation{
        private readonly List<int> triangles=new List<int>();
        private Mesh mesh;

        public PartMeshRepresentation(in RepresentationRequest request):base(in request){
        }

        private PartMeshRepresentationSettings Settings=>this.Band.Settings as PartMeshRepresentationSettings;

        public override RepresentationCost EstimateCost(){
            return new RepresentationCost(64*1024,64*1024,0.01f,0.2f);
        }

        protected override void OnBeginPrepare(VirtualObjectContext context,CancellationToken cancellation){
            PartMeshRepresentationSettings settings=this.Settings;
            if(settings==null||settings.Material==null){throw new InvalidOperationException("部分のメッシュの表現に材質がありません。");}
            VirtualObjectNode parent=this.Node.Parent;
            if(parent==null||parent.Definition==null||!(parent.Definition.Partition is MeshClusterPartition partition)){
                throw new InvalidOperationException("部分のメッシュの表現は、メッシュの cluster で分けた親の子にだけ使えます。");
            }
            Mesh source=partition.ResolveMesh(parent);
            if(source==null||!partition.TryGetTriangles(parent,this.Node.Number,this.triangles)||!partition.TryGetPart(parent,this.Node.Number,out VirtualObjectPart part)){
                throw new InvalidOperationException("親の部分 "+this.Node.Number+" の三角形を読めません。");
            }
            this.mesh=BuildMesh(source,this.triangles,part.ApproximateOrigin,part.Normal*settings.Offset);
            this.mesh.name="Part "+this.Node.Path;
            var root=new GameObject("Part "+this.Node.Path);
            root.hideFlags=HideFlags.DontSave;
            root.transform.SetParent(context.Pool,false);
            root.AddComponent<MeshFilter>().sharedMesh=this.mesh;
            var renderer=root.AddComponent<MeshRenderer>();
            renderer.sharedMaterial=settings.Material;
            renderer.shadowCastingMode=ShadowCastingMode.Off;
            this.Instance=root;
        }

        protected override void OnRelease(VirtualObjectContext context){
            base.OnRelease(context);
            if(this.mesh!=null){
                // GameObject と同じ frame 数の後に捨てる(描画の仕組みが、外した frame の破棄を許さないため)。
                context.RetireMesh(this.mesh);
                this.mesh=null;
            }
        }

        /// <summary>source の三角形 triangles(番号)を、origin を原点にしたメッシュにする(頂点を三角形ごとに複製し、法線とUVを写す)。shift は全部の頂点に足す。</summary>
        public static Mesh BuildMesh(Mesh source,IReadOnlyList<int> triangles,Vector3 origin,Vector3 shift){
            Vector3[] vertices=source.vertices;
            Vector3[] normals=source.normals;
            Vector2[] uvs=source.uv;
            int[] indices=source.triangles;
            int count=triangles.Count*3;
            var positions=new Vector3[count];
            var outNormals=new Vector3[count];
            var outUvs=new Vector2[count];
            var outIndices=new int[count];
            bool hasNormals=normals.Length==vertices.Length;
            bool hasUvs=uvs.Length==vertices.Length;
            for(int t=0;t<triangles.Count;t++){
                for(int k=0;k<3;k++){
                    int v=indices[triangles[t]*3+k];
                    int o=t*3+k;
                    positions[o]=vertices[v]-origin+shift;
                    if(hasNormals){outNormals[o]=normals[v];}
                    if(hasUvs){outUvs[o]=uvs[v];}
                    outIndices[o]=o;
                }
            }
            var result=new Mesh{hideFlags=HideFlags.DontSave};
            if(count>65535){result.indexFormat=IndexFormat.UInt32;}
            result.vertices=positions;
            if(hasNormals){result.normals=outNormals;}else{result.RecalculateNormals();}
            if(hasUvs){result.uv=outUvs;}
            result.triangles=outIndices;
            result.RecalculateBounds();
            return result;
        }
    }
}
