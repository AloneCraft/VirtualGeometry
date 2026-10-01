using UnityEditor;

namespace VirtualizedGeometry.Editor{
    /// <summary>変換データの状態を、内部用語を使わない文で表す。Mesh AssetとRendererのInspectorで共有する。</summary>
    internal static class VirtualGeometryAssetText{
        /// <summary>LODの段数と三角形数、メモリの要約。</summary>
        public static string Summary(VirtualGeometryMeshAsset asset){
            return "LOD "+asset.LevelCount+"段 / 最も細かい段 "+asset.LeafTriangleCount.ToString("N0")+" 三角形 / 離れたときや準備中の代替表示 "+asset.ProxyTriangleCount.ToString("N0")+" 三角形\n"+
                "形状データ "+Bytes(asset.GeometryBytes)+"(必要な部分だけGPUへ読み込みます)";
        }

        /// <summary>モデルのインポートで作られたものでなく、自動で作り直されない旧方式のassetならその説明。該当しなければnull。</summary>
        public static string LegacyNotice(VirtualGeometryMeshAsset asset){
            if(asset==null||AssetDatabase.IsSubAsset(asset)){return null;}
            if(string.IsNullOrEmpty(AssetDatabase.GetAssetPath(asset))){return null;}
            return "旧方式で作られたMesh Assetのため、自動では作り直されず表示できません。元のモデルをProjectで選び、Inspectorの「Virtual Geometry」にチェックを入れてApplyし、モデル内にできたMesh Assetへ差し替えてください。";
        }

        public static string Bytes(long bytes){
            if(bytes>=1<<20){return (bytes/(double)(1<<20)).ToString("F1")+" MB";}
            if(bytes>=1<<10){return (bytes/(double)(1<<10)).ToString("F1")+" KB";}
            return bytes+" B";
        }
    }
}
