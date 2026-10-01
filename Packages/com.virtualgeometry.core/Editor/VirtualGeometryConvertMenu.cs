using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

namespace VirtualizedGeometry.Editor{
    /// <summary>Projectの右クリックから選択Meshを直接変換する。出力は元assetと同じフォルダの *_VG.asset。</summary>
    internal static class VirtualGeometryConvertMenu{
        private const string MenuPath="Assets/仮想ジオメトリに変換";

        private readonly struct Target{
            public readonly Mesh Mesh;public readonly string OutputPath;
            public Target(Mesh mesh,string outputPath){this.Mesh=mesh;this.OutputPath=outputPath;}
        }

        [MenuItem(MenuPath,true)]
        private static bool Validate(){
            return Collect(Selection.objects).Count>0;
        }

        [MenuItem(MenuPath,false,19)]
        private static void Convert(){
            var decisions=new Dictionary<string,bool>();
            foreach(Target target in Collect(Selection.objects)){
                Mesh mesh=EnsureReadable(target.Mesh,decisions);
                if(mesh==null){continue;}
                string output=target.OutputPath;
                VirtualGeometryConversionRunner.Enqueue(mesh,output,new VirtualGeometryBuildSettings(),false,(asset,error)=>{
                    if(asset==null){Debug.LogWarning("仮想ジオメトリへ変換できませんでした: "+output+"\n"+error);return;}
                    Debug.Log("仮想ジオメトリへ変換しました: "+output+" ("+asset.LeafTriangleCount.ToString("N0")+" triangles / "+asset.NodeCount+" nodes / "+asset.StreamingPageCount+" pages)",asset);
                    Selection.activeObject=asset;EditorGUIUtility.PingObject(asset);
                });
            }
        }

        /// <summary>Read/Writeが無効なmodel由来Meshは、同意を得た場合だけimport設定を有効化して再読込する。geometryは変更しない。</summary>
        internal static Mesh EnsureReadable(Mesh mesh,Dictionary<string,bool> decisions){
            if(mesh==null||mesh.isReadable){return mesh;}
            string path=AssetDatabase.GetAssetPath(mesh);
            if(!(AssetImporter.GetAtPath(path) is ModelImporter importer)){
                Debug.LogWarning("Read/Writeが無効なMeshは変換できません: "+path);return null;
            }
            // 再importで元のMesh参照は無効になるため、識別子を先に控える。
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh,out string _,out long localId);
            string name=mesh.name;
            if(!decisions.TryGetValue(path,out bool accepted)){
                accepted=EditorUtility.DisplayDialog("Read/Writeが無効です",path+"\n\n変換にはMeshの頂点を読む必要があります。このmodelのImport設定でRead/Writeを有効にして再インポートしますか。geometryは変更しません。","有効化して変換","スキップ");
                decisions.Add(path,accepted);
                if(accepted){importer.isReadable=true;importer.SaveAndReimport();}
            }
            if(!accepted){return null;}
            Mesh byName=null;
            foreach(Object candidate in AssetDatabase.LoadAllAssetsAtPath(path)){
                if(!(candidate is Mesh reloaded)){continue;}
                if(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(reloaded,out string _,out long reloadedId)&&reloadedId==localId){return reloaded;}
                if(reloaded.name==name){byName=reloaded;}
            }
            return byName;
        }

        private static List<Target> Collect(Object[] selection){
            var result=new List<Target>();var seen=new HashSet<Mesh>();
            foreach(Object selected in selection){
                if(selected is Mesh mesh){Add(mesh,result,seen);continue;}
                // VGは静的geometry用のため、modelからはMeshFilterのMeshだけを対象にする。
                if(selected is GameObject model&&AssetDatabase.Contains(model)){
                    foreach(MeshFilter filter in model.GetComponentsInChildren<MeshFilter>(true)){Add(filter.sharedMesh,result,seen);}
                }
            }
            return result;
        }

        private static void Add(Mesh mesh,List<Target> result,HashSet<Mesh> seen){
            if(mesh==null||!seen.Add(mesh)){return;}
            string output=OutputPath(mesh);
            if(output!=null){result.Add(new Target(mesh,output));}
        }

        /// <summary>元assetと同じフォルダの *_VG.asset。project外(Library/組込み)のMeshはnull。</summary>
        internal static string OutputPath(Mesh mesh){
            string path=AssetDatabase.GetAssetPath(mesh);
            if(string.IsNullOrEmpty(path)||!path.StartsWith("Assets/",StringComparison.Ordinal)){return null;}
            string folder=Path.GetDirectoryName(path).Replace('\\','/');
            string fileName=Path.GetFileNameWithoutExtension(path);
            string name=Sanitize(mesh.name);
            if(!AssetDatabase.IsMainAsset(mesh)&&Sanitize(fileName)!=name){name=Sanitize(fileName)+"_"+name;}
            return folder+"/"+name+"_VG.asset";
        }

        private const string ReplaceMenuPath="GameObject/仮想ジオメトリに置き換え";

        [MenuItem(ReplaceMenuPath,true)]
        private static bool ValidateReplace(){
            foreach(GameObject target in Selection.gameObjects){if(Replaceable(target,out _)){return true;}}
            return false;
        }

