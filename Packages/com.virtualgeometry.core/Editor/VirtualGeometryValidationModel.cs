using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VirtualizedGeometry.Editor{
    /// <summary>
    /// 検証scriptの元Meshを、利用者と同じ経路(モデルの「Virtual Geometry」チェックとインポート)で変換する。
    /// 元MeshはOBJとして書き出す。内容が同じなら書き直してもUnityは再インポートしない。
    /// </summary>
    internal static class VirtualGeometryValidationModel{
        /// <summary>sourceをpath(.obj)へ書き出し、settingsでVirtual Geometryを有効にしてインポートした変換データを返す。</summary>
        public static VirtualGeometryMeshAsset Import(Mesh source,string path,VirtualGeometryModelSettings settings){
            if(source==null){throw new ArgumentNullException(nameof(source));}
            VirtualGeometryObjWriter.Write(source,path);
            // OBJは.mtlに依存するため、先に.mtlを取り込む。
            AssetDatabase.ImportAsset(System.IO.Path.ChangeExtension(path,".mtl"),ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport);
            return Configure(path,settings);
        }

        /// <summary>既存のモデルにsettingsを設定し、変わっていれば再インポートして変換データを返す。</summary>
        public static VirtualGeometryMeshAsset Configure(string path,VirtualGeometryModelSettings settings){
            var importer=AssetImporter.GetAtPath(path) as ModelImporter;
            if(importer==null){throw new InvalidOperationException(path+" is not a model.");}
            settings.Enabled=true;
            string data=settings.Write();
            if(importer.userData!=data){importer.userData=data;importer.SaveAndReimport();}
            VirtualGeometryMeshAsset asset=Load(path);
            if(asset==null){throw new InvalidOperationException(path+" was not converted. See the import warnings.");}
            return asset;
        }

        /// <summary>モデル内の変換データ。複数Meshのモデルでは最初の1つ。</summary>
        public static VirtualGeometryMeshAsset Load(string path){
            return AssetDatabase.LoadAllAssetsAtPath(path).OfType<VirtualGeometryMeshAsset>().FirstOrDefault();
        }

        /// <summary>
        /// 起動時の固定pageがrootからProxyの段まで届く最小のslot数(固定するchainのpageのslot数と、細分化に空けるslot数の和)。
        /// 準備直後のVGをProxyと同じ形で描かせたい検証で、予算の下限に使う。
        /// </summary>
        public static int SlotsToPinProxyLevel(VirtualGeometryStreamMetadata metadata){
            var pages=new System.Collections.Generic.HashSet<uint>{0};
            foreach(uint root in metadata.Roots){
                if(root==uint.MaxValue){continue;}
                uint node=root;
                while(metadata.Nodes[node].ChildCount==1){node=metadata.Nodes[node].FirstChild;pages.Add(metadata.Nodes[node].PageId);}
            }
            int slots=0;
            foreach(uint page in pages){slots+=metadata.PageSlots((int)page);}
            return slots+VirtualGeometryStreamingController.RefinementReserveSlots(metadata);
        }

        /// <summary>変換データのpage archiveのmetadata。</summary>
        public static VirtualGeometryStreamMetadata ReadMetadata(VirtualGeometryMeshAsset asset){
            using(var file=System.IO.File.OpenRead(VirtualGeometryPagePaths.Resolve(asset.PageRelativePath))){return VirtualGeometryStreamCodec.ReadMetadata(file);}
        }

        /// <summary>CPUで選んだclusterだけを持つMesh。GPU選択の結果を通常のMeshRendererで描いて比べるために使う。</summary>
        public static Mesh CutMesh(VirtualGeometryPage page,VirtualGeometryLodSelectionResult selection){
            var mesh=new Mesh{indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};
            mesh.vertices=page.Vertices.Select(v=>v.Position).ToArray();mesh.normals=page.Vertices.Select(v=>v.Normal).ToArray();mesh.uv=page.Vertices.Select(v=>v.UV).ToArray();
            mesh.subMeshCount=page.MaterialCount;
            var indices=new System.Collections.Generic.List<int>[page.MaterialCount];
            for(int m=0;m<indices.Length;m++){indices[m]=new System.Collections.Generic.List<int>();}
            foreach(int c in selection.SelectedClusters){
                var cluster=page.Clusters[c];
                for(uint i=0;i<cluster.IndexCount;i++){indices[cluster.MaterialSlot].Add((int)page.Indices[cluster.FirstIndex+i]);}
            }
            for(int m=0;m<indices.Length;m++){mesh.SetTriangles(indices[m],m);}
            mesh.RecalculateBounds();return mesh;
        }

        /// <summary>モデルが読み込んだ元のMesh(通常のMeshRendererで比較描画するため)。</summary>
        public static Mesh SourceMesh(string path){
            return AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().FirstOrDefault(mesh=>!mesh.name.EndsWith("(Virtual Geometry Proxy)",StringComparison.Ordinal));
        }
    }
}
