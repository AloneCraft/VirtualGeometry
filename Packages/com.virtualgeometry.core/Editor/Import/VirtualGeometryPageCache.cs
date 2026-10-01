using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VirtualizedGeometry.Editor{
    /// <summary>
    /// Editor起動時に変換キャッシュ(Library/VirtualGeometry)を点検する。
    /// Libraryの復元(Accelerator等)でpageが欠けたモデルは再インポートして作り直し、どの変換データからも参照されないpageは削除する。
    /// </summary>
    [InitializeOnLoad]
    internal static class VirtualGeometryPageCache{
        static VirtualGeometryPageCache(){
            if(Application.isBatchMode){return;}
            EditorApplication.delayCall+=Maintain;
        }

        public static void Maintain(){
            var referenced=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missing=new HashSet<string>(StringComparer.Ordinal);
            foreach(string guid in AssetDatabase.FindAssets("t:"+nameof(VirtualGeometryMeshAsset))){
                string path=AssetDatabase.GUIDToAssetPath(guid);
                foreach(VirtualGeometryMeshAsset asset in AssetDatabase.LoadAllAssetsAtPath(path).OfType<VirtualGeometryMeshAsset>()){
                    if(string.IsNullOrEmpty(asset.PageRelativePath)||!AssetDatabase.IsSubAsset(asset)){continue;}
                    string page=VirtualGeometryPagePaths.Resolve(asset.PageRelativePath);
                    referenced.Add(page);
                    if(!File.Exists(page)){missing.Add(path);}
                }
            }
            foreach(string path in missing){
                Debug.Log("Virtual Geometry: 変換データが無いため再インポートします: "+path);
                AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceUpdate);
            }
            // 直下の.vgpageだけを対象にする。検証用のサブフォルダ(障害注入archive等)は触らない。
            string folder=VirtualGeometryPagePaths.Resolve(VirtualGeometryPagePaths.Folder+"/.");
            if(!Directory.Exists(folder)){return;}
            foreach(string file in Directory.GetFiles(folder,"*.vgpage",SearchOption.TopDirectoryOnly)){
                if(!referenced.Contains(Path.GetFullPath(file))){File.Delete(file);}
            }
        }
    }
}
