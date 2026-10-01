using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

namespace VirtualizedGeometry.Editor{
    /// <summary>ビルド中だけ原本をLibraryへ退避する。必ずusingで囲み、失敗時も元のパスへ復元する。</summary>
    public sealed class VirtualGeometryBuildArchiveScope:IDisposable{
        private readonly string source;private readonly string staging;private bool moved;
        public string[] RequiredFiles{get;}
        public VirtualGeometryBuildArchiveScope(string source,string staging,string[] relativePaths){
            this.source=ProjectChild(source);this.staging=ProjectChild(staging);
            if(Directory.Exists(this.staging)||File.Exists(this.staging+".meta")){throw new IOException("Build staging already exists; restore it before another build.");}
            if(!Directory.Exists(this.source)){throw new DirectoryNotFoundException(this.source);}
            string prefix=this.source+Path.DirectorySeparatorChar;
            string[] paths=VirtualGeometryBuildInclusion.DistinctArchives(relativePaths);
            foreach(string relative in paths){
                string full=Path.GetFullPath(Path.Combine(this.source,relative));
                if(Path.IsPathRooted(relative)||!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)){throw new InvalidDataException("Archive path escapes its root.");}
                if(!File.Exists(full)){throw new FileNotFoundException("Required converted archive is missing.",full);}
            }
            this.RequiredFiles=paths.Select(path=>Path.GetFullPath(Path.Combine(this.staging,path))).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(this.staging));
            Directory.Move(this.source,this.staging);this.moved=true;
            try{if(File.Exists(this.source+".meta")){File.Move(this.source+".meta",this.staging+".meta");}}catch{Directory.Move(this.staging,this.source);if(File.Exists(this.staging+".meta")){File.Move(this.staging+".meta",this.source+".meta");}this.moved=false;throw;}
        }
        private static string ProjectChild(string path){
            string full=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            string prefix=Path.GetFullPath(".").TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)){throw new InvalidDataException("Build staging must stay within the current project.");}return full;
        }
        public void Dispose(){
            if(!this.moved){return;}
            if(Directory.Exists(this.source)){if(Directory.EnumerateFileSystemEntries(this.source).Any()){throw new IOException("Archive restoration target contains unexpected data; originals remain in "+this.staging);}Directory.Delete(this.source,false);}
            Directory.Move(this.staging,this.source);if(File.Exists(this.staging+".meta")){File.Move(this.staging+".meta",this.source+".meta");}this.moved=false;
        }
    }

    /// <summary>Scene/Resourcesの参照を解決した専用ビルド入口。非参照archiveをPlayerへ収録しない。</summary>
    [InitializeOnLoad]
    public static class VirtualGeometryPlayerBuild{
        private static readonly string Source=Path.GetFullPath("Assets/StreamingAssets/VirtualGeometry");
        private static readonly string Staging=Path.GetFullPath("Library/VirtualGeometryBuildArchives");
        internal static VirtualGeometryBuildArchiveScope CurrentScope;
        internal static string[] CurrentRelativePaths;
        static VirtualGeometryPlayerBuild(){EditorApplication.delayCall+=RecoverInterruptedBuild;}
        public static void RecoverInterruptedBuild(){
            if(BuildPipeline.isBuildingPlayer||CurrentScope!=null||!Directory.Exists(Staging)){return;}
            if(Directory.Exists(Source)){if(Directory.EnumerateFileSystemEntries(Source).Any()){throw new IOException("Interrupted VG build has data in both source and staging folders; preserve both and inspect them.");}Directory.Delete(Source,false);}
            Directory.Move(Staging,Source);if(File.Exists(Staging+".meta")){File.Move(Staging+".meta",Source+".meta");}
        }
        public static BuildReport Build(BuildPlayerOptions options){
            RecoverInterruptedBuild();
            string[] resources=AssetDatabase.GetAllAssetPaths().Where(path=>path.StartsWith("Assets/",StringComparison.Ordinal)&&path.Contains("/Resources/")&&!AssetDatabase.IsValidFolder(path)).ToArray();
            string[] dependencies=AssetDatabase.GetDependencies(options.scenes.Concat(resources).ToArray(),true);
            CurrentRelativePaths=VirtualGeometryBuildInclusion.DistinctArchives(dependencies.Select(path=>AssetDatabase.LoadAssetAtPath<VirtualGeometryMeshAsset>(path)).Where(asset=>asset!=null).Select(asset=>{
                string path=asset.PageRelativePath.Replace('\\','/');
                if(!path.StartsWith("VirtualGeometry/",StringComparison.Ordinal)){throw new InvalidDataException("VG build requires archives under VirtualGeometry/.");}return path.Substring("VirtualGeometry/".Length);
            }));
            AssetDatabase.SaveAssets();AssetDatabase.DisallowAutoRefresh();
            try{
                using(var scope=new VirtualGeometryBuildArchiveScope(Source,Staging,CurrentRelativePaths)){
                    CurrentScope=scope;return BuildPipeline.BuildPlayer(options);
                }
            }finally{CurrentScope=null;CurrentRelativePaths=null;AssetDatabase.AllowAutoRefresh();}
        }
    }
    internal sealed class VirtualGeometryPlayerBuildProcessor:BuildPlayerProcessor,IProcessSceneWithReport{
        public override int callbackOrder=>0;
        public override void PrepareForBuild(BuildPlayerContext context){
            if(VirtualGeometryPlayerBuild.CurrentScope==null){return;}
            for(int i=0;i<VirtualGeometryPlayerBuild.CurrentRelativePaths.Length;i++){
                context.AddAdditionalPathToStreamingAssets(VirtualGeometryPlayerBuild.CurrentScope.RequiredFiles[i],"VirtualGeometry/"+VirtualGeometryPlayerBuild.CurrentRelativePaths[i]);
            }
        }
        public void OnProcessScene(Scene scene,BuildReport report){
            if(report==null){return;}
            foreach(GameObject root in scene.GetRootGameObjects()){
                foreach(var renderer in root.GetComponentsInChildren<VirtualGeometryRenderer>(true)){
                    MeshFilter source=renderer.GetComponent<MeshFilter>();MeshRenderer visual=renderer.GetComponent<MeshRenderer>();
                    if(source==null||source.sharedMesh==null||visual==null||visual.enabled||renderer.MeshAsset==null){continue;}
                    if(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source.sharedMesh,out string guid,out long localId)&&guid==renderer.MeshAsset.SourceGuid&&localId==renderer.MeshAsset.SourceLocalId){
                        Object.DestroyImmediate(visual);Object.DestroyImmediate(source);
                    }
                }
            }
        }
    }
}