        /// <summary>
        /// Hierarchyで選んだMeshRendererのMeshを変換し、完了後に同じGameObjectへ専用Rendererを付ける。
        /// MaterialはMeshRendererから引き継ぎ、元のMeshRendererは無効化する(Undo 1回で戻せる)。
        /// </summary>
        [MenuItem(ReplaceMenuPath,false,20)]
        private static void Replace(){
            var decisions=new Dictionary<string,bool>();
            foreach(GameObject target in Selection.gameObjects){
                if(!Replaceable(target,out Mesh source)){continue;}
                Mesh mesh=EnsureReadable(source,decisions);
                if(mesh==null){continue;}
                string output=OutputPath(mesh);
                GameObject owner=target;
                VirtualGeometryConversionRunner.Enqueue(mesh,output,new VirtualGeometryBuildSettings(),false,(asset,error)=>{
                    if(asset==null){Debug.LogWarning("仮想ジオメトリへ置き換えできませんでした: "+owner?.name+"\n"+error,owner);return;}
                    if(owner==null){return;}
                    VirtualGeometryConversionService.PlaceRenderer(asset,owner);
                    Debug.Log("仮想ジオメトリへ置き換えました: "+owner.name+" ("+asset.LeafTriangleCount.ToString("N0")+" triangles)",owner);
                });
            }
        }

        private static bool Replaceable(GameObject target,out Mesh mesh){
            mesh=null;
            if(target==null||target.GetComponent<VirtualGeometryRenderer>()!=null){return false;}
            var filter=target.GetComponent<MeshFilter>();var renderer=target.GetComponent<MeshRenderer>();
            if(filter==null||renderer==null||filter.sharedMesh==null){return false;}
            if(OutputPath(filter.sharedMesh)==null){return false;}
            mesh=filter.sharedMesh;return true;
        }

        private static string Sanitize(string name){
            foreach(char invalid in Path.GetInvalidFileNameChars()){name=name.Replace(invalid,'_');}
            if(string.IsNullOrWhiteSpace(name)){return "Mesh";}
            return name;
        }
    }

    /// <summary>
    /// 右クリックメニューとmesh asset Inspectorが共有する変換queue。
    /// 1件ずつVirtualGeometryConversionService.Beginし、Editor updateでpollする。進捗はBackground Tasksへ出し、Cancelを受け付ける。
    /// </summary>
    internal static class VirtualGeometryConversionRunner{
        private sealed class Job{
            public Mesh Source;public string OutputPath;public VirtualGeometryBuildSettings Settings;public bool Force;public Action<VirtualGeometryMeshAsset,string> Completed;
        }
        private static readonly Queue<Job> pending=new Queue<Job>();
        private static Job current;private static VirtualGeometryConversionOperation operation;
        private static int progressId=-1;private static int total;private static int done;

        public static bool IsBusy=>current!=null||pending.Count>0;
        /// <summary>直近に完了した変換が、source/設定/archiveの一致によりcacheを再利用したか。</summary>
        public static bool LastCacheHit{get;private set;}
        public static string CurrentOutputPath=>current?.OutputPath;
        public static float CurrentProgress{
            get{
                if(operation==null){return 0;}
                return operation.Progress;
            }
        }

        public static void Enqueue(Mesh source,string outputPath,VirtualGeometryBuildSettings settings,bool force,Action<VirtualGeometryMeshAsset,string> completed){
            pending.Enqueue(new Job{Source=source,OutputPath=outputPath,Settings=settings.Clone(),Force=force,Completed=completed});total++;
            if(progressId>=0){return;}
            progressId=Progress.Start("仮想ジオメトリへ変換");
            Progress.RegisterCancelCallback(progressId,Cancel);
            EditorApplication.update+=Update;
            AssemblyReloadEvents.beforeAssemblyReload+=Abort;
        }

        /// <summary>待機中の変換を破棄し、実行中の変換へCancelを伝える。公開前のstagingは変換側が破棄する。</summary>
        public static bool Cancel(){
            pending.Clear();operation?.Cancel();return true;
        }

        private static void Abort(){
            Cancel();operation?.Dispose();operation=null;current=null;Finish();
        }

        private static void Update(){
            if(operation==null){
                if(pending.Count==0){Finish();return;}
                current=pending.Dequeue();
                try{
                    operation=VirtualGeometryConversionService.Begin(current.Source,current.OutputPath,current.Settings,current.Force);
                }catch(Exception error){
                    Complete(null,error.Message);return;
                }
            }
            Progress.Report(progressId,(done+operation.Progress)/Mathf.Max(1,total),current.OutputPath);
            if(!operation.IsCompleted){return;}
            VirtualGeometryMeshAsset asset=null;string message=null;LastCacheHit=false;
            try{asset=operation.Commit();LastCacheHit=operation.WasCacheHit;}
            catch(OperationCanceledException){message="キャンセルしました。";}
            catch(Exception error){message=error.Message;}
            finally{operation.Dispose();operation=null;}
            Complete(asset,message);
        }

        private static void Complete(VirtualGeometryMeshAsset asset,string error){
            Job job=current;current=null;done++;
            try{job.Completed?.Invoke(asset,error);}catch(Exception callback){Debug.LogException(callback);}
        }

        private static void Finish(){
            EditorApplication.update-=Update;AssemblyReloadEvents.beforeAssemblyReload-=Abort;
            if(progressId>=0){Progress.Finish(progressId);}
            progressId=-1;total=0;done=0;
        }
    }
}
