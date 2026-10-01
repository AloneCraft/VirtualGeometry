using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VirtualizedGeometry.Editor{
    /// <summary>
    /// Meshから変換データ(索引asset・Proxy・page archive)を作る。モデルのインポート中に呼ぶため、AssetDatabaseとUndoは使わない。
    /// 生成物はまだどのassetにも属さない。呼び出し側がAssetImportContextへ追加する。
    /// </summary>
    internal static class VirtualGeometryAssetBuild{
        /// <summary>変換器の版。上げるとAssetPostprocessorの版が変わり、Virtual Geometryを有効にした全モデルが自動で再変換される。</summary>
        public const int BuilderVersion=8;

        internal sealed class Result{
            public VirtualGeometryMeshAsset Asset;
            public Mesh Proxy;
            public Material ProxyLit;
            public Material ProxyUnlit;
            public Texture2D FlatNormal;
        }

        public static string SettingsKey(VirtualGeometryBuildSettings settings){
            string version="v2";
            if(settings.GenerateStreamingPages&&settings.GenerateHierarchy){version="v3";}
            int hierarchy=0;
            if(settings.GenerateHierarchy){hierarchy=1;}
            return version+"-t"+settings.MaxTrianglesPerCluster+"-h"+hierarchy+"-f"+settings.HierarchyFanout+
                "-r"+settings.SimplificationRatio.ToString("R",CultureInfo.InvariantCulture)+"-q"+settings.PositionQuantizationBits+"-b"+BuilderVersion;
        }

        /// <summary>meshを変換し、page archiveをLibrary/VirtualGeometryへ書く。sourceGuidは元モデルのGUID(詳細表示と元モデルの選択に使う)。</summary>
        public static Result Build(Mesh mesh,VirtualGeometryBuildSettings settings,string sourceGuid){
            if(mesh==null){throw new ArgumentNullException(nameof(mesh));}
            VirtualGeometryBuildOutput output=VirtualGeometryBuilder.Build(VirtualGeometryMeshInput.FromMesh(mesh),settings);
            byte[] bytes;VirtualGeometryStreamMetadata metadata=null;
            if(settings.GenerateHierarchy&&settings.GenerateStreamingPages){
                // 変換結果から直接streaming archiveを書く。単一page形式を経由すると、大きなmeshで書き出しと読み直しの時間が増える。
                bytes=VirtualGeometryStreamCodec.Write(output.Page);
                metadata=VirtualGeometryStreamCodec.ReadMetadata(new MemoryStream(bytes,false));
            }else{
                bytes=output.PageBytes;
            }
            string hash=Hash(bytes);
            string relative=VirtualGeometryPagePaths.Folder+"/"+hash+".vgpage";
            WritePage(VirtualGeometryPagePaths.Resolve(relative),bytes);

            var result=new Result();
            result.Proxy=VirtualGeometryProxyBuilder.Create(output,out int proxyTriangles,out float proxyError);result.Proxy.name=mesh.name+" (Virtual Geometry Proxy)";
            result.FlatNormal=new Texture2D(1,1,TextureFormat.RGBAHalf,false,true){name=mesh.name+" (Virtual Geometry Flat Normal)"};
            result.FlatNormal.SetPixel(0,0,new Color(0.5f,0.5f,1,1));result.FlatNormal.Apply();
            result.ProxyLit=new Material(Shader.Find("Universal Render Pipeline/Lit")){name=mesh.name+" (Virtual Geometry Lit)"};
            result.ProxyUnlit=new Material(Shader.Find("Universal Render Pipeline/Unlit")){name=mesh.name+" (Virtual Geometry Unlit)"};
            result.ProxyLit.SetFloat("_Cull",0);result.ProxyUnlit.SetFloat("_Cull",0);
            result.ProxyLit.SetTexture("_BumpMap",result.FlatNormal);result.ProxyLit.EnableKeyword("_NORMALMAP");

            result.Asset=ScriptableObject.CreateInstance<VirtualGeometryMeshAsset>();result.Asset.name=mesh.name+" (Virtual Geometry)";
            var serialized=new SerializedObject(result.Asset);
            serialized.FindProperty("proxyMesh").objectReferenceValue=result.Proxy;
            serialized.FindProperty("proxyTriangleCount").intValue=proxyTriangles;
            serialized.FindProperty("proxyGeometricError").floatValue=proxyError;
            serialized.FindProperty("proxyLitMaterial").objectReferenceValue=result.ProxyLit;
            serialized.FindProperty("proxyUnlitMaterial").objectReferenceValue=result.ProxyUnlit;
            serialized.FindProperty("sourceGuid").stringValue=sourceGuid??string.Empty;
            serialized.FindProperty("settingsHash").stringValue=SettingsKey(settings);
            serialized.FindProperty("contentHash").stringValue=hash;
            serialized.FindProperty("pageRelativePath").stringValue=relative;
            serialized.FindProperty("bounds").boundsValue=output.Bounds;
            serialized.FindProperty("inputTriangleCount").intValue=output.InputTriangleCount;
            serialized.FindProperty("triangleCount").intValue=output.Indices.Length/3;
            serialized.FindProperty("leafTriangleCount").intValue=output.LeafTriangleCount;
            serialized.FindProperty("clusterCount").intValue=output.Clusters.Length;
            serialized.FindProperty("nodeCount").intValue=output.Nodes.Length;
            serialized.FindProperty("levelCount").intValue=LevelCount(output.Nodes);
            serialized.FindProperty("materialCount").intValue=mesh.subMeshCount;
            if(metadata!=null){
                serialized.FindProperty("streamingPageCount").intValue=metadata.Pages.Length;
                serialized.FindProperty("geometryBytes").longValue=metadata.UniqueGeometryBytes;
                serialized.FindProperty("metadataBytes").intValue=metadata.MetadataBytes;
                serialized.FindProperty("slotGeometryBytes").intValue=metadata.SlotGeometryBytes;
                serialized.FindProperty("formatVersion").intValue=3;
            }else{
                serialized.FindProperty("streamingPageCount").intValue=1;
                serialized.FindProperty("geometryBytes").longValue=(long)output.Vertices.Length*32+(long)output.Indices.Length*4;
                serialized.FindProperty("formatVersion").intValue=2;
            }
            serialized.FindProperty("builderVersion").intValue=BuilderVersion;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return result;
        }

        private static int LevelCount(VirtualGeometryHierarchyNode[] nodes){
            uint maximum=0;
            foreach(VirtualGeometryHierarchyNode node in nodes){maximum=Math.Max(maximum,node.Level);}
            if(nodes.Length==0){return 1;}
            return checked((int)maximum+1);
        }

        // 内容のhashを名前にするため、同じ内容は書き直さない。途中で失敗しても壊れたpageが残らないよう、一時fileへ書いてから移す。
        private static void WritePage(string path,byte[] bytes){
            if(File.Exists(path)&&new FileInfo(path).Length==bytes.LongLength){return;}
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{
                File.WriteAllBytes(temporary,bytes);
                if(File.Exists(path)){File.Delete(path);}
                File.Move(temporary,path);
            }finally{
                if(File.Exists(temporary)){File.Delete(temporary);}
            }
        }

        // pageの名前と同一性に使う内容hash(128 bit)。UnityのMonoではSHA256が数百MBで数十秒かかるため、Unityのnative hashを使う。
        private static string Hash(byte[] bytes){
            return Hash128.Compute(bytes).ToString();
        }
    }
}
