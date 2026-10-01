using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VirtualizedGeometry.Editor;

namespace VirtualizedGeometry.Tests{
    /// <summary>モデルのVirtual Geometryチェックで、インポートのたびに変換され、通常のMeshRendererのままVGで描ける構成になることを確かめる。</summary>
    public sealed class VirtualGeometryModelPostprocessorTests{
        private const string Folder="Assets/VirtualGeometryImportTests";
        private const string ModelPath=Folder+"/Wave.obj";

        [TearDown]
        public void TearDown(){
            AssetDatabase.DeleteAsset(Folder);
        }

        [Test]
        public void OnPostprocessModel(){
            Mesh source=Wave(24,16,0);
            try{
                VirtualGeometryObjWriter.Write(source,ModelPath);
                AssetDatabase.ImportAsset(ModelPath,ImportAssetOptions.ForceSynchronousImport);
                {
                    //チェックが無効なら通常のMeshRendererのまま変換しない
                    var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
                    Assert.That(prefab.GetComponentsInChildren<MeshRenderer>(true),Is.Not.Empty);
                    Assert.That(prefab.GetComponentsInChildren<VirtualGeometryRenderer>(true),Is.Empty);
                    Assert.That(AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<VirtualGeometryMeshAsset>(),Is.Empty);
                }
                {
                    //OBJの書き出しとインポートの往復で、位置(X反転の補正)と巻き順が元Meshと一致する
                    Mesh imported=ImportedMesh();
                    Assert.That(Vector3.Distance(imported.bounds.center,source.bounds.center),Is.LessThan(1e-3f));
                    Assert.That(Vector3.Distance(imported.bounds.size,source.bounds.size),Is.LessThan(1e-3f));
                    Assert.That(imported.subMeshCount,Is.EqualTo(source.subMeshCount));
                    Assert.That(FrontFacingRatio(imported),Is.GreaterThan(0.99f));
                }
                int resourcesBefore=VirtualGeometryRenderer.SharedResourceCount;
                Enable(new VirtualGeometryModelSettings{Enabled=true,MaxTrianglesPerCluster=32});
                string previousHash;
                {
                    //有効にすると変換データがモデルのサブアセットになる。MeshRendererとMaterialはそのまま残り、MeshFilterは低密度の代替Meshになり、VGとつなぐ部品が非表示で付く
                    VirtualGeometryMeshAsset converted=AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<VirtualGeometryMeshAsset>().Single();
                    Assert.That(converted.LevelCount,Is.GreaterThan(1));
                    Assert.That(converted.InputTriangleCount,Is.EqualTo(TriangleCount(source)));
                    Assert.That(converted.MaterialCount,Is.EqualTo(source.subMeshCount));
                    Assert.That(converted.ProxyMesh,Is.Not.Null);Assert.That(converted.ProxyLitMaterial,Is.Not.Null);
                    Assert.That(File.Exists(VirtualGeometryPagePaths.Resolve(converted.PageRelativePath)),Is.True);
                    var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
                    VirtualGeometryRenderer renderer=prefab.GetComponentsInChildren<VirtualGeometryRenderer>(true).Single();
                    Assert.That(renderer.MeshAsset,Is.SameAs(converted));
                    Assert.That(renderer.hideFlags&HideFlags.HideInInspector,Is.EqualTo(HideFlags.HideInInspector));
                    Assert.That(renderer.GetComponent<MeshFilter>().sharedMesh,Is.SameAs(converted.ProxyMesh));
                    MeshRenderer ordinary=renderer.GetComponent<MeshRenderer>();
                    Assert.That(ordinary,Is.Not.Null);
                    Assert.That(ordinary.sharedMaterials.Length,Is.EqualTo(source.subMeshCount));
                    Assert.That(ordinary.sharedMaterials.All(material=>material!=null),Is.True);
                    //元のMeshはサブアセットとして残り、通常のMeshRendererからも使える
                    Assert.That(ImportedMesh(),Is.Not.Null);
                    //インポート中のRendererはGPU資源を確保しない
                    Assert.That(VirtualGeometryRenderer.SharedResourceCount,Is.EqualTo(resourcesBefore));
                    // 再インポート後もUnityは同じobjectを使い回すため、比較用のhashは文字列で控える。
                    previousHash=converted.ContentHash;
                }
                {
                    //設定を変えて再インポートすると作り直される
                    Enable(new VirtualGeometryModelSettings{Enabled=true,MaxTrianglesPerCluster=16});
                    var rebuilt=AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<VirtualGeometryMeshAsset>().Single();
                    Assert.That(rebuilt.ContentHash,Is.Not.EqualTo(previousHash));
                    previousHash=rebuilt.ContentHash;
                }
                {
                    //モデルの衝突形状の事前ベイク設定は、MeshFilter/MeshColliderが使う代替Meshへ引き継がれる
                    var importer=(ModelImporter)AssetImporter.GetAtPath(ModelPath);
                    importer.SetPreBakeCollisionMesh(false,true);importer.SaveAndReimport();
                    Mesh proxy=AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<VirtualGeometryMeshAsset>().Single().ProxyMesh;
                    Assert.That(proxy.HasPreBakeCollisionMesh(false),Is.True);
                    Assert.That(proxy.HasPreBakeCollisionMesh(true),Is.False);
                    importer.SetPreBakeCollisionMesh(false,false);importer.SaveAndReimport();
                    proxy=AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<VirtualGeometryMeshAsset>().Single().ProxyMesh;
                    Assert.That(proxy.HasPreBakeCollisionMesh(false),Is.False);
                }
                {
                    //元のモデルファイルを書き換えると、設定を保ったまま作り直される
                    Mesh changed=Wave(30,20,1);
                    try{
                        VirtualGeometryObjWriter.Write(changed,ModelPath);
                        AssetDatabase.ImportAsset(ModelPath,ImportAssetOptions.ForceSynchronousImport);
                        var rebuilt=AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<VirtualGeometryMeshAsset>().Single();
                        Assert.That(rebuilt.InputTriangleCount,Is.EqualTo(TriangleCount(changed)));
                        Assert.That(rebuilt.ContentHash,Is.Not.EqualTo(previousHash));
                    }finally{Object.DestroyImmediate(changed);}
                }
                {
                    //チェックを外すとVGとつなぐ部品が無くなり、MeshFilterは元のMeshへ戻る
                    Enable(new VirtualGeometryModelSettings{Enabled=false});
                    var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
                    Assert.That(prefab.GetComponentsInChildren<VirtualGeometryRenderer>(true),Is.Empty);
                    Assert.That(prefab.GetComponentInChildren<MeshFilter>(true).sharedMesh,Is.SameAs(ImportedMesh()));
                    Assert.That(AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<VirtualGeometryMeshAsset>(),Is.Empty);
                }
            }finally{
                Object.DestroyImmediate(source);
            }
        }

