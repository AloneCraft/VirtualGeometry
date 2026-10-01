using System;
using System.Collections.Generic;
using System.Globalization;
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
            if(!mesh.isReadable){throw new ArgumentException("Mesh read/write must be enabled for conversion.",nameof(mesh));}
            int[][] submeshes=new int[mesh.subMeshCount][];
            for(int i=0;i<submeshes.Length;i++){
                if(mesh.GetTopology(i)!=MeshTopology.Triangles){throw new ArgumentException("Only triangle submeshes can be converted.",nameof(mesh));}
                submeshes[i]=mesh.GetIndices(i,true);
            }
            return new VirtualGeometryMeshInput(mesh.vertices,mesh.normals,mesh.uv,submeshes);
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
        public string ContentHash{get;}
        public byte[] PageBytes{get;}

        public VirtualGeometryBuildOutput(VirtualGeometryPage page,VirtualGeometryBuildDiagnostic[] diagnostics,int inputTriangleCount,int leafTriangleCount,byte[] pageBytes,string contentHash){
            this.Vertices=page.Vertices;this.Indices=page.Indices;this.Clusters=page.Clusters;this.Nodes=page.Nodes;
            this.RootNodeIndices=page.RootNodeIndices;this.Diagnostics=diagnostics;this.Bounds=page.Bounds;
            this.InputTriangleCount=inputTriangleCount;this.LeafTriangleCount=leafTriangleCount;this.PageBytes=pageBytes;this.ContentHash=contentHash;
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
            byte[] pageBytes=VirtualGeometryPageCodec.Write(page);
            string hash=ComputeSha256(pageBytes);
            return new VirtualGeometryBuildOutput(page,diagnostics.ToArray(),inputTriangleCount,triangles.Count,pageBytes,hash);
        }

        private static uint BuildHierarchyForMaterial(int material,List<int> leaves,VirtualGeometryBuildSettings settings,VirtualGeometryPackedVertex[] vertices,
            Bounds meshBounds,HashSet<Vector3> materialSeams,List<uint> indices,List<VirtualGeometryCluster> clusters,List<VirtualGeometryHierarchyNode> nodes,
            ref int nonProgressCount,ref int stalledChainCount,out int highestLevel){
            List<int> current=leaves;
            uint level=1;
            while(current.Count>1){
                if(level>64){throw new InvalidOperationException("Hierarchy did not converge within 64 levels.");}
                List<int> next=new List<int>((current.Count+settings.HierarchyFanout-1)/settings.HierarchyFanout);
                for(int start=0;start<current.Count;start+=settings.HierarchyFanout){
                    buildCancellation.ThrowIfCancellationRequested();
                    int childCount=Math.Min(settings.HierarchyFanout,current.Count-start);
                    int firstChild=current[start];
                    for(int i=1;i<childCount;i++){
                        if(current[start+i]!=firstChild+i){throw new InvalidOperationException("Hierarchy level nodes are not contiguous.");}
                    }
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
                    if(childCount>1&&representation.Length>3){
                        HashSet<uint> locks=CreateBoundaryLocks(vertices,representation);
                        int sourceTriangles=representation.Length/3;
                        int targetTriangles=Math.Max(1,Mathf.FloorToInt(sourceTriangles*settings.SimplificationRatio));
                        if(targetTriangles>=sourceTriangles){targetTriangles=sourceTriangles-1;}
                        MeshOptimizerSimplifyResult simplified=MeshOptimizerSimplifier.SimplifyLockedVertices(vertices,representation,locks,targetTriangles*3);
                        if(simplified.Indices.Length<representation.Length){
                            representation=SortByMorton(simplified.Indices,vertices,meshBounds);
                            parentError=childError+simplified.Error;
                        }else{
                            nonProgressCount++;
                        }
                    }
                    int firstCluster=AppendClusters(representation,material,parentError,settings.MaxTrianglesPerCluster,vertices,indices,clusters);
                    EnclosingSphere(nodes,firstChild,childCount,out Vector3 center,out float radius);
                    int nodeIndex=nodes.Count;
                    nodes.Add(new VirtualGeometryHierarchyNode{
                        BoundsCenter=center,BoundsRadius=radius,GeometricError=parentError,
                        FirstCluster=(uint)firstCluster,ClusterCount=(uint)(clusters.Count-firstCluster),FirstChild=(uint)firstChild,ChildCount=(uint)childCount,
                        PageId=0,MaterialSlot=(uint)material,Level=level,LeafStart=leafStart,LeafCount=leafCount
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
                MeshOptimizerSimplifyResult simplified=MeshOptimizerSimplifier.SimplifyLockedVertices(vertices,representation,locks,targetTriangles*3);
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

        private static HashSet<uint> CreateBoundaryLocks(VirtualGeometryPackedVertex[] vertices,uint[] indices){
            Dictionary<EdgeKey,int> edgeCounts=new Dictionary<EdgeKey,int>();
            for(int i=0;i<indices.Length;i+=3){
                AddEdge(edgeCounts,new EdgeKey((int)indices[i],(int)indices[i+1]));
                AddEdge(edgeCounts,new EdgeKey((int)indices[i+1],(int)indices[i+2]));
                AddEdge(edgeCounts,new EdgeKey((int)indices[i+2],(int)indices[i]));
            }
            HashSet<uint> locks=new HashSet<uint>();
            foreach(KeyValuePair<EdgeKey,int> edge in edgeCounts){
                if(edge.Value!=2){locks.Add((uint)edge.Key.A);locks.Add((uint)edge.Key.B);}
            }
            HashSet<Vector3> lockedPositions=new HashSet<Vector3>();
            foreach(uint index in locks){lockedPositions.Add(vertices[index].Position);}
            foreach(uint index in indices){if(lockedPositions.Contains(vertices[index].Position)){locks.Add(index);}}
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

        private static string ComputeSha256(byte[] data){
            using(SHA256 sha=SHA256.Create()){return BitConverter.ToString(sha.ComputeHash(data)).Replace("-",string.Empty).ToLowerInvariant();}
        }
    }
}
