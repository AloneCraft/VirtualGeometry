using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;

namespace VirtualizedGeometry.Editor{
    /// <summary>Mesh Asset InspectorのUI Toolkit Clickable経路と右クリック変換のqueueで、変換/Cancel/cache/配置Undoを検証する。</summary>
    public static class M5EditorValidation{
        private const string Root="Assets/VirtualizedGeometrySample/M5/EditorValidation";
        private sealed class Host:EditorWindow{}
        private static Host host;private static UnityEditor.Editor editor;private static VisualElement inspector;private static VirtualGeometryMeshAsset asset;
        private static Mesh large,small;private static int stage;private static double started,cancelRequested;
        private static int updates;private static readonly List<string> results=new List<string>();private static readonly List<string> errors=new List<string>();
        public static void Run(){
            Directory.CreateDirectory("TestResults/M5");Directory.CreateDirectory(Root);AssetDatabase.Refresh();results.Clear();errors.Clear();stage=0;updates=0;started=EditorApplication.timeSinceStartup;
            Application.logMessageReceived+=Log;
            try{
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                large=AssetDatabase.LoadAssetAtPath<Mesh>(Root+"/Large.asset");if(large==null){large=Grid(384);AssetDatabase.CreateAsset(large,Root+"/Large.asset");}
                small=AssetDatabase.LoadAssetAtPath<Mesh>(Root+"/Small.asset");if(small==null){small=Grid(8);AssetDatabase.CreateAsset(small,Root+"/Small.asset");}AssetDatabase.SaveAssets();
                host=ScriptableObject.CreateInstance<Host>();host.Show();
                Inspect(Root+"/Cancelled.asset");
                EditorApplication.update+=Tick;
            }catch(Exception error){Finish(error);}
        }
        private static void Log(string message,string stack,LogType type){if(type==LogType.Warning||type==LogType.Error||type==LogType.Exception||type==LogType.Assert){errors.Add(message);}}
        /// <summary>空のmesh assetを作り直し、そのInspectorをwindowへ載せてscheduleを動かす。</summary>
        private static void Inspect(string path){
            AssetDatabase.DeleteAsset(path);
            asset=ScriptableObject.CreateInstance<VirtualGeometryMeshAsset>();AssetDatabase.CreateAsset(asset,path);AssetDatabase.SaveAssets();
            if(editor!=null){Object.DestroyImmediate(editor);}
            editor=UnityEditor.Editor.CreateEditor(asset);inspector=editor.CreateInspectorGUI();
            host.rootVisualElement.Clear();host.rootVisualElement.Add(inspector);
        }
        private static void Submit(string name){
            Button button=inspector.Q<Button>(name);
            var method=typeof(Clickable).GetMethod("SimulateSingleClick",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
            if(method==null){throw new MissingMethodException("UI Toolkit Clickable.SimulateSingleClick");}
            var parameters=method.GetParameters();object[] arguments=new object[parameters.Length];for(int i=0;i<arguments.Length;i++){arguments[i]=parameters[i].ParameterType==typeof(int)?(object)0:null;}
            method.Invoke(button.clickable,arguments);
        }
        private static string PagePath()=>Path.Combine(Application.streamingAssetsPath,asset.PageRelativePath);
        private static void Tick(){
            try{
                updates++;if(EditorApplication.timeSinceStartup-started>120){throw new TimeoutException("Editor UI validation timeout at stage "+stage);}
                switch(stage){
                    case 0:
                        if(updates<4){return;}inspector.Q<ObjectField>("source").value=large;Submit("convert");
                        if(!VirtualGeometryConversionRunner.IsBusy){throw new InvalidOperationException("Inspector submit did not start conversion.");}stage=1;break;
                    case 1:
                        float progress=inspector.Q<ProgressBar>("progress").value;if(progress<10){return;}
                        results.Add("cancel request at build progress="+progress+"%, editor updates="+updates+", source triangles="+(large.triangles.Length/3));
                        cancelRequested=EditorApplication.timeSinceStartup;Submit("cancel");stage=2;break;
                    case 2:
                        if(VirtualGeometryConversionRunner.IsBusy){return;}
                        if(!string.IsNullOrEmpty(asset.PageRelativePath)){throw new InvalidOperationException("Cancelled Inspector conversion published a page.");}
                        if(Directory.Exists("Library/VirtualGeometryStaging")&&Directory.GetFiles("Library/VirtualGeometryStaging","*.tmp").Length!=0){throw new InvalidOperationException("Cancelled conversion left staging files.");}
                        results.Add("Inspector Cancel latency ms="+((EditorApplication.timeSinceStartup-cancelRequested)*1000).ToString("R")+", partial page/staging=0");
                        Inspect(Root+"/Converted.asset");inspector.Q<ObjectField>("source").value=small;Submit("convert");stage=3;break;
                    case 3:
                        if(VirtualGeometryConversionRunner.IsBusy){return;}
                        if(string.IsNullOrEmpty(asset.PageRelativePath)||VirtualGeometryConversionRunner.LastCacheHit){throw new InvalidOperationException("Inspector conversion did not fill the asset.");}
                        results.Add("Inspector convert into empty asset: PASS");
                        // 右クリック変換と同じ既定設定・cache再利用ありの経路。
                        VirtualGeometryConversionRunner.Enqueue(small,AssetDatabase.GetAssetPath(asset),new VirtualGeometryBuildSettings(),false,null);stage=4;break;
                    case 4:
                        if(VirtualGeometryConversionRunner.IsBusy){return;}
                        if(!VirtualGeometryConversionRunner.LastCacheHit){throw new InvalidOperationException("Default conversion did not reuse matching cache.");}results.Add("identical cache hit: PASS");
                        Selection.activeGameObject=null;Submit("place");var placed=Selection.activeGameObject;
                        if(placed==null||placed.GetComponent<VirtualGeometryRenderer>()==null){throw new InvalidOperationException("Inspector placement failed.");}
                        Undo.FlushUndoRecordObjects();Undo.PerformUndo();if(placed!=null){throw new InvalidOperationException("Placement Undo did not restore scene.");}results.Add("Inspector placement/Undo: PASS");
                        string archive=PagePath();Submit("clear");if(File.Exists(archive)){throw new InvalidOperationException("Clear cache left archive.");}results.Add("Inspector cache delete: PASS");
                        VirtualGeometryConversionRunner.Enqueue(small,AssetDatabase.GetAssetPath(asset),new VirtualGeometryBuildSettings(),false,null);stage=5;break;
                    case 5:
                        if(VirtualGeometryConversionRunner.IsBusy){return;}
                        if(VirtualGeometryConversionRunner.LastCacheHit||!File.Exists(PagePath())){throw new InvalidOperationException("Deleted cache was not rebuilt.");}
                        results.Add("deleted cache regeneration: PASS");if(errors.Count>0){throw new InvalidOperationException(string.Join(" | ",errors));}Finish(null);break;
                }
            }catch(Exception error){Finish(error);}
        }
        private static Mesh Grid(int n){
            var vertices=new Vector3[(n+1)*(n+1)];var uv=new Vector2[vertices.Length];var indices=new int[n*n*6];int write=0;
            for(int y=0;y<=n;y++){for(int x=0;x<=n;x++){int i=y*(n+1)+x;vertices[i]=new Vector3(x,y,Mathf.Sin(x*0.1f)*Mathf.Cos(y*0.1f));uv[i]=new Vector2((float)x/n,(float)y/n);}}
            for(int y=0;y<n;y++){for(int x=0;x<n;x++){int a=y*(n+1)+x;indices[write++]=a;indices[write++]=a+n+1;indices[write++]=a+1;indices[write++]=a+1;indices[write++]=a+n+1;indices[write++]=a+n+2;}}
            var mesh=new Mesh{name="UI source "+n,indexFormat=IndexFormat.UInt32};mesh.vertices=vertices;mesh.uv=uv;mesh.triangles=indices;mesh.RecalculateNormals();return mesh;
        }
        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Log;
            if(editor!=null){Object.DestroyImmediate(editor);}if(host!=null){host.Close();}
            string text=string.Join(Environment.NewLine,results)+Environment.NewLine;
            if(error!=null){text+="FAIL: "+error;Debug.LogException(error);}else{text+="PASS: Editor conversion UI scope only";Debug.Log(text);}File.WriteAllText("TestResults/M5/EditorResults.txt",text);EditorApplication.Exit(error==null?0:1);
        }
    }
}