        private static void Enable(VirtualGeometryModelSettings settings){
            var importer=(ModelImporter)AssetImporter.GetAtPath(ModelPath);
            importer.userData=settings.Write();importer.SaveAndReimport();
        }

        private static Mesh ImportedMesh(){
            return AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<Mesh>().FirstOrDefault(mesh=>!mesh.name.Contains("(Virtual Geometry"));
        }

        private static int TriangleCount(Mesh mesh){
            int count=0;
            for(int i=0;i<mesh.subMeshCount;i++){count+=(int)mesh.GetIndexCount(i)/3;}
            return count;
        }

        // 面の向き(巻き順から求めた法線)が頂点法線と同じ側を向く三角形の割合。巻き順が反転していれば0に近くなる。
        private static float FrontFacingRatio(Mesh mesh){
            Vector3[] positions=mesh.vertices;Vector3[] normals=mesh.normals;int front=0;int total=0;
            for(int submesh=0;submesh<mesh.subMeshCount;submesh++){
                int[] indices=mesh.GetIndices(submesh);
                for(int i=0;i<indices.Length;i+=3){
                    Vector3 face=Vector3.Cross(positions[indices[i+1]]-positions[indices[i]],positions[indices[i+2]]-positions[indices[i]]);
                    Vector3 normal=normals[indices[i]]+normals[indices[i+1]]+normals[indices[i+2]];
                    if(Vector3.Dot(face,normal)>0){front++;}
                    total++;
                }
            }
            return (float)front/total;
        }

        // X方向に偏らせ、左右反転の取り違えを位置で検出できるようにした2 submeshの波面。
        private static Mesh Wave(int width,int height,float phase){
            var vertices=new Vector3[(width+1)*(height+1)];var uv=new Vector2[vertices.Length];
            for(int y=0;y<=height;y++){
                for(int x=0;x<=width;x++){
                    int i=y*(width+1)+x;
                    vertices[i]=new Vector3(x*0.25f+3,Mathf.Sin(x*0.4f+phase)*Mathf.Cos(y*0.3f)*0.5f,y*0.25f);
                    uv[i]=new Vector2((float)x/width,(float)y/height);
                }
            }
            int half=height/2;
            var lower=new System.Collections.Generic.List<int>();var upper=new System.Collections.Generic.List<int>();
            for(int y=0;y<height;y++){
                for(int x=0;x<width;x++){
                    int a=y*(width+1)+x;int b=a+1;int c=a+width+1;int d=c+1;
                    var target=lower;
                    if(y>=half){target=upper;}
                    target.Add(a);target.Add(c);target.Add(b);target.Add(b);target.Add(c);target.Add(d);
                }
            }
            var mesh=new Mesh{name="Wave",indexFormat=IndexFormat.UInt32,subMeshCount=2};
            mesh.vertices=vertices;mesh.uv=uv;mesh.SetTriangles(lower,0);mesh.SetTriangles(upper,1);
            mesh.RecalculateNormals();mesh.RecalculateBounds();
            return mesh;
        }
    }
}
