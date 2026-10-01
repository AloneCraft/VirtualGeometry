using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using VirtualizedGeometry.Editor;

namespace VirtualizedGeometry.Tests{
    public sealed class VirtualGeometryHierarchyTests{
        [Test]
        public void ScreenError_HandlesProjectionScaleAndCameraInsideBoundsConservatively(){
            VirtualGeometryHierarchyNode node=CreateNode(Vector3.zero,1,1,0,1,0,0,0,1,0);
            VirtualGeometryLodSelectionContext orthographic=Context(Matrix4x4.Scale(new Vector3(0.5f,2,1)),new Vector3(0,0,-10),true,90,5,1000,0.1f,1,0);
            VirtualGeometryLodSelectionContext perspective=Context(Matrix4x4.Scale(new Vector3(0.5f,2,1)),new Vector3(0,0,-10),false,90,5,1000,0.1f,1,0);
            VirtualGeometryLodSelectionContext inside=Context(Matrix4x4.Scale(new Vector3(0.5f,2,1)),Vector3.zero,false,90,5,1000,0.1f,1,0);

            Assert.That(VirtualGeometryLodSelector.CalculateScreenError(node,orthographic),Is.EqualTo(200).Within(0.001));
            Assert.That(VirtualGeometryLodSelector.CalculateScreenError(node,perspective),Is.EqualTo(125).Within(0.001));
            float insideError=VirtualGeometryLodSelector.CalculateScreenError(node,inside);
            Assert.That(float.IsFinite(insideError),Is.True);
            Assert.That(insideError,Is.EqualTo(10000).Within(0.1));
        }

        [Test]
        public void Selection_CoversLeavesOnceAndFallsBackAtomicallyWhenAnyChildPageIsMissing(){
            VirtualGeometryPage page=CreateAtomicPage(false);
            VirtualGeometryLodSelectionContext context=Context(Matrix4x4.identity,new Vector3(0,0,-5),false,60,5,1080,0.1f,0.1f,0);

            VirtualGeometryLodSelectionResult missing=VirtualGeometryLodSelector.Select(page,context,pageId=>pageId<=1,null);
            Assert.That(missing.SelectedNodes,Is.EqualTo(new[]{0}));
            AssertCoverage(page,missing.SelectedNodes,2);

            VirtualGeometryLodSelectionResult resident=VirtualGeometryLodSelector.Select(page,context,pageId=>pageId<=2,null);
            Assert.That(resident.SelectedNodes,Is.EqualTo(new[]{1,2}));
            AssertCoverage(page,resident.SelectedNodes,2);
        }

        [Test]
        public void Selection_RejectsHierarchyCycles(){
            VirtualGeometryPage cycle=CreateAtomicPage(true);
            VirtualGeometryLodSelectionContext context=Context(Matrix4x4.identity,new Vector3(0,0,-5),false,60,5,1080,0.1f,0.1f,0);
            Assert.Throws<InvalidDataException>(()=>VirtualGeometryLodSelector.Select(cycle,context,_=>true,null));
        }

        [Test]
        public void Build_CreatesFiniteHierarchyWithValidCoverageAndDeterministicBytes(){
            VirtualGeometryMeshInput input=CreateWaveGrid(18,12);
            VirtualGeometryBuildSettings settings=new VirtualGeometryBuildSettings{
                MaxTrianglesPerCluster=8,GenerateHierarchy=true,HierarchyFanout=4,SimplificationRatio=0.5f,PositionQuantizationBits=16
            };

            VirtualGeometryBuildOutput first=VirtualGeometryBuilder.Build(input,settings);
            VirtualGeometryBuildOutput second=VirtualGeometryBuilder.Build(input,settings);
            VirtualGeometryPage page=VirtualGeometryPageCodec.Read(first.PageBytes);
            int leafCount=0;
            foreach(VirtualGeometryHierarchyNode node in page.Nodes){if(node.ChildCount==0){leafCount++;}}

            Assert.That(first.PageBytes,Is.EqualTo(second.PageBytes));
            Assert.That(page.Nodes.Length,Is.GreaterThan(leafCount));
            Assert.That(page.Nodes.Length,Is.LessThanOrEqualTo(leafCount*2+1));
            Assert.That(page.RootNodeIndices.Length,Is.EqualTo(page.MaterialCount));
            Assert.That(page.RootNodeIndices[0],Is.Not.EqualTo(uint.MaxValue));
            ValidateTree(page,(int)page.RootNodeIndices[0],new HashSet<int>(),new HashSet<int>());
            Assert.That(page.Nodes[page.RootNodeIndices[0]].LeafCount,Is.EqualTo((uint)leafCount));
        }

