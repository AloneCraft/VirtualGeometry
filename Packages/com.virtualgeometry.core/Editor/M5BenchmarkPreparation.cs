using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Debug=UnityEngine.Debug;

namespace VirtualizedGeometry.Editor{
    /// <summary>規模ごとの変換時間/実process memoryを監視し、予算超過時はworkerを取消す。</summary>
    public static class M5BenchmarkPreparation{
        private static VirtualGeometryConversionOperation operation;private static Stopwatch watch;private static int grid;private static string root,output;private static long peakBytes,budget;private static bool cancelled;
        private static readonly List<string> samples=new List<string>();private static int updates;
        public static void Run(){
            grid=724;string[] args=Environment.GetCommandLineArgs();for(int i=0;i<args.Length-1;i++){if(args[i]=="-vgGrid"){grid=int.Parse(args[i+1]);}}
            root="Assets/VirtualizedGeometrySample/Benchmark"+grid;output="TestResults/M5/Benchmark"+grid;Directory.CreateDirectory(root);Directory.CreateDirectory(output);AssetDatabase.Refresh();
            watch=Stopwatch.StartNew();samples.Clear();samples.Add("seconds,progress,working_set_bytes");peakBytes=0;updates=0;cancelled=false;budget=Math.Min(12L*1024*1024*1024,(long)SystemInfo.systemMemorySize*1024*1024/3);
            try{
                Mesh mesh=AssetDatabase.LoadAssetAtPath<Mesh>(root+"/Source.asset");if(mesh==null){mesh=CreateGrid(grid);AssetDatabase.CreateAsset(mesh,root+"/Source.asset");AssetDatabase.SaveAssets();}
                File.WriteAllText(output+"/PreparationEnvironment.txt","Unity="+Application.unityVersion+"\nGPU="+SystemInfo.graphicsDeviceName+"\nSystemMiB="+SystemInfo.systemMemorySize+"\nMemoryLimitBytes="+budget+"\nTimeLimitSeconds=600\nGrid="+grid+"\nTriangles="+(2L*grid*grid)+"\nSourcePreparationSeconds="+watch.Elapsed.TotalSeconds.ToString("R")+"\n");
                watch.Restart();operation=VirtualGeometryConversionService.Begin(mesh,root+"/Converted.asset",new VirtualGeometryBuildSettings{MaxTrianglesPerCluster=128,HierarchyFanout=4},Array.IndexOf(args,"-vgForce")>=0);EditorApplication.update+=Tick;
            }catch(Exception error){Finish(error);}
        }
        private static void Tick(){
            try{
                {long bytes=VirtualGeometryProcessMemory.WorkingSetBytes;peakBytes=Math.Max(peakBytes,bytes);if(updates++%10==0){samples.Add(watch.Elapsed.TotalSeconds.ToString("R")+","+operation.Progress.ToString("R")+","+bytes);}if(!operation.IsCompleted&&(bytes>budget||watch.Elapsed.TotalSeconds>600)){operation.Cancel();cancelled=true;}}
                if(!operation.IsCompleted){return;}
                if(cancelled){File.WriteAllText(output+"/PreparationResults.txt","INCOMPLETE: conversion cancelled at resource/time limit.\nSeconds="+watch.Elapsed.TotalSeconds.ToString("R")+"\nPeakWorkingSet="+peakBytes+"\nProgress="+operation.Progress+"\n");Finish(null);return;}
                VirtualGeometryMeshAsset asset=operation.Commit();double seconds=watch.Elapsed.TotalSeconds;bool cache=operation.WasCacheHit;operation.Dispose();operation=null;
                string text="Conversion PASS\nTriangles="+asset.InputTriangleCount+"\nSeconds="+seconds.ToString("R")+"\nCacheHit="+cache+"\nPeakWorkingSet="+peakBytes+"\nGeometryBytes="+asset.GeometryBytes+"\nMetadataBytes="+asset.MetadataBytes+"\nPages="+asset.StreamingPageCount+"\nSlotBytes="+asset.SlotGeometryBytes+"\nProxyTriangles="+asset.ProxyTriangleCount+"\nProxyError="+asset.ProxyGeometricError+"\n";
                File.WriteAllText(output+"/PreparationResults.txt",text);
                if(AssetDatabase.LoadAssetAtPath<Mesh>(root+"/LodMiddle.asset")==null){
                    VirtualGeometryPage page=VirtualGeometryPageCodec.Read(File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath,asset.PageRelativePath)));int level=(int)page.Nodes[page.RootNodeIndices[0]].Level/2;
                    Mesh middle=CreateCut(page,level);AssetDatabase.CreateAsset(middle,root+"/LodMiddle.asset");File.AppendAllText(output+"/PreparationResults.txt","MiddleLodLevel="+level+"\nMiddleLodTriangles="+middle.triangles.Length/3+"\n");
                }
                AssetDatabase.SaveAssets();Debug.Log(text);Finish(null);
            }catch(Exception error){Finish(error);}
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
        private static void Finish(Exception error){
            EditorApplication.update-=Tick;operation?.Dispose();operation=null;File.WriteAllLines(output+"/ConversionMetrics.csv",samples);if(error!=null){File.WriteAllText(output+"/PreparationFailure.txt",error.ToString());Debug.LogException(error);}EditorApplication.Exit(error==null?0:1);
        }
    }
}