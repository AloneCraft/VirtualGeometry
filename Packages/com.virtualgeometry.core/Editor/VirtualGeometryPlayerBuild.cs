using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace VirtualizedGeometry.Editor{
    /// <summary>
    /// Player buildへ、build対象のsceneとResourcesが参照する変換データのpageだけを収録する。
    /// pageはEditorの変換キャッシュ(Library/VirtualGeometry)にあり、StreamingAssets/VirtualGeometryへ同じ相対パスで追加する。通常のFile &gt; Buildでも働く。
    /// </summary>
    internal sealed class VirtualGeometryPlayerBuildProcessor:BuildPlayerProcessor{
        public override int callbackOrder=>0;

        public override void PrepareForBuild(BuildPlayerContext context){
            string[] scenes=context.BuildPlayerOptions.scenes;
            if(scenes==null||scenes.Length==0){scenes=EditorBuildSettings.scenes.Where(scene=>scene.enabled).Select(scene=>scene.path).ToArray();}
            foreach(string relative in VirtualGeometryPlayerBuild.ReferencedPages(scenes)){
                string source=VirtualGeometryPagePaths.Resolve(relative);
                if(!File.Exists(source)){throw new BuildFailedException("Virtual Geometryの変換データが見つかりません: "+relative+"。元のモデルを再インポートしてからbuildしてください。");}
                context.AddAdditionalPathToStreamingAssets(source,relative);
            }
        }
    }

    /// <summary>build対象から参照される変換データのpage一覧と、検証用のbuild入口。</summary>
    public static class VirtualGeometryPlayerBuild{
        public static string[] ReferencedPages(string[] scenes){
            string[] resources=AssetDatabase.GetAllAssetPaths().Where(path=>path.StartsWith("Assets/",StringComparison.Ordinal)&&path.Contains("/Resources/")&&!AssetDatabase.IsValidFolder(path)).ToArray();
            string[] dependencies=AssetDatabase.GetDependencies(scenes.Concat(resources).ToArray(),true);
            // 変換データはモデルのサブアセットなので、依存するassetの全サブアセットから探す。
            var pages=dependencies.SelectMany(AssetDatabase.LoadAllAssetsAtPath).OfType<VirtualGeometryMeshAsset>().Select(asset=>asset.PageRelativePath.Replace('\\','/'));
            return VirtualGeometryBuildInclusion.DistinctArchives(pages);
        }

        /// <summary>検証scriptからのbuild入口。収録はVirtualGeometryPlayerBuildProcessorが行う。</summary>
        public static BuildReport Build(BuildPlayerOptions options){
            AssetDatabase.SaveAssets();
            return BuildPipeline.BuildPlayer(options);
        }
    }
}