        [Test]
        public void Build_ContinuesLevelsUntilRootFitsOneCluster(){
            // Practical sampleと同じ2,048 triangles・既定設定。開いた縁を持つ平面でもrootが1 cluster以下になるまで段を重ねる。
            VirtualGeometryBuildSettings settings=new VirtualGeometryBuildSettings{
                MaxTrianglesPerCluster=64,GenerateHierarchy=true,HierarchyFanout=4,SimplificationRatio=0.5f,PositionQuantizationBits=16
            };
            VirtualGeometryBuildOutput output=VirtualGeometryBuilder.Build(CreateWaveGrid(32,32),settings);
            VirtualGeometryPage page=VirtualGeometryPageCodec.Read(output.PageBytes);
            int root=(int)page.RootNodeIndices[0];
            Assert.That(Triangles(page,page.Nodes[root]),Is.LessThanOrEqualTo(settings.MaxTrianglesPerCluster));
            int chain=0;
            for(int i=0;i<page.Nodes.Length;i++){
                VirtualGeometryHierarchyNode node=page.Nodes[i];
                if(node.ChildCount==1){chain++;}
                for(uint c=0;c<node.ChildCount;c++){
                    VirtualGeometryHierarchyNode child=page.Nodes[node.FirstChild+c];
                    Assert.That(child.GeometricError,Is.LessThanOrEqualTo(node.GeometricError));
                    Assert.That(child.Level,Is.LessThan(node.Level));
                }
            }
            Assert.That(chain,Is.GreaterThan(0));
            Assert.That(page.Nodes[root].Level,Is.GreaterThan(3u));
            // CPU参照選択の構造検証(被覆・誤差単調・閉路なし)を通る。
            VirtualGeometryLodSelectionContext context=Context(Matrix4x4.identity,new Vector3(0,0,-1000),false,60,5,1080,0.1f,2,0);
            Assert.DoesNotThrow(()=>VirtualGeometryLodSelector.Select(page,context,_=>true,null));
            // Proxyは単一子の段を降りた、group束ねで1 nodeへまとまった段から作る。
            int proxyNode=root;
            while(page.Nodes[proxyNode].ChildCount==1){proxyNode=(int)page.Nodes[proxyNode].FirstChild;}
            Mesh proxy=VirtualGeometryProxyBuilder.Create(output,out int proxyTriangles,out float proxyError);
            try{
                Assert.That(proxyTriangles,Is.EqualTo(Triangles(page,page.Nodes[proxyNode])));
                Assert.That(proxyTriangles,Is.GreaterThan(Triangles(page,page.Nodes[root])));
                Assert.That(proxyError,Is.EqualTo(page.Nodes[proxyNode].GeometricError));
            }finally{UnityEngine.Object.DestroyImmediate(proxy);}
            {
                //小さな閉じた部品(面ごとに頂点が分かれた箱)が集まったmeshも、部品を小さい順に取り除いてrootが1 cluster以下になる
                //箱は形を保つ縮約では消せないため、取り除かないとrootが全三角形のまま残る。取り除いた部品の大きさは誤差に入る
                const float BoxSize=0.2f;
                VirtualGeometryBuildOutput boxes=VirtualGeometryBuilder.Build(CreateBoxes(18,18,BoxSize),settings);
                VirtualGeometryPage boxPage=VirtualGeometryPageCodec.Read(boxes.PageBytes);
                VirtualGeometryHierarchyNode boxRoot=boxPage.Nodes[boxPage.RootNodeIndices[0]];
                Assert.That(Triangles(boxPage,boxRoot),Is.LessThanOrEqualTo(settings.MaxTrianglesPerCluster));
                Assert.That(boxRoot.GeometricError,Is.GreaterThanOrEqualTo(BoxSize*0.5f));
                for(int i=0;i<boxPage.Nodes.Length;i++){
                    VirtualGeometryHierarchyNode node=boxPage.Nodes[i];
                    for(uint c=0;c<node.ChildCount;c++){Assert.That(boxPage.Nodes[node.FirstChild+c].GeometricError,Is.LessThanOrEqualTo(node.GeometricError));}
                }
                Assert.DoesNotThrow(()=>VirtualGeometryLodSelector.Select(boxPage,context,_=>true,null));
            }
        }

