using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualizedGeometry.Editor{
    /// <summary>Meshから抽出した、Unityオブジェクト寿命に依存しない変換入力。</summary>
    public sealed class VirtualGeometryMeshInput{
        public Vector3[] Positions{get;}
        public Vector3[] Normals{get;}
        public Vector2[] UV{get;}
        public int[][] SubmeshIndices{get;}

        public VirtualGeometryMeshInput(Vector3[] positions,Vector3[] normals,Vector2[] uv,int[][] submeshIndices){
            this.Positions=positions??throw new ArgumentNullException(nameof(positions));
            this.Normals=normals??throw new ArgumentNullException(nameof(normals));
            this.UV=uv??throw new ArgumentNullException(nameof(uv));
            this.SubmeshIndices=submeshIndices??throw new ArgumentNullException(nameof(submeshIndices));
        }

        public static VirtualGeometryMeshInput FromMesh(Mesh mesh){
            if(mesh==null){throw new ArgumentNullException(nameof(mesh));}
            if(!mesh.isReadable){return FromMeshData(mesh);}
            int[][] submeshes=new int[mesh.subMeshCount][];
            for(int i=0;i<submeshes.Length;i++){
                if(mesh.GetTopology(i)!=MeshTopology.Triangles){throw new ArgumentException("Only triangle submeshes can be converted.",nameof(mesh));}
                submeshes[i]=mesh.GetIndices(i,true);
            }
            return new VirtualGeometryMeshInput(mesh.vertices,mesh.normals,mesh.uv,submeshes);
        }

        // モデルのインポート中や読み書き無効のMeshは、通常の配列取得が拒否される。Editorではread-only snapshotから読める。
        private static VirtualGeometryMeshInput FromMeshData(Mesh mesh){
            using(Mesh.MeshDataArray array=Mesh.AcquireReadOnlyMeshData(mesh)){
                Mesh.MeshData data=array[0];
                int count=data.vertexCount;
                Vector3[] positions=new Vector3[count];Vector3[] normals=new Vector3[0];Vector2[] uv=new Vector2[0];
                using(var buffer=new Unity.Collections.NativeArray<Vector3>(count,Unity.Collections.Allocator.Temp)){
                    data.GetVertices(buffer);buffer.CopyTo(positions);
                    if(data.HasVertexAttribute(VertexAttribute.Normal)){normals=new Vector3[count];data.GetNormals(buffer);buffer.CopyTo(normals);}
                }
                if(data.HasVertexAttribute(VertexAttribute.TexCoord0)){
                    uv=new Vector2[count];
                    using(var buffer=new Unity.Collections.NativeArray<Vector2>(count,Unity.Collections.Allocator.Temp)){data.GetUVs(0,buffer);buffer.CopyTo(uv);}
                }
                int[][] submeshes=new int[data.subMeshCount][];
                for(int i=0;i<submeshes.Length;i++){
                    SubMeshDescriptor descriptor=data.GetSubMesh(i);
                    if(descriptor.topology!=MeshTopology.Triangles){throw new ArgumentException("Only triangle submeshes can be converted.",nameof(mesh));}
                    using(var buffer=new Unity.Collections.NativeArray<int>(descriptor.indexCount,Unity.Collections.Allocator.Temp)){
                        data.GetIndices(buffer,i,true);submeshes[i]=buffer.ToArray();
                    }
                }
                return new VirtualGeometryMeshInput(positions,normals,uv,submeshes);
            }
        }
    }

    /// <summary>クラスタ化、共通位置量子化、境界保持階層を制御する変換設定。</summary>
    [Serializable]
    public sealed class VirtualGeometryBuildSettings{
        [SerializeField,Range(1,128)] private int maxTrianglesPerCluster=64;
        [SerializeField] private bool generateHierarchy=true;
        [SerializeField,Range(2,8)] private int hierarchyFanout=4;
        [SerializeField,Range(0.1f,0.9f)] private float simplificationRatio=0.5f;
        [SerializeField,Range(0,24)] private int positionQuantizationBits=16;

        public VirtualGeometryBuildSettings Clone(){return (VirtualGeometryBuildSettings)this.MemberwiseClone();}
        public bool GenerateStreamingPages=true;
        public int MaxTrianglesPerCluster{get=>this.maxTrianglesPerCluster;set=>this.maxTrianglesPerCluster=value;}
        public bool GenerateHierarchy{get=>this.generateHierarchy;set=>this.generateHierarchy=value;}
        public int HierarchyFanout{get=>this.hierarchyFanout;set=>this.hierarchyFanout=value;}
        public float SimplificationRatio{get=>this.simplificationRatio;set=>this.simplificationRatio=value;}
        public int PositionQuantizationBits{get=>this.positionQuantizationBits;set=>this.positionQuantizationBits=value;}
    }

    public enum VirtualGeometryDiagnosticSeverity{
        Info,
        Warning
    }

    public readonly struct VirtualGeometryBuildDiagnostic{
        public string Code{get;}
        public VirtualGeometryDiagnosticSeverity Severity{get;}
        public string Message{get;}

        public VirtualGeometryBuildDiagnostic(string code,VirtualGeometryDiagnosticSeverity severity,string message){
            this.Code=code;this.Severity=severity;this.Message=message;
        }
    }

    public sealed class VirtualGeometryBuildOutput{
        public VirtualGeometryPackedVertex[] Vertices{get;}
        public uint[] Indices{get;}
        public VirtualGeometryCluster[] Clusters{get;}
        public VirtualGeometryHierarchyNode[] Nodes{get;}
        public uint[] RootNodeIndices{get;}
        public VirtualGeometryBuildDiagnostic[] Diagnostics{get;}
        public Bounds Bounds{get;}
        public int InputTriangleCount{get;}
        public int LeafTriangleCount{get;}
        /// <summary>変換結果のgeometryと階層。streaming archiveはここから直接書き出す。</summary>
        public VirtualGeometryPage Page{get;}
        private byte[] pageBytes;private string contentHash;
        /// <summary>単一page形式のbyte列。大きなmeshでは書き出しが重いため、参照されたときに初めて作る。</summary>
        public byte[] PageBytes{
            get{if(this.pageBytes==null){this.pageBytes=VirtualGeometryPageCodec.Write(this.Page);}return this.pageBytes;}
        }
        public string ContentHash{
            get{if(this.contentHash==null){this.contentHash=VirtualGeometryBuilder.ComputeSha256(this.PageBytes);}return this.contentHash;}
        }

        public VirtualGeometryBuildOutput(VirtualGeometryPage page,VirtualGeometryBuildDiagnostic[] diagnostics,int inputTriangleCount,int leafTriangleCount){
            this.Page=page;this.Vertices=page.Vertices;this.Indices=page.Indices;this.Clusters=page.Clusters;this.Nodes=page.Nodes;
            this.RootNodeIndices=page.RootNodeIndices;this.Diagnostics=diagnostics;this.Bounds=page.Bounds;
            this.InputTriangleCount=inputTriangleCount;this.LeafTriangleCount=leafTriangleCount;
        }
    }

    /// <summary>入力を検査し、共有頂点を維持したクラスタと原子的な境界保持LOD階層へ変換する。</summary>
    public static class VirtualGeometryBuilder{
        private readonly struct Triangle{
            public readonly int A;public readonly int B;public readonly int C;public readonly int Material;public readonly int Order;public readonly uint Morton;
            public Triangle(int a,int b,int c,int material,int order,uint morton){
                this.A=a;this.B=b;this.C=c;this.Material=material;this.Order=order;this.Morton=morton;
            }
        }

        private readonly struct OrderedTriangle{
            public readonly uint A;public readonly uint B;public readonly uint C;public readonly int Order;public readonly uint Morton;
            public OrderedTriangle(uint a,uint b,uint c,int order,uint morton){this.A=a;this.B=b;this.C=c;this.Order=order;this.Morton=morton;}
        }

        private readonly struct TriangleKey:IEquatable<TriangleKey>{
            private readonly int a;private readonly int b;private readonly int c;private readonly int material;
            public TriangleKey(int a,int b,int c,int material){
                if(a>b){(a,b)=(b,a);}if(b>c){(b,c)=(c,b);}if(a>b){(a,b)=(b,a);}
                this.a=a;this.b=b;this.c=c;this.material=material;
            }
            public bool Equals(TriangleKey other){return this.a==other.a&&this.b==other.b&&this.c==other.c&&this.material==other.material;}
            public override bool Equals(object obj){return obj is TriangleKey other&&this.Equals(other);}
            public override int GetHashCode(){unchecked{return (((this.a*397)^this.b)*397^this.c)*397^this.material;}}
        }

        private readonly struct EdgeKey:IEquatable<EdgeKey>{
            public int A{get;}public int B{get;}
            public EdgeKey(int a,int b){if(a<b){this.A=a;this.B=b;}else{this.A=b;this.B=a;}}
            public bool Equals(EdgeKey other){return this.A==other.A&&this.B==other.B;}
            public override bool Equals(object obj){return obj is EdgeKey other&&this.Equals(other);}
            public override int GetHashCode(){unchecked{return this.A*397^this.B;}}
        }

        public static VirtualGeometryBuildOutput Build(Mesh mesh,VirtualGeometryBuildSettings settings){
            return Build(VirtualGeometryMeshInput.FromMesh(mesh),settings);
        }

        [ThreadStatic] private static CancellationToken buildCancellation;
        [ThreadStatic] private static Action<float> buildProgress;
        public static VirtualGeometryBuildOutput Build(VirtualGeometryMeshInput input,VirtualGeometryBuildSettings settings){
            return Build(input,settings,CancellationToken.None,null);
        }
        public static VirtualGeometryBuildOutput Build(VirtualGeometryMeshInput input,VirtualGeometryBuildSettings settings,CancellationToken cancellation,Action<float> progress=null){
            CancellationToken previous=buildCancellation;Action<float> previousProgress=buildProgress;
            buildCancellation=cancellation;buildProgress=progress;
            try{cancellation.ThrowIfCancellationRequested();progress?.Invoke(0);var output=BuildCore(input,settings);cancellation.ThrowIfCancellationRequested();progress?.Invoke(1);return output;}
            finally{buildCancellation=previous;buildProgress=previousProgress;}
        }
        private static VirtualGeometryBuildOutput BuildCore(VirtualGeometryMeshInput input,VirtualGeometryBuildSettings settings){
            if(input==null){throw new ArgumentNullException(nameof(input));}
            if(settings==null){throw new ArgumentNullException(nameof(settings));}
            ValidateSettings(settings);
            ValidateAttributes(input);buildCancellation.ThrowIfCancellationRequested();buildProgress?.Invoke(0.1f);
            Vector3[] positions=QuantizePositions(input.Positions,settings.PositionQuantizationBits,out float quantizationError);
            VirtualGeometryMeshInput working=new VirtualGeometryMeshInput(positions,input.Normals,input.UV,input.SubmeshIndices);
            Bounds bounds=CalculateBounds(positions);
            List<VirtualGeometryBuildDiagnostic> diagnostics=new List<VirtualGeometryBuildDiagnostic>();
            List<Triangle> triangles=CollectTriangles(working,bounds,diagnostics,out int inputTriangleCount);
            DiagnoseTopology(triangles,diagnostics);buildCancellation.ThrowIfCancellationRequested();buildProgress?.Invoke(0.25f);
            triangles.Sort((left,right)=>{
                int comparison=left.Material.CompareTo(right.Material);
                if(comparison==0){comparison=left.Morton.CompareTo(right.Morton);}
                if(comparison==0){comparison=left.Order.CompareTo(right.Order);}
                return comparison;
            });
            Vector3[] normals=PrepareNormals(working,triangles);
            VirtualGeometryPackedVertex[] vertices=new VirtualGeometryPackedVertex[positions.Length];
            for(int i=0;i<vertices.Length;i++){
                Vector2 uv=input.UV.Length==0?Vector2.zero:input.UV[i];
                vertices[i]=new VirtualGeometryPackedVertex(positions[i],normals[i],uv);
            }
            if(settings.PositionQuantizationBits>0){
                diagnostics.Add(new VirtualGeometryBuildDiagnostic("VG2003",VirtualGeometryDiagnosticSeverity.Info,
                    "Quantized positions on a mesh-wide "+settings.PositionQuantizationBits+"-bit grid; maximum displacement="+
                    quantizationError.ToString("R",CultureInfo.InvariantCulture)+" units."));
            }

            List<uint> indices=new List<uint>(triangles.Count*4);
            List<VirtualGeometryCluster> clusters=new List<VirtualGeometryCluster>();
            List<VirtualGeometryHierarchyNode> nodes=new List<VirtualGeometryHierarchyNode>();
            uint[] roots=new uint[input.SubmeshIndices.Length];
            for(int i=0;i<roots.Length;i++){roots[i]=uint.MaxValue;}
            uint leafOrdinal=0;
            int triangleCursor=0;
            int nonProgressCount=0;
            int stalledChainCount=0;
            int maximumLevel=0;
            HashSet<Vector3> materialSeams=CollectMaterialSeams(triangles,positions);
            for(int material=0;material<input.SubmeshIndices.Length;material++){
                buildCancellation.ThrowIfCancellationRequested();buildProgress?.Invoke(0.35f+0.5f*material/input.SubmeshIndices.Length);
                List<int> currentNodes=new List<int>();
                while(triangleCursor<triangles.Count&&triangles[triangleCursor].Material<material){triangleCursor++;}
                while(triangleCursor<triangles.Count&&triangles[triangleCursor].Material==material){
                    int end=Math.Min(triangleCursor+settings.MaxTrianglesPerCluster,triangles.Count);
                    while(end>triangleCursor&&triangles[end-1].Material!=material){end--;}
                    uint[] leafIndices=new uint[(end-triangleCursor)*3];
                    int offset=0;
                    for(int i=triangleCursor;i<end;i++){
                        leafIndices[offset++]=(uint)triangles[i].A;leafIndices[offset++]=(uint)triangles[i].B;leafIndices[offset++]=(uint)triangles[i].C;
                    }
                    int firstCluster=AppendClusters(leafIndices,material,quantizationError,settings.MaxTrianglesPerCluster,vertices,indices,clusters);
                    if(settings.GenerateHierarchy){
                        VirtualGeometryCluster cluster=clusters[firstCluster];
                        int nodeIndex=nodes.Count;
                        nodes.Add(new VirtualGeometryHierarchyNode{
                            BoundsCenter=cluster.BoundsCenter,BoundsRadius=cluster.BoundsRadius,GeometricError=quantizationError,
                            FirstCluster=(uint)firstCluster,ClusterCount=1,FirstChild=0,ChildCount=0,PageId=0,MaterialSlot=(uint)material,
                            Level=0,LeafStart=leafOrdinal++,LeafCount=1
                        });
                        currentNodes.Add(nodeIndex);
                    }
                    triangleCursor=end;
                }
                if(settings.GenerateHierarchy&&currentNodes.Count>0){
                    uint root=BuildHierarchyForMaterial(material,currentNodes,settings,vertices,bounds,materialSeams,indices,clusters,nodes,ref nonProgressCount,ref stalledChainCount,out int level);
                    roots[material]=root;
                    maximumLevel=Math.Max(maximumLevel,level);
                }
            }
            if(settings.GenerateHierarchy&&nodes.Count>0){
                diagnostics.Add(new VirtualGeometryBuildDiagnostic("VG2001",VirtualGeometryDiagnosticSeverity.Info,
                    "Built "+nodes.Count+" hierarchy nodes through level "+maximumLevel+" with atomic child replacement."));
                if(nonProgressCount>0){
                    diagnostics.Add(new VirtualGeometryBuildDiagnostic("VG2002",VirtualGeometryDiagnosticSeverity.Warning,
                        nonProgressCount+" hierarchy groups could not be simplified and retain their child representation."));
                }
                if(stalledChainCount>0){
                    diagnostics.Add(new VirtualGeometryBuildDiagnostic("VG2004",VirtualGeometryDiagnosticSeverity.Warning,
                        stalledChainCount+" material roots stopped above one cluster because simplification made no progress."));
                }
            }
            VirtualGeometryPage page=new VirtualGeometryPage(vertices,indices.ToArray(),clusters.ToArray(),nodes.ToArray(),roots,input.SubmeshIndices.Length,bounds);
            buildCancellation.ThrowIfCancellationRequested();buildProgress?.Invoke(0.9f);
            return new VirtualGeometryBuildOutput(page,diagnostics.ToArray(),inputTriangleCount,triangles.Count);
        }

        private static uint BuildHierarchyForMaterial(int material,List<int> leaves,VirtualGeometryBuildSettings settings,VirtualGeometryPackedVertex[] vertices,
            Bounds meshBounds,HashSet<Vector3> materialSeams,List<uint> indices,List<VirtualGeometryCluster> clusters,List<VirtualGeometryHierarchyNode> nodes,
            ref int nonProgressCount,ref int stalledChainCount,out int highestLevel){
            List<int> current=leaves;
            uint level=1;
            while(current.Count>1){
                if(level>64){throw new InvalidOperationException("Hierarchy did not converge within 64 levels.");}
                int groupCount=(current.Count+settings.HierarchyFanout-1)/settings.HierarchyFanout;
                List<int> next=new List<int>(groupCount);
                for(int start=0;start<current.Count;start+=settings.HierarchyFanout){
                    int childCount=Math.Min(settings.HierarchyFanout,current.Count-start);
                    for(int i=1;i<childCount;i++){
                        if(current[start+i]!=current[start]+i){throw new InvalidOperationException("Hierarchy level nodes are not contiguous.");}
                    }
                }
                // 同じ段のgroupは互いに独立なので簡略化を並列に行う。追加は元の順で行い、出力を逐次処理と同じにする。
                // 並列処理中は段の入力(nodes・clusters・indices)を読むだけで、書き込みは後の逐次処理だけが行う。
                var groups=new SimplifiedGroup[groupCount];
                List<int> levelNodes=current;
                RunParallel(groupCount,group=>{
                    groups[group]=SimplifyGroup(levelNodes,group*settings.HierarchyFanout,settings,vertices,meshBounds,indices,clusters,nodes);
                });
                for(int group=0;group<groupCount;group++){
                    SimplifiedGroup result=groups[group];
                    if(result.NoProgress){nonProgressCount++;}
                    int firstCluster=AppendClusters(result.Representation,material,result.Error,settings.MaxTrianglesPerCluster,vertices,indices,clusters);
                    EnclosingSphere(nodes,result.FirstChild,result.ChildCount,out Vector3 center,out float radius);
                    int nodeIndex=nodes.Count;
                    nodes.Add(new VirtualGeometryHierarchyNode{
                        BoundsCenter=center,BoundsRadius=radius,GeometricError=result.Error,
                        FirstCluster=(uint)firstCluster,ClusterCount=(uint)(clusters.Count-firstCluster),FirstChild=(uint)result.FirstChild,ChildCount=(uint)result.ChildCount,
                        PageId=0,MaterialSlot=(uint)material,Level=level,LeafStart=result.LeafStart,LeafCount=result.LeafCount
                    });
                    next.Add(nodeIndex);
                }
                current=next;
                level++;
            }
            int root=current[0];
            // 1 nodeへまとまった後も、rootが1 cluster以下になるまで単一子の段を重ねる(Naniteの最粗cluster相当)。
            // この段には隣接groupがないため、group境界もmeshの開いた縁も固定しない。
            // 別materialと共有する位置だけは固定し、material間の継ぎ目にcrackを作らない。
            while(RepresentationTriangles(nodes[root],clusters)>settings.MaxTrianglesPerCluster){
                if(level>64){throw new InvalidOperationException("Hierarchy did not converge within 64 levels.");}
                buildCancellation.ThrowIfCancellationRequested();
                VirtualGeometryHierarchyNode child=nodes[root];
                uint[] representation=Representation(child,clusters,indices);
                int sourceTriangles=representation.Length/3;
                int targetTriangles=Math.Max(1,Mathf.FloorToInt(sourceTriangles*settings.SimplificationRatio));
                if(targetTriangles>=sourceTriangles){targetTriangles=sourceTriangles-1;}
                HashSet<uint> locks=new HashSet<uint>();
                foreach(uint index in representation){if(materialSeams.Contains(vertices[index].Position)){locks.Add(index);}}
                MeshOptimizerSimplifyResult simplified=SimplifyWithPruning(vertices,representation,locks,targetTriangles);
                if(simplified.Indices.Length==0||simplified.Indices.Length>=representation.Length){stalledChainCount++;break;}
                representation=SortByMorton(simplified.Indices,vertices,meshBounds);
                float parentError=child.GeometricError+simplified.Error;
                int firstCluster=AppendClusters(representation,material,parentError,settings.MaxTrianglesPerCluster,vertices,indices,clusters);
                int nodeIndex=nodes.Count;
                nodes.Add(new VirtualGeometryHierarchyNode{
                    BoundsCenter=child.BoundsCenter,BoundsRadius=child.BoundsRadius,GeometricError=parentError,
                    FirstCluster=(uint)firstCluster,ClusterCount=(uint)(clusters.Count-firstCluster),FirstChild=(uint)root,ChildCount=1,
                    PageId=0,MaterialSlot=(uint)material,Level=level,LeafStart=child.LeafStart,LeafCount=child.LeafCount
                });
                root=nodeIndex;
                level++;
            }
            highestLevel=(int)level-1;
            return (uint)root;
        }

        private readonly struct SimplifiedGroup{
            public readonly uint[] Representation;public readonly float Error;public readonly bool NoProgress;
            public readonly int FirstChild;public readonly int ChildCount;public readonly uint LeafStart;public readonly uint LeafCount;
            public SimplifiedGroup(uint[] representation,float error,bool noProgress,int firstChild,int childCount,uint leafStart,uint leafCount){
                this.Representation=representation;this.Error=error;this.NoProgress=noProgress;
                this.FirstChild=firstChild;this.ChildCount=childCount;this.LeafStart=leafStart;this.LeafCount=leafCount;
            }
        }

        // fanout個の子nodeの表現をまとめ、group外周を固定して簡略化する。段の入力を読むだけなので、同じ段のgroupを並列に呼べる。
        private static SimplifiedGroup SimplifyGroup(List<int> level,int start,VirtualGeometryBuildSettings settings,VirtualGeometryPackedVertex[] vertices,
            Bounds meshBounds,List<uint> indices,List<VirtualGeometryCluster> clusters,List<VirtualGeometryHierarchyNode> nodes){
            int childCount=Math.Min(settings.HierarchyFanout,level.Count-start);
            int firstChild=level[start];
            List<uint> combined=new List<uint>();
            float childError=0;
            uint leafStart=nodes[firstChild].LeafStart;
            uint leafCount=0;
            for(int i=0;i<childCount;i++){
                VirtualGeometryHierarchyNode child=nodes[firstChild+i];
                childError=Mathf.Max(childError,child.GeometricError);
                leafCount=checked(leafCount+child.LeafCount);
                for(uint clusterOffset=0;clusterOffset<child.ClusterCount;clusterOffset++){
                    VirtualGeometryCluster cluster=clusters[checked((int)(child.FirstCluster+clusterOffset))];
                    for(uint indexOffset=0;indexOffset<cluster.IndexCount;indexOffset++){combined.Add(indices[checked((int)(cluster.FirstIndex+indexOffset))]);}
                }
            }
            uint[] representation=combined.ToArray();
            float parentError=childError;
            bool noProgress=false;
            if(childCount>1&&representation.Length>3){
                HashSet<uint> locks=CreateBoundaryLocks(vertices,representation);
                int sourceTriangles=representation.Length/3;
                int targetTriangles=Math.Max(1,Mathf.FloorToInt(sourceTriangles*settings.SimplificationRatio));
                if(targetTriangles>=sourceTriangles){targetTriangles=sourceTriangles-1;}
                MeshOptimizerSimplifyResult simplified=SimplifyWithPruning(vertices,representation,locks,targetTriangles);
                if(simplified.Indices.Length<representation.Length){
                    representation=SortByMorton(simplified.Indices,vertices,meshBounds);
                    parentError=childError+simplified.Error;
                }else{
                    noProgress=true;
                }
            }
            return new SimplifiedGroup(representation,parentError,noProgress,firstChild,childCount,leafStart,leafCount);
        }

        // 呼び出し元threadのcancelを各workerへ伝え、worker内の例外は元の型のまま投げ直す。
        private static void RunParallel(int count,Action<int> body){
            CancellationToken token=buildCancellation;
            try{
                System.Threading.Tasks.Parallel.For(0,count,new System.Threading.Tasks.ParallelOptions{CancellationToken=token},index=>{token.ThrowIfCancellationRequested();body(index);});
            }catch(AggregateException error){
                if(error.InnerExceptions.Count==1){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerExceptions[0]).Throw();}
                throw;
            }
        }

        private static int RepresentationTriangles(VirtualGeometryHierarchyNode node,List<VirtualGeometryCluster> clusters){
            uint total=0;
            for(uint clusterOffset=0;clusterOffset<node.ClusterCount;clusterOffset++){total+=clusters[checked((int)(node.FirstCluster+clusterOffset))].IndexCount;}
            return (int)(total/3);
        }

        private static uint[] Representation(VirtualGeometryHierarchyNode node,List<VirtualGeometryCluster> clusters,List<uint> indices){
            List<uint> result=new List<uint>();
            for(uint clusterOffset=0;clusterOffset<node.ClusterCount;clusterOffset++){
                VirtualGeometryCluster cluster=clusters[checked((int)(node.FirstCluster+clusterOffset))];
                for(uint indexOffset=0;indexOffset<cluster.IndexCount;indexOffset++){result.Add(indices[checked((int)(cluster.FirstIndex+indexOffset))]);}
            }
            return result.ToArray();
        }

        private static HashSet<Vector3> CollectMaterialSeams(List<Triangle> triangles,Vector3[] positions){
            Dictionary<Vector3,int> owner=new Dictionary<Vector3,int>();
            HashSet<Vector3> seams=new HashSet<Vector3>();
            foreach(Triangle triangle in triangles){
                foreach(int vertex in new[]{triangle.A,triangle.B,triangle.C}){
                    Vector3 position=positions[vertex];
                    if(owner.TryGetValue(position,out int material)){
                        if(material!=triangle.Material){seams.Add(position);}
                    }else{
                        owner.Add(position,triangle.Material);
                    }
                }
            }
            return seams;
        }

        private static int AppendClusters(uint[] source,int material,float error,int maxTriangles,VirtualGeometryPackedVertex[] vertices,
            List<uint> outputIndices,List<VirtualGeometryCluster> outputClusters){
            if(source.Length==0||source.Length%3!=0){throw new InvalidOperationException("Cluster source must contain complete triangles.");}
            int firstCluster=outputClusters.Count;
            int maxIndices=maxTriangles*3;
            for(int start=0;start<source.Length;start+=maxIndices){
                int count=Math.Min(maxIndices,source.Length-start);
                uint firstIndex=(uint)outputIndices.Count;
                Vector3 min=new Vector3(float.PositiveInfinity,float.PositiveInfinity,float.PositiveInfinity);
                Vector3 max=new Vector3(float.NegativeInfinity,float.NegativeInfinity,float.NegativeInfinity);
                for(int i=0;i<count;i++){
                    uint vertexIndex=source[start+i];
                    if(vertexIndex>=vertices.Length){throw new InvalidOperationException("Simplified cluster contains an out-of-range vertex.");}
                    outputIndices.Add(vertexIndex);
                    Encapsulate(ref min,ref max,vertices[vertexIndex].Position);
                }
                Vector3 center=(min+max)*0.5f;
                float radius=0;
                for(int i=0;i<count;i++){radius=Mathf.Max(radius,Vector3.Distance(center,vertices[source[start+i]].Position));}
                outputClusters.Add(new VirtualGeometryCluster{
                    BoundsCenter=center,BoundsRadius=radius,FirstIndex=firstIndex,IndexCount=(uint)count,MaterialSlot=(uint)material,PageId=0,
                    GeometricError=error,Parent=-1,ChildStart=0,ChildCount=0
                });
            }
            return firstCluster;
        }

        private readonly struct MeshComponent{
            public readonly int Triangles;public readonly float Radius;public readonly bool Removable;public readonly int Order;
            public MeshComponent(int triangles,float radius,bool removable,int order){this.Triangles=triangles;this.Radius=radius;this.Removable=removable;this.Order=order;}
        }

        // 小さな閉じた部品が集まったmesh(ボルトや板の集合)は、形を保つ縮約では部品を消せず、部品数より三角形を減らせない。
        // 目標数に届かないときは、固定頂点を持たない部品(groupの内側で完結する部品)を小さい順に取り除き、その大きさ(半径)を誤差に含める。
        // 縮約と除去が同じ誤差の範囲で釣り合うよう、除去だけで目標に届く誤差を縮約の上限にする。
        // meshoptimizerのPruneは固定頂点を守らず、group境界にcrackを作るため使わない。
        private static MeshOptimizerSimplifyResult SimplifyWithPruning(VirtualGeometryPackedVertex[] vertices,uint[] representation,HashSet<uint> locks,int targetTriangles){
            float limit=PruneLimit(Components(vertices,representation,locks,out _),representation.Length/3,targetTriangles);
            MeshOptimizerSimplifyResult simplified=MeshOptimizerSimplifier.SimplifyLockedVertices(vertices,representation,locks,targetTriangles*3,limit);
            if(simplified.Indices.Length<=targetTriangles*3){return simplified;}
            List<MeshComponent> components=Components(vertices,simplified.Indices,locks,out int[] triangleComponents);
            bool[] removed=new bool[components.Count];
            int remaining=simplified.Indices.Length/3;float error=simplified.Error;
            foreach(MeshComponent component in RemovalOrder(components)){
                if(remaining<=targetTriangles){break;}
                // 全部を取り除くと表現が空になるため、最後の部品は残す。
                if(remaining-component.Triangles<=0){continue;}
                removed[component.Order]=true;remaining-=component.Triangles;error=Mathf.Max(error,component.Radius);
            }
            if(remaining==simplified.Indices.Length/3){return simplified;}
            uint[] kept=new uint[remaining*3];int write=0;
            for(int triangle=0;triangle<triangleComponents.Length;triangle++){
                if(removed[triangleComponents[triangle]]){continue;}
                kept[write++]=simplified.Indices[triangle*3];kept[write++]=simplified.Indices[triangle*3+1];kept[write++]=simplified.Indices[triangle*3+2];
            }
            return new MeshOptimizerSimplifyResult(kept,error,simplified.ScratchVertexCount);
        }

        // 除去だけで目標数に届くときの誤差。届かなければ上限なし(縮約を従来どおり目標数まで進める)。
        private static float PruneLimit(List<MeshComponent> components,int triangles,int targetTriangles){
            int remaining=triangles;
            foreach(MeshComponent component in RemovalOrder(components)){
                if(remaining<=targetTriangles){break;}
                if(remaining-component.Triangles<=0){continue;}
                remaining-=component.Triangles;
                if(remaining<=targetTriangles){return component.Radius;}
            }
            return float.MaxValue;
        }

        // 取り除ける部品を小さい順に並べる。同じ大きさは最初に現れた順にして、出力を決定的にする。
        private static IEnumerable<MeshComponent> RemovalOrder(List<MeshComponent> components){
            return components.Where(component=>component.Removable).OrderBy(component=>component.Radius).ThenBy(component=>component.Order);
        }

        // 位置でつながった三角形の集まり(部品)ごとの三角形数・半径(包む箱の対角の半分)・固定頂点の有無。部品番号は最初に現れた順。
        private static List<MeshComponent> Components(VirtualGeometryPackedVertex[] vertices,uint[] indices,HashSet<uint> locks,out int[] triangleComponents){
            Dictionary<Vector3,int> positionIds=new Dictionary<Vector3,int>();
            int[] ids=new int[indices.Length];
            for(int i=0;i<indices.Length;i++){
                Vector3 position=vertices[indices[i]].Position;
                if(!positionIds.TryGetValue(position,out int id)){id=positionIds.Count;positionIds.Add(position,id);}
                ids[i]=id;
            }
            int[] parent=new int[positionIds.Count];
            for(int i=0;i<parent.Length;i++){parent[i]=i;}
            for(int i=0;i<indices.Length;i+=3){Union(parent,ids[i],ids[i+1]);Union(parent,ids[i],ids[i+2]);}
            Dictionary<int,int> componentOfRoot=new Dictionary<int,int>();
            List<int> counts=new List<int>();List<Vector3> minimums=new List<Vector3>();List<Vector3> maximums=new List<Vector3>();List<bool> removable=new List<bool>();
            triangleComponents=new int[indices.Length/3];
            for(int triangle=0;triangle<triangleComponents.Length;triangle++){
                int root=Find(parent,ids[triangle*3]);
                if(!componentOfRoot.TryGetValue(root,out int component)){
                    component=counts.Count;componentOfRoot.Add(root,component);
                    Vector3 first=vertices[indices[triangle*3]].Position;
                    counts.Add(0);minimums.Add(first);maximums.Add(first);removable.Add(true);
                }
                triangleComponents[triangle]=component;counts[component]++;
                for(int corner=0;corner<3;corner++){
                    uint index=indices[triangle*3+corner];Vector3 position=vertices[index].Position;
                    minimums[component]=Vector3.Min(minimums[component],position);maximums[component]=Vector3.Max(maximums[component],position);
                    if(locks.Contains(index)){removable[component]=false;}
                }
            }
            List<MeshComponent> result=new List<MeshComponent>(counts.Count);
            for(int component=0;component<counts.Count;component++){
                result.Add(new MeshComponent(counts[component],(maximums[component]-minimums[component]).magnitude*0.5f,removable[component],component));
            }
            return result;
        }

        private static int Find(int[] parent,int value){
            while(parent[value]!=value){parent[value]=parent[parent[value]];value=parent[value];}
            return value;
        }

        private static void Union(int[] parent,int a,int b){
            int rootA=Find(parent,a);int rootB=Find(parent,b);
            if(rootA<rootB){parent[rootB]=rootA;}else if(rootB<rootA){parent[rootA]=rootB;}
        }

        // groupの外周(隣のgroupと共有する縁)とmeshの開いた縁を固定する。辺は頂点番号ではなく位置で数える。
        // 法線・UVの継ぎ目では同じ位置の頂点が分かれるため、番号で数えると継ぎ目まで縁と判定して固定し、角ばったmeshがほとんど簡略化されない。
        // 位置で見て2枚の三角形が共有する辺は内側(継ぎ目を含む)で、継ぎ目の形はmeshoptimizerが保つ。1枚だけ、または3枚以上の辺を固定する。
        private static HashSet<uint> CreateBoundaryLocks(VirtualGeometryPackedVertex[] vertices,uint[] indices){
            Dictionary<Vector3,int> positionIds=new Dictionary<Vector3,int>();
            int[] ids=new int[indices.Length];
            for(int i=0;i<indices.Length;i++){
                Vector3 position=vertices[indices[i]].Position;
                if(!positionIds.TryGetValue(position,out int id)){id=positionIds.Count;positionIds.Add(position,id);}
                ids[i]=id;
            }
            Dictionary<EdgeKey,int> edgeCounts=new Dictionary<EdgeKey,int>();
            for(int i=0;i<indices.Length;i+=3){
                AddEdge(edgeCounts,new EdgeKey(ids[i],ids[i+1]));
                AddEdge(edgeCounts,new EdgeKey(ids[i+1],ids[i+2]));
                AddEdge(edgeCounts,new EdgeKey(ids[i+2],ids[i]));
            }
            HashSet<int> lockedIds=new HashSet<int>();
            foreach(KeyValuePair<EdgeKey,int> edge in edgeCounts){
                if(edge.Value!=2){lockedIds.Add(edge.Key.A);lockedIds.Add(edge.Key.B);}
            }
            HashSet<uint> locks=new HashSet<uint>();
            for(int i=0;i<indices.Length;i++){if(lockedIds.Contains(ids[i])){locks.Add(indices[i]);}}
            return locks;
        }

        private static uint[] SortByMorton(uint[] source,VirtualGeometryPackedVertex[] vertices,Bounds bounds){
            OrderedTriangle[] triangles=new OrderedTriangle[source.Length/3];
            for(int i=0;i<triangles.Length;i++){
                uint a=source[i*3];uint b=source[i*3+1];uint c=source[i*3+2];
                Vector3 centroid=(vertices[a].Position+vertices[b].Position+vertices[c].Position)/3f;
                triangles[i]=new OrderedTriangle(a,b,c,i,CalculateMorton(centroid,bounds));
            }
            Array.Sort(triangles,(left,right)=>{
                int comparison=left.Morton.CompareTo(right.Morton);
                return comparison!=0?comparison:left.Order.CompareTo(right.Order);
            });
            uint[] result=new uint[source.Length];
            for(int i=0;i<triangles.Length;i++){
                result[i*3]=triangles[i].A;result[i*3+1]=triangles[i].B;result[i*3+2]=triangles[i].C;
            }
            return result;
        }

        // 子の球をすべて包む球。立方体AABBの外接球を使うと段ごとに最大√3倍へ膨らみ、画面誤差を過大に見積もる。
        // 親の球が子の球を包む性質は、距離に対する画面誤差が親で子以上になるために維持する。
        private static void EnclosingSphere(List<VirtualGeometryHierarchyNode> nodes,int first,int count,out Vector3 center,out float radius){
            Vector3 min=new Vector3(float.PositiveInfinity,float.PositiveInfinity,float.PositiveInfinity);
            Vector3 max=new Vector3(float.NegativeInfinity,float.NegativeInfinity,float.NegativeInfinity);
            for(int i=0;i<count;i++){
                VirtualGeometryHierarchyNode node=nodes[first+i];
                Vector3 extent=Vector3.one*node.BoundsRadius;
                Encapsulate(ref min,ref max,node.BoundsCenter-extent);Encapsulate(ref min,ref max,node.BoundsCenter+extent);
            }
            center=(min+max)*0.5f;radius=0;
            for(int i=0;i<count;i++){
                VirtualGeometryHierarchyNode node=nodes[first+i];
                radius=Mathf.Max(radius,Vector3.Distance(center,node.BoundsCenter)+node.BoundsRadius);
            }
        }

        private static Vector3[] QuantizePositions(Vector3[] source,int bits,out float maximumError){
            Vector3[] result=(Vector3[])source.Clone();
            maximumError=0;
            if(bits==0){return result;}
            Bounds bounds=CalculateBounds(source);
            float levels=(1<<bits)-1;
            Vector3 min=bounds.min;Vector3 size=bounds.size;
            for(int i=0;i<result.Length;i++){
                Vector3 point=source[i];
                Vector3 quantized=new Vector3(
                    QuantizeAxis(point.x,min.x,size.x,levels),
                    QuantizeAxis(point.y,min.y,size.y,levels),
                    QuantizeAxis(point.z,min.z,size.z,levels));
                result[i]=quantized;
                maximumError=Mathf.Max(maximumError,Vector3.Distance(point,quantized));
            }
            return result;
        }

        private static float QuantizeAxis(float value,float min,float size,float levels){
            if(size<=1e-20f){return min;}
            float unit=Mathf.Clamp01((value-min)/size);
            return min+Mathf.Round(unit*levels)/levels*size;
        }

        private static void ValidateSettings(VirtualGeometryBuildSettings settings){
            if(settings.MaxTrianglesPerCluster<1||settings.MaxTrianglesPerCluster>128){throw new ArgumentOutOfRangeException(nameof(settings.MaxTrianglesPerCluster),"Cluster triangle limit must be in [1,128].");}
            if(settings.HierarchyFanout<2||settings.HierarchyFanout>8){throw new ArgumentOutOfRangeException(nameof(settings.HierarchyFanout),"Hierarchy fanout must be in [2,8].");}
            if(!float.IsFinite(settings.SimplificationRatio)||settings.SimplificationRatio<0.1f||settings.SimplificationRatio>0.9f){throw new ArgumentOutOfRangeException(nameof(settings.SimplificationRatio));}
            if(settings.PositionQuantizationBits!=0&&(settings.PositionQuantizationBits<8||settings.PositionQuantizationBits>24)){throw new ArgumentOutOfRangeException(nameof(settings.PositionQuantizationBits),"Position quantization must be disabled or use [8,24] bits.");}
        }

        private static void ValidateAttributes(VirtualGeometryMeshInput input){
            if(input.Positions.Length==0){throw new ArgumentException("Mesh has no vertices.",nameof(input));}
            if(input.SubmeshIndices.Length==0){throw new ArgumentException("Mesh has no submeshes.",nameof(input));}
            if(input.Normals.Length!=0&&input.Normals.Length!=input.Positions.Length){throw new ArgumentException("Normal count does not match positions.",nameof(input));}
            if(input.UV.Length!=0&&input.UV.Length!=input.Positions.Length){throw new ArgumentException("UV count does not match positions.",nameof(input));}
            for(int i=0;i<input.Positions.Length;i++){
                if(!IsFinite(input.Positions[i])){throw new ArgumentException("Vertex position "+i+" is not finite.",nameof(input));}
                if(input.Normals.Length!=0&&!IsFinite(input.Normals[i])){throw new ArgumentException("Vertex normal "+i+" is not finite.",nameof(input));}
                if(input.UV.Length!=0&&(!float.IsFinite(input.UV[i].x)||!float.IsFinite(input.UV[i].y))){throw new ArgumentException("UV "+i+" is not finite.",nameof(input));}
            }
        }

        private static List<Triangle> CollectTriangles(VirtualGeometryMeshInput input,Bounds bounds,List<VirtualGeometryBuildDiagnostic> diagnostics,out int inputTriangleCount){
            List<Triangle> result=new List<Triangle>();
            HashSet<TriangleKey> unique=new HashSet<TriangleKey>();
            int degenerateCount=0;int duplicateCount=0;inputTriangleCount=0;
            for(int material=0;material<input.SubmeshIndices.Length;material++){
                int[] source=input.SubmeshIndices[material]??throw new ArgumentException("Submesh index array is null.",nameof(input));
                if(source.Length%3!=0){throw new ArgumentException("Submesh index count is not divisible by three.",nameof(input));}
                for(int i=0;i<source.Length;i+=3){
                    if((i&1023)==0){buildCancellation.ThrowIfCancellationRequested();}
                    int a=source[i];int b=source[i+1];int c=source[i+2];inputTriangleCount++;
                    if((uint)a>=input.Positions.Length||(uint)b>=input.Positions.Length||(uint)c>=input.Positions.Length){throw new ArgumentException("Triangle contains an out-of-range index.",nameof(input));}
                    if(a==b||b==c||c==a||Vector3.Cross(input.Positions[b]-input.Positions[a],input.Positions[c]-input.Positions[a]).sqrMagnitude<=1e-20f){degenerateCount++;continue;}
                    TriangleKey key=new TriangleKey(a,b,c,material);
                    if(!unique.Add(key)){duplicateCount++;continue;}
                    Vector3 centroid=(input.Positions[a]+input.Positions[b]+input.Positions[c])/3f;
                    result.Add(new Triangle(a,b,c,material,inputTriangleCount-1,CalculateMorton(centroid,bounds)));
                }
            }
            if(degenerateCount>0){diagnostics.Add(new VirtualGeometryBuildDiagnostic("VG1003",VirtualGeometryDiagnosticSeverity.Warning,"Dropped "+degenerateCount+" degenerate triangles."));}
            if(duplicateCount>0){diagnostics.Add(new VirtualGeometryBuildDiagnostic("VG1004",VirtualGeometryDiagnosticSeverity.Warning,"Dropped "+duplicateCount+" duplicate triangles inside material boundaries."));}
            return result;
        }

        private static void DiagnoseTopology(List<Triangle> triangles,List<VirtualGeometryBuildDiagnostic> diagnostics){
            Dictionary<EdgeKey,int> edges=new Dictionary<EdgeKey,int>();
            foreach(Triangle triangle in triangles){
                AddEdge(edges,new EdgeKey(triangle.A,triangle.B));AddEdge(edges,new EdgeKey(triangle.B,triangle.C));AddEdge(edges,new EdgeKey(triangle.C,triangle.A));
            }
            int boundary=0;int nonManifold=0;
            foreach(int count in edges.Values){if(count==1){boundary++;}else if(count>2){nonManifold++;}}
            if(boundary>0){diagnostics.Add(new VirtualGeometryBuildDiagnostic("VG1005",VirtualGeometryDiagnosticSeverity.Info,"Preserved "+boundary+" open boundary edges."));}
            if(nonManifold>0){diagnostics.Add(new VirtualGeometryBuildDiagnostic("VG1006",VirtualGeometryDiagnosticSeverity.Warning,"Preserved "+nonManifold+" non-manifold edges; simplification does not cross them."));}
        }

        private static Vector3[] PrepareNormals(VirtualGeometryMeshInput input,List<Triangle> triangles){
            if(input.Normals.Length==input.Positions.Length){
                Vector3[] copy=(Vector3[])input.Normals.Clone();
                for(int i=0;i<copy.Length;i++){if(copy[i].sqrMagnitude>1e-20f){copy[i].Normalize();}else{copy[i]=Vector3.up;}}
                return copy;
            }
            Vector3[] normals=new Vector3[input.Positions.Length];
            foreach(Triangle triangle in triangles){
                Vector3 face=Vector3.Cross(input.Positions[triangle.B]-input.Positions[triangle.A],input.Positions[triangle.C]-input.Positions[triangle.A]);
                normals[triangle.A]+=face;normals[triangle.B]+=face;normals[triangle.C]+=face;
            }
            for(int i=0;i<normals.Length;i++){if(normals[i].sqrMagnitude>1e-20f){normals[i].Normalize();}else{normals[i]=Vector3.up;}}
            return normals;
        }

        private static Bounds CalculateBounds(Vector3[] positions){
            Vector3 min=positions[0];Vector3 max=positions[0];
            for(int i=1;i<positions.Length;i++){Encapsulate(ref min,ref max,positions[i]);}
            Bounds bounds=new Bounds();bounds.SetMinMax(min,max);return bounds;
        }

        private static uint CalculateMorton(Vector3 point,Bounds bounds){
            // 全軸を最大辺長で正規化する。軸ごとに正規化すると薄い軸(波状平面の高さなど)が他軸と同じ重みに伸び、
            // Morton順が高さ帯で分かれてclusterとgroupが空間的に散らばる。group外周の固定が増え、親の誤差が段で跳ねる。
            Vector3 size=bounds.size;float extent=Mathf.Max(size.x,Mathf.Max(size.y,size.z));
            float x=extent>1e-20f?(point.x-bounds.min.x)/extent:0.5f;
            float y=extent>1e-20f?(point.y-bounds.min.y)/extent:0.5f;
            float z=extent>1e-20f?(point.z-bounds.min.z)/extent:0.5f;
            uint ix=(uint)Mathf.RoundToInt(Mathf.Clamp01(x)*1023f);
            uint iy=(uint)Mathf.RoundToInt(Mathf.Clamp01(y)*1023f);
            uint iz=(uint)Mathf.RoundToInt(Mathf.Clamp01(z)*1023f);
            return ExpandBits(ix)|(ExpandBits(iy)<<1)|(ExpandBits(iz)<<2);
        }

        private static uint ExpandBits(uint value){
            value=(value|(value<<16))&0x030000FF;value=(value|(value<<8))&0x0300F00F;
            value=(value|(value<<4))&0x030C30C3;value=(value|(value<<2))&0x09249249;return value;
        }

        private static void Encapsulate(ref Vector3 min,ref Vector3 max,Vector3 point){min=Vector3.Min(min,point);max=Vector3.Max(max,point);}
        private static void AddEdge(Dictionary<EdgeKey,int> edges,EdgeKey edge){edges.TryGetValue(edge,out int count);edges[edge]=count+1;}
        private static bool IsFinite(Vector3 value){return float.IsFinite(value.x)&&float.IsFinite(value.y)&&float.IsFinite(value.z);}

        internal static string ComputeSha256(byte[] data){
            using(SHA256 sha=SHA256.Create()){return BitConverter.ToString(sha.ComputeHash(data)).Replace("-",string.Empty).ToLowerInvariant();}
        }
    }
}
