using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Debug=UnityEngine.Debug;

namespace VirtualizedGeometry.Editor{
    /// <summary>
    /// 規模ごとの元モデル(OBJ)を生成し、Virtual Geometryを有効にしたインポートの変換時間と実process memoryを記録する。
    /// 変換はインポート中に同期で走るため、memoryは別threadで標本化する。生成物は大きいためgitへは入れない。
    /// </summary>
    public static class M5BenchmarkPreparation{
        public static void Run(){
            int grid=724;string[] args=Environment.GetCommandLineArgs();
            for(int i=0;i<args.Length-1;i++){if(args[i]=="-vgGrid"){grid=int.Parse(args[i+1]);}}
            string root="Assets/VirtualizedGeometrySample/Benchmark"+grid;string output="TestResults/M5/Benchmark"+grid;string model=root+"/Source.obj";
            var samples=new List<string>{"seconds,working_set_bytes"};long peakBytes=0;
            Directory.CreateDirectory(root);Directory.CreateDirectory(output);
            try{
                var watch=Stopwatch.StartNew();
                Mesh mesh=CreateGrid(grid);
                VirtualGeometryObjWriter.Write(mesh,model);UnityEngine.Object.DestroyImmediate(mesh);
                AssetDatabase.ImportAsset(model,ImportAssetOptions.ForceSynchronousImport);
                File.WriteAllText(output+"/PreparationEnvironment.txt","Unity="+Application.unityVersion+"\nGPU="+SystemInfo.graphicsDeviceName+"\nSystemMiB="+SystemInfo.systemMemorySize+"\nGrid="+grid+"\nTriangles="+(2L*grid*grid)+"\nSourcePreparationSeconds="+watch.Elapsed.TotalSeconds.ToString("R")+"\n");
                watch.Restart();
                bool sampling=true;
                var sampler=new Thread(()=>{
                    while(Volatile.Read(ref sampling)){
                        long bytes=VirtualGeometryProcessMemory.WorkingSetBytes;
                        lock(samples){peakBytes=Math.Max(peakBytes,bytes);samples.Add(watch.Elapsed.TotalSeconds.ToString("R")+","+bytes);}
                        Thread.Sleep(250);
                    }
                }){IsBackground=true};
                sampler.Start();
                VirtualGeometryMeshAsset asset;
                try{
                    if(Array.IndexOf(args,"-vgForce")>=0){AssetDatabase.ImportAsset(model,ImportAssetOptions.ForceUpdate|ImportAssetOptions.ForceSynchronousImport);}
                    asset=VirtualGeometryValidationModel.Configure(model,new VirtualGeometryModelSettings{MaxTrianglesPerCluster=128,HierarchyFanout=4});
                }finally{
                    Volatile.Write(ref sampling,false);sampler.Join();
                }
                double seconds=watch.Elapsed.TotalSeconds;
                string text="Conversion PASS\nTriangles="+asset.InputTriangleCount+"\nSeconds(OBJ再読込を含むインポート全体)="+seconds.ToString("R")+"\nPeakWorkingSet="+peakBytes+"\nLevels="+asset.LevelCount+"\nGeometryBytes="+asset.GeometryBytes+"\nMetadataBytes="+asset.MetadataBytes+"\nPages="+asset.StreamingPageCount+"\nSlotBytes="+asset.SlotGeometryBytes+"\nProxyTriangles="+asset.ProxyTriangleCount+"\nProxyError="+asset.ProxyGeometricError+"\n";
                VirtualGeometryPage page=VirtualGeometryPageCodec.Read(File.ReadAllBytes(VirtualGeometryPagePaths.Resolve(asset.PageRelativePath)));
                int level=(int)page.Nodes[page.RootNodeIndices[0]].Level/2;
                Mesh middle=CreateCut(page,level);
                AssetDatabase.DeleteAsset(root+"/LodMiddle.asset");AssetDatabase.CreateAsset(middle,root+"/LodMiddle.asset");
                text+="MiddleLodLevel="+level+"\nMiddleLodTriangles="+middle.triangles.Length/3+"\n";
                File.WriteAllText(output+"/PreparationResults.txt",text);
                AssetDatabase.SaveAssets();Debug.Log(text);Finish(output,samples,null);
            }catch(Exception error){Finish(output,samples,error);}
        }
        /// <summary>
        /// 生成した元モデル・中間LOD・それを参照するsceneを削除する。数百MBあり、変換器の版が変わるたびにEditorの再インポートで数分止まるため、計測の後は残さない。
        /// 計測をやり直すときはRunで作り直す。
        /// </summary>
        public static void Clean(){
            foreach(int grid in new[]{724,1224,2236}){DeleteGenerated("Assets/VirtualizedGeometrySample/Benchmark"+grid);}
            if(Application.isBatchMode){EditorApplication.Exit(0);}
        }