        [Test]
        public void Build_KeepsClustersCompactAndBoundsEnclosingOnThinSurfaces(){
            // Practical sampleと同じ8×8・高さ±0.4の薄い波状平面。薄い軸がMorton順を支配するとclusterが散らばり、親の誤差が段で跳ねる。
            VirtualGeometryBuildSettings settings=new VirtualGeometryBuildSettings{
                MaxTrianglesPerCluster=64,GenerateHierarchy=true,HierarchyFanout=4,SimplificationRatio=0.5f,PositionQuantizationBits=16
            };
            VirtualGeometryBuildOutput output=VirtualGeometryBuilder.Build(CreateThinWave(32),settings);
            VirtualGeometryPage page=VirtualGeometryPageCodec.Read(output.PageBytes);
            float leafRadius=0;
            var levelErrors=new SortedDictionary<uint,float>();
            for(int i=0;i<page.Nodes.Length;i++){
                VirtualGeometryHierarchyNode node=page.Nodes[i];
                if(node.ChildCount==0){leafRadius=Mathf.Max(leafRadius,node.BoundsRadius);}
                levelErrors.TryGetValue(node.Level,out float error);levelErrors[node.Level]=Mathf.Max(error,node.GeometricError);
                // 各nodeの球は自分の表現の頂点と子の球を包む。
                for(uint c=0;c<node.ClusterCount;c++){
                    VirtualGeometryCluster cluster=page.Clusters[node.FirstCluster+c];
                    for(uint k=0;k<cluster.IndexCount;k++){
                        Assert.That(Vector3.Distance(page.Vertices[page.Indices[cluster.FirstIndex+k]].Position,node.BoundsCenter),Is.LessThanOrEqualTo(node.BoundsRadius+1e-4f));
                    }
                }
                for(uint c=0;c<node.ChildCount;c++){
                    VirtualGeometryHierarchyNode child=page.Nodes[node.FirstChild+c];
                    Assert.That(Vector3.Distance(child.BoundsCenter,node.BoundsCenter)+child.BoundsRadius,Is.LessThanOrEqualTo(node.BoundsRadius+1e-4f));
                }
            }
            // 64 trianglesは面積約2なので、まとまったclusterの半径は1前後になる。
            Assert.That(leafRadius,Is.LessThan(2f));
            // 子の球を包む分だけ保守的だが、段ごとの√3倍の累積はない。
            Assert.That(page.Nodes[page.RootNodeIndices[0]].BoundsRadius,Is.LessThanOrEqualTo(output.Bounds.extents.magnitude*1.25f));
            // L1以上では、1段ごとの誤差の増え方が数倍以内に収まる(L0は量子化誤差だけなので除く)。
            float previous=0;
            foreach(var pair in levelErrors){
                if(pair.Key>=2){Assert.That(pair.Value/previous,Is.LessThan(8f),"Level "+pair.Key);}
                previous=pair.Value;
            }
        }

        [Test]
        public void Build_QuantizesPositionsOnceAndKeepsSharedVertexIdsAcrossClusters(){
            VirtualGeometryMeshInput input=CreateWaveGrid(5,3);
            VirtualGeometryBuildOutput output=VirtualGeometryBuilder.Build(input,new VirtualGeometryBuildSettings{
                MaxTrianglesPerCluster=1,GenerateHierarchy=false,PositionQuantizationBits=8
            });
            Vector3 min=output.Bounds.min;
            Vector3 size=output.Bounds.size;
            foreach(VirtualGeometryPackedVertex vertex in output.Vertices){
                AssertQuantized(vertex.Position.x,min.x,size.x,255);
                AssertQuantized(vertex.Position.y,min.y,size.y,255);
                AssertQuantized(vertex.Position.z,min.z,size.z,255);
            }
            int sharedVertex=2*6+3;
            int containingClusters=0;
            foreach(VirtualGeometryCluster cluster in output.Clusters){
                bool found=false;
                for(uint i=0;i<cluster.IndexCount;i++){
                    if(output.Indices[cluster.FirstIndex+i]==sharedVertex){found=true;break;}
                }
                if(found){containingClusters++;}
            }
            Assert.That(containingClusters,Is.GreaterThan(1));
            Assert.That(output.Vertices.Length,Is.EqualTo(input.Positions.Length));
        }

