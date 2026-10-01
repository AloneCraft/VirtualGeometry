using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using VirtualizedGeometry.Editor;

namespace VirtualizedGeometry.Tests{
    public sealed class VirtualGeometryConversionTransactionTests{
        private const string Root="Assets/VirtualGeometryTransactionTests";
        [UnityTest]
        public IEnumerator BackgroundCancelAndSourceInvalidationLeaveNoPartialOutput(){
            Directory.CreateDirectory(Root);AssetDatabase.Refresh();Mesh source=Grid(20);string sourcePath=Root+"/Source.asset",outputPath=Root+"/Converted.asset";AssetDatabase.CreateAsset(source,sourcePath);
            try{
                using(var operation=VirtualGeometryConversionService.Begin(source,outputPath,new VirtualGeometryBuildSettings())){
                    operation.Cancel();while(!operation.IsCompleted){yield return null;}
                    Assert.Throws<OperationCanceledException>(()=>operation.Commit());
                    Assert.That(AssetDatabase.LoadAssetAtPath<VirtualGeometryMeshAsset>(outputPath),Is.Null);
                    Assert.That(File.Exists(operation.StagingPath),Is.False);
                }
                using(var operation=VirtualGeometryConversionService.Begin(source,outputPath,new VirtualGeometryBuildSettings())){
                    while(!operation.IsCompleted){yield return null;}
                    Vector3[] changedVertices=source.vertices;changedVertices[0]+=Vector3.up*0.25f;source.vertices=changedVertices;source.RecalculateBounds();EditorUtility.SetDirty(source);AssetDatabase.SaveAssets();
                    Assert.Throws<InvalidOperationException>(()=>operation.Commit());
                    Assert.That(AssetDatabase.LoadAssetAtPath<VirtualGeometryMeshAsset>(outputPath),Is.Null);
                }
            }finally{AssetDatabase.DeleteAsset(Root);}
        }
        [UnityTest]
        public IEnumerator AtomicCommitCacheValidationAndUndoPreserveSourceAndReferences(){
            Directory.CreateDirectory(Root);AssetDatabase.Refresh();Mesh source=Grid(8);string sourcePath=Root+"/Source.asset",outputPath=Root+"/Converted.asset";AssetDatabase.CreateAsset(source,sourcePath);AssetDatabase.SaveAssets();
            string sourceHash=AssetDatabase.GetAssetDependencyHash(sourcePath).ToString();string firstPage=null,secondPage=null;
            try{
                VirtualGeometryMeshAsset asset;var settings=new VirtualGeometryBuildSettings{MaxTrianglesPerCluster=16,HierarchyFanout=4};
                using(var operation=VirtualGeometryConversionService.Begin(source,outputPath,settings)){while(!operation.IsCompleted){yield return null;}asset=operation.Commit();Assert.That(operation.WasCacheHit,Is.False);}
                firstPage=asset.PageRelativePath;Mesh proxy=asset.ProxyMesh;Assert.That(File.Exists(Path.Combine(Application.streamingAssetsPath,firstPage)),Is.True);
                using(var operation=VirtualGeometryConversionService.Begin(source,outputPath,settings)){while(!operation.IsCompleted){yield return null;}Assert.That(operation.Commit(),Is.SameAs(asset));Assert.That(operation.WasCacheHit,Is.True);}
                Assert.That(asset.ProxyMesh,Is.SameAs(proxy));
                settings.MaxTrianglesPerCluster=32;
                using(var operation=VirtualGeometryConversionService.Begin(source,outputPath,settings)){while(!operation.IsCompleted){yield return null;}Assert.That(operation.Commit(),Is.SameAs(asset));Assert.That(operation.WasCacheHit,Is.False);}
                secondPage=asset.PageRelativePath;Assert.That(secondPage,Is.Not.EqualTo(firstPage));Assert.That(asset.ProxyMesh,Is.SameAs(proxy));
                Undo.PerformUndo();Assert.That(asset.PageRelativePath,Is.EqualTo(firstPage));Assert.That(asset.ProxyMesh,Is.SameAs(proxy));
                Assert.That(AssetDatabase.GetAssetDependencyHash(sourcePath).ToString(),Is.EqualTo(sourceHash));
                byte[] original=File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath,firstPage));File.WriteAllBytes(Path.Combine(Application.streamingAssetsPath,firstPage),new byte[]{1,2,3});
                using(var operation=VirtualGeometryConversionService.Begin(source,outputPath,new VirtualGeometryBuildSettings{MaxTrianglesPerCluster=16,HierarchyFanout=4})){
                    while(!operation.IsCompleted){yield return null;}operation.Commit();Assert.That(operation.WasCacheHit,Is.False);
                }
                Assert.That(File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath,firstPage)),Is.EqualTo(original));
            }finally{
                Undo.ClearAll();AssetDatabase.DeleteAsset(Root);
                if(firstPage!=null){AssetDatabase.DeleteAsset("Assets/StreamingAssets/"+firstPage);}if(secondPage!=null){AssetDatabase.DeleteAsset("Assets/StreamingAssets/"+secondPage);}
            }
        }
        private static Mesh Grid(int n){
            var vertices=new Vector3[(n+1)*(n+1)];var indices=new int[n*n*6];int write=0;
            for(int y=0;y<=n;y++){for(int x=0;x<=n;x++){vertices[y*(n+1)+x]=new Vector3(x,y,0);}}
            for(int y=0;y<n;y++){for(int x=0;x<n;x++){int a=y*(n+1)+x;indices[write++]=a;indices[write++]=a+n+1;indices[write++]=a+1;indices[write++]=a+1;indices[write++]=a+n+1;indices[write++]=a+n+2;}}
            var mesh=new Mesh{name="Source"};mesh.vertices=vertices;mesh.triangles=indices;mesh.RecalculateNormals();return mesh;
        }
    }
}