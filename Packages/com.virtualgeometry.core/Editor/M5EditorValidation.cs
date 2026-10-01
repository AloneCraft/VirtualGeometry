using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using Object=UnityEngine.Object;

namespace VirtualizedGeometry.Editor{
    /// <summary>
    /// 利用者のEditor操作の流れを検証する。モデルのInspectorのApplyで変換され、MeshRendererのままVGで描ける構成になること、
    /// 変換データのScene viewドロップとUndo、page欠落時の自動再インポート、チェックを外したときの復元を確かめる。
    /// </summary>
    public static class M5EditorValidation{
        private const string Root="Assets/VirtualizedGeometrySample/M5/EditorValidation";
        private const string ModelPath=Root+"/Grid.obj";
        private static readonly List<string> results=new List<string>();private static readonly List<string> errors=new List<string>();

        public static void Run(){
            Directory.CreateDirectory("TestResults/M5");results.Clear();errors.Clear();
            Application.logMessageReceived+=Log;
            try{
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                Mesh grid=Grid(96);VirtualGeometryObjWriter.Write(grid,ModelPath);Object.DestroyImmediate(grid);
                AssetDatabase.ImportAsset(ModelPath,ImportAssetOptions.ForceSynchronousImport);
                var importer=(ModelImporter)AssetImporter.GetAtPath(ModelPath);
                VirtualGeometryModelImporterGUI.Apply(new[]{importer},new VirtualGeometryModelSettings{Enabled=false});
                {
                    // InspectorのApplyでチェックを入れると、再インポートで変換されMeshRendererが置き換わる
                    VirtualGeometryModelImporterGUI.Apply(new[]{importer},new VirtualGeometryModelSettings{Enabled=true,MaxTrianglesPerCluster=32});
                    var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
                    VirtualGeometryMeshAsset asset=VirtualGeometryValidationModel.Load(ModelPath);
                    VirtualGeometryRenderer bridge=null;if(asset!=null){bridge=prefab.GetComponentInChildren<VirtualGeometryRenderer>(true);}
                    if(bridge==null||bridge.MeshAsset!=asset||bridge.GetComponent<MeshFilter>().sharedMesh!=asset.ProxyMesh||bridge.GetComponent<MeshRenderer>()==null||(bridge.hideFlags&HideFlags.HideInInspector)==0){throw new InvalidOperationException("Apply did not convert the model into MeshRenderer + hidden Virtual Geometry bridge.");}
                    results.Add("model Inspector Apply: converted levels="+asset.LevelCount+", MeshRenderer kept with fallback mesh, hidden bridge: PASS");
                    // 変換データのInspectorは読み取り専用の要約と元モデルの選択だけを出す
                    var inspector=UnityEditor.Editor.CreateEditor(asset);
                    try{
                        VisualElement root=inspector.CreateInspectorGUI();
                        if(root.Query<Button>().ToList().Count!=1){throw new InvalidOperationException("Mesh Asset Inspector should only offer the source model selection.");}
                    }finally{Object.DestroyImmediate(inspector);}
                    results.Add("Mesh Asset Inspector read-only summary: PASS");
                    // Scene viewへ変換データをドロップすると配置され、Undo 1回で戻る
                    Selection.activeGameObject=null;DragAndDrop.objectReferences=new Object[]{asset};
                    if(VirtualGeometryDragAndDrop.SceneDrop(null,new Vector3(1,2,3),Vector2.zero,null,false)!=DragAndDropVisualMode.Copy){throw new InvalidOperationException("Dragging the asset over Scene view was not accepted.");}
                    VirtualGeometryDragAndDrop.SceneDrop(null,new Vector3(1,2,3),Vector2.zero,null,true);var placed=Selection.activeGameObject;
                    VirtualGeometryRenderer placedRenderer=null;if(placed!=null){placedRenderer=placed.GetComponent<VirtualGeometryRenderer>();}
                    DragAndDrop.objectReferences=new Object[0];
                    if(placedRenderer==null||placedRenderer.MeshAsset!=asset||placed.transform.position!=new Vector3(1,2,3)){throw new InvalidOperationException("Scene view drop placement failed.");}
                    Undo.FlushUndoRecordObjects();Undo.PerformUndo();if(placed!=null){throw new InvalidOperationException("Placement Undo did not restore scene.");}
                    results.Add("Scene view drop placement/Undo: PASS");
                    // 変換キャッシュのpageが失われても、点検でモデルを再インポートして作り直す
                    string page=VirtualGeometryPagePaths.Resolve(asset.PageRelativePath);File.Delete(page);
                    VirtualGeometryPageCache.Maintain();
                    if(!File.Exists(VirtualGeometryPagePaths.Resolve(VirtualGeometryValidationModel.Load(ModelPath).PageRelativePath))){throw new InvalidOperationException("Missing page was not rebuilt.");}
                    results.Add("missing page regeneration by reimport: PASS");
                }
                {
                    // チェックを外すとVGとつなぐ部品と変換データが無くなり、通常のMeshRendererだけになる
                    VirtualGeometryModelImporterGUI.Apply(new[]{importer},new VirtualGeometryModelSettings{Enabled=false});
                    var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
                    if(VirtualGeometryValidationModel.Load(ModelPath)!=null||prefab.GetComponentsInChildren<VirtualGeometryRenderer>(true).Length!=0||prefab.GetComponentsInChildren<MeshRenderer>(true).Length==0){throw new InvalidOperationException("Disabling did not restore the ordinary renderer.");}
                    results.Add("disable restores MeshRenderer: PASS");
                }
                if(errors.Count>0){throw new InvalidOperationException(string.Join(" | ",errors));}
                Finish(null);
            }catch(Exception error){Finish(error);}
        }
        private static void Log(string message,string stack,LogType type){if(type==LogType.Warning||type==LogType.Error||type==LogType.Exception||type==LogType.Assert){errors.Add(message);}}
        private static Mesh Grid(int n){
            var vertices=new Vector3[(n+1)*(n+1)];var uv=new Vector2[vertices.Length];var indices=new int[n*n*6];int write=0;
            for(int y=0;y<=n;y++){for(int x=0;x<=n;x++){int i=y*(n+1)+x;vertices[i]=new Vector3(x,y,Mathf.Sin(x*0.1f)*Mathf.Cos(y*0.1f));uv[i]=new Vector2((float)x/n,(float)y/n);}}
            for(int y=0;y<n;y++){for(int x=0;x<n;x++){int a=y*(n+1)+x;indices[write++]=a;indices[write++]=a+n+1;indices[write++]=a+1;indices[write++]=a+1;indices[write++]=a+n+1;indices[write++]=a+n+2;}}
            var mesh=new Mesh{name="Editor validation grid "+n,indexFormat=IndexFormat.UInt32};mesh.vertices=vertices;mesh.uv=uv;mesh.triangles=indices;mesh.RecalculateNormals();return mesh;
        }
        private static void Finish(Exception error){
            Application.logMessageReceived-=Log;
            string text=string.Join(Environment.NewLine,results)+Environment.NewLine;
            if(error!=null){text+="FAIL: "+error;Debug.LogException(error);}else{text+="PASS: Editor model workflow";Debug.Log(text);}
            File.WriteAllText("TestResults/M5/EditorResults.txt",text);EditorApplication.Exit(error==null?0:1);
        }
    }
}