        private static void AssertCoverage(VirtualGeometryPage page,int[] selectedNodes,int leafCount){
            int[] coverage=new int[leafCount];
            foreach(int nodeIndex in selectedNodes){
                VirtualGeometryHierarchyNode node=page.Nodes[nodeIndex];
                for(uint leaf=node.LeafStart;leaf<node.LeafStart+node.LeafCount;leaf++){coverage[leaf]++;}
            }
            Assert.That(coverage,Is.All.EqualTo(1));
        }

        private static void AssertQuantized(float value,float min,float size,int levels){
            if(size<=1e-20f){Assert.That(value,Is.EqualTo(min));return;}
            float grid=(value-min)/size*levels;
            Assert.That(grid,Is.EqualTo(Mathf.Round(grid)).Within(0.001f));
        }

        private static VirtualGeometryMeshInput CreateThinWave(int size){
            Vector3[] vertices=new Vector3[(size+1)*(size+1)];Vector2[] uv=new Vector2[vertices.Length];
            for(int y=0;y<=size;y++){
                for(int x=0;x<=size;x++){
                    float px=(float)x/size*8-4;float py=(float)y/size*8-4;int i=y*(size+1)+x;
                    vertices[i]=new Vector3(px,py,Mathf.Sin(px*1.5f)*Mathf.Cos(py)*0.4f);uv[i]=new Vector2((float)x/size,(float)y/size);
                }
            }
            int[] triangles=new int[size*size*6];int offset=0;
            for(int y=0;y<size;y++){
                for(int x=0;x<size;x++){
                    int a=y*(size+1)+x;
                    triangles[offset++]=a;triangles[offset++]=a+size+1;triangles[offset++]=a+1;
                    triangles[offset++]=a+1;triangles[offset++]=a+size+1;triangles[offset++]=a+size+2;
                }
            }
            return new VirtualGeometryMeshInput(vertices,Array.Empty<Vector3>(),uv,new[]{triangles});
        }

        private static int Triangles(VirtualGeometryPage page,VirtualGeometryHierarchyNode node){
            int indices=0;
            for(uint c=0;c<node.ClusterCount;c++){indices+=(int)page.Clusters[node.FirstCluster+c].IndexCount;}
            return indices/3;
        }

        private static void ValidateTree(VirtualGeometryPage page,int nodeIndex,HashSet<int> visiting,HashSet<int> visited){
            Assert.That(visiting.Add(nodeIndex),Is.True,"Hierarchy contains a cycle.");
            VirtualGeometryHierarchyNode node=page.Nodes[nodeIndex];
            for(uint child=0;child<node.ChildCount;child++){ValidateTree(page,(int)(node.FirstChild+child),visiting,visited);}
            visiting.Remove(nodeIndex);
            Assert.That(visited.Add(nodeIndex),Is.True,"Hierarchy node is referenced more than once.");
        }

        private static VirtualGeometryPage CreateAtomicPage(bool cycle){
            VirtualGeometryPackedVertex[] vertices={
                new VirtualGeometryPackedVertex(Vector3.zero,Vector3.forward,Vector2.zero),
                new VirtualGeometryPackedVertex(Vector3.right,Vector3.forward,Vector2.right),
                new VirtualGeometryPackedVertex(Vector3.up,Vector3.forward,Vector2.up)
            };
            uint[] indices={0,1,2};
            VirtualGeometryCluster[] clusters={new VirtualGeometryCluster{BoundsCenter=Vector3.zero,BoundsRadius=1,FirstIndex=0,IndexCount=3,MaterialSlot=0,PageId=0}};
            VirtualGeometryHierarchyNode root=CreateNode(Vector3.zero,1,10,0,1,cycle?0u:1u,cycle?1u:2u,0,2,1);
            VirtualGeometryHierarchyNode[] nodes=cycle?new[]{root}:new[]{root,CreateNode(Vector3.left,0.5f,0,0,1,0,0,0,1,1),CreateNode(Vector3.right,0.5f,0,0,1,0,0,1,1,2)};
            return new VirtualGeometryPage(vertices,indices,clusters,nodes,new[]{0u},1,new Bounds(Vector3.zero,Vector3.one*2));
        }