        internal static void DeleteGenerated(string root){
            // sceneは元モデルと中間LODを参照し、BenchmarkBuildが毎回作り直す。参照先が消えたsceneを残さないよう一緒に削除する。
            foreach(string name in new[]{"Source.obj","Source.mtl","LodMiddle.asset","Benchmark.unity"}){AssetDatabase.DeleteAsset(root+"/"+name);}
        }

        private static Mesh CreateCut(VirtualGeometryPage page,int level){
            var chosen=new List<int>();var queue=new Stack<uint>();foreach(uint rootNode in page.RootNodeIndices){if(rootNode!=uint.MaxValue){queue.Push(rootNode);}}
            while(queue.Count>0){var node=page.Nodes[queue.Pop()];if(node.ChildCount>0&&node.Level>level){for(uint i=0;i<node.ChildCount;i++){queue.Push(node.FirstChild+i);}}else{for(uint c=0;c<node.ClusterCount;c++){chosen.Add((int)(node.FirstCluster+c));}}}
            var map=new Dictionary<uint,int>();var vertices=new List<Vector3>();var normals=new List<Vector3>();var uv=new List<Vector2>();var indices=new List<int>();
            foreach(int id in chosen){var cluster=page.Clusters[id];for(uint i=0;i<cluster.IndexCount;i++){uint source=page.Indices[cluster.FirstIndex+i];if(!map.TryGetValue(source,out int next)){next=vertices.Count;map.Add(source,next);var vertex=page.Vertices[source];vertices.Add(vertex.Position);normals.Add(vertex.Normal);uv.Add(vertex.UV);}indices.Add(next);}}
            var mesh=new Mesh{name="Automatic hierarchy LOD",indexFormat=IndexFormat.UInt32};mesh.SetVertices(vertices);mesh.SetNormals(normals);mesh.SetUVs(0,uv);mesh.SetTriangles(indices,0);mesh.RecalculateBounds();return mesh;
        }
        private static Mesh CreateGrid(int n){
            Vector3[] vertices=new Vector3[(n+1)*(n+1)],normals=new Vector3[(n+1)*(n+1)];Vector2[] uv=new Vector2[vertices.Length];int[] indices=new int[n*n*6];
            for(int y=0;y<=n;y++){for(int x=0;x<=n;x++){float px=(float)x/n*100-50,py=(float)y/n*100-50;int i=y*(n+1)+x;vertices[i]=new Vector3(px,py,Mathf.Sin(px*0.17f)*Mathf.Cos(py*0.13f)*2);normals[i]=new Vector3(Mathf.Cos(px*0.17f)*Mathf.Cos(py*0.13f)*0.34f,-Mathf.Sin(px*0.17f)*Mathf.Sin(py*0.13f)*0.26f,-1).normalized;uv[i]=new Vector2((float)x/n,(float)y/n);}}
            int write=0;for(int y=0;y<n;y++){for(int x=0;x<n;x++){int a=y*(n+1)+x;indices[write++]=a;indices[write++]=a+n+1;indices[write++]=a+1;indices[write++]=a+1;indices[write++]=a+n+1;indices[write++]=a+n+2;}}
            var mesh=new Mesh{name="Benchmark unique "+(2L*n*n)+" triangles",indexFormat=IndexFormat.UInt32};mesh.vertices=vertices;mesh.normals=normals;mesh.uv=uv;mesh.triangles=indices;mesh.RecalculateBounds();return mesh;
        }
        private static void Finish(string output,List<string> samples,Exception error){
            lock(samples){File.WriteAllLines(output+"/ConversionMetrics.csv",samples);}
            if(error!=null){File.WriteAllText(output+"/PreparationFailure.txt",error.ToString());Debug.LogException(error);}
            EditorApplication.Exit(error==null?0:1);
        }
    }
}