        private static VirtualGeometryHierarchyNode CreateNode(Vector3 center,float radius,float error,uint firstCluster,uint clusterCount,uint firstChild,uint childCount,uint leafStart,uint leafCount,uint pageId){
            return new VirtualGeometryHierarchyNode{
                BoundsCenter=center,BoundsRadius=radius,GeometricError=error,FirstCluster=firstCluster,ClusterCount=clusterCount,
                FirstChild=firstChild,ChildCount=childCount,PageId=pageId,MaterialSlot=0,Level=childCount==0?0u:1u,LeafStart=leafStart,LeafCount=leafCount
            };
        }

        private static VirtualGeometryLodSelectionContext Context(Matrix4x4 objectToWorld,Vector3 cameraPosition,bool orthographic,float fov,float orthoSize,int height,float near,float target,float hysteresis){
            return new VirtualGeometryLodSelectionContext(objectToWorld,cameraPosition,orthographic,fov,orthoSize,height,near,target,hysteresis);
        }

        private static VirtualGeometryMeshInput CreateWaveGrid(int width,int height){
            Vector3[] vertices=new Vector3[(width+1)*(height+1)];
            Vector3[] normals=new Vector3[vertices.Length];
            Vector2[] uv=new Vector2[vertices.Length];
            for(int y=0;y<=height;y++){
                for(int x=0;x<=width;x++){
                    int i=y*(width+1)+x;
                    float px=x*0.73f-2.13f;
                    float py=y*0.61f+0.17f;
                    vertices[i]=new Vector3(px,py,Mathf.Sin(px*1.31f)*Mathf.Cos(py*0.83f)*0.47f+0.031f*x*y);
                    normals[i]=Vector3.forward;
                    uv[i]=new Vector2(x/(float)width,y/(float)height);
                }
            }
            int[] triangles=new int[width*height*6];
            int offset=0;
            for(int y=0;y<height;y++){
                for(int x=0;x<width;x++){
                    int a=y*(width+1)+x;int b=a+1;int c=a+width+1;int d=c+1;
                    triangles[offset++]=a;triangles[offset++]=c;triangles[offset++]=b;
                    triangles[offset++]=b;triangles[offset++]=c;triangles[offset++]=d;
                }
            }
            return new VirtualGeometryMeshInput(vertices,normals,uv,new[]{triangles});
        }

        // 1辺sizeの箱をcolumns×rows個、間隔1で並べる。面ごとに頂点を分け(角ばった形の法線)、箱どうしはつながらない。
        private static VirtualGeometryMeshInput CreateBoxes(int columns,int rows,float size){
            Vector3[] axes={Vector3.right,Vector3.left,Vector3.up,Vector3.down,Vector3.forward,Vector3.back};
            var vertices=new List<Vector3>();var normals=new List<Vector3>();var uv=new List<Vector2>();var triangles=new List<int>();
            float half=size*0.5f;
            for(int y=0;y<rows;y++){
                for(int x=0;x<columns;x++){
                    Vector3 center=new Vector3(x,y,0);
                    foreach(Vector3 normal in axes){
                        Vector3 reference=Vector3.up;
                        if(Mathf.Abs(normal.y)>0.5f){reference=Vector3.forward;}
                        Vector3 u=Vector3.Cross(normal,reference).normalized*half;Vector3 v=Vector3.Cross(normal,u).normalized*half;
                        Vector3 face=center+normal*half;int start=vertices.Count;
                        vertices.Add(face-u-v);vertices.Add(face+u-v);vertices.Add(face+u+v);vertices.Add(face-u+v);
                        for(int corner=0;corner<4;corner++){normals.Add(normal);}
                        uv.Add(new Vector2(0,0));uv.Add(new Vector2(1,0));uv.Add(new Vector2(1,1));uv.Add(new Vector2(0,1));
                        triangles.Add(start);triangles.Add(start+1);triangles.Add(start+2);triangles.Add(start);triangles.Add(start+2);triangles.Add(start+3);
                    }
                }
            }
            return new VirtualGeometryMeshInput(vertices.ToArray(),normals.ToArray(),uv.ToArray(),new[]{triangles.ToArray()});
        }
    }
}
