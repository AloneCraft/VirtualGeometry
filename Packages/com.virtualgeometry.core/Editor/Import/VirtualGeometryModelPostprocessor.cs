using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VirtualizedGeometry.Editor{
    /// <summary>
    /// Unity標準のModelImporter(FBX・OBJ・DAE・3DS・DXF・.blend等)でVirtual Geometryを有効にしたモデルを、インポートのたびに変換する。
    /// 変換データはモデルのサブアセットにする。インポート結果のMeshRenderer/MeshFilterはそのまま残し、MeshFilterには低密度の代替Mesh
    /// (UE5のFallback Mesh相当)を入れて、VGとつなぐ非表示の部品を付ける。Material・影の設定は通常どおりMeshRendererで行う。
    /// 元Meshや設定が変わればUnityがモデルを再インポートするため、変換結果が古いまま残らない。
    /// </summary>
    internal sealed class VirtualGeometryModelPostprocessor:AssetPostprocessor{
        // インポート結果の構成の版。変換器の版と合わせてUnityへ伝え、どちらかが上がると全モデルを再インポートして作り直させる。
        // 2: MeshRendererを残し非表示の部品を付ける構成。3: pageの内容hashをUnityのHash128へ変更。4: 衝突形状の事前ベイク設定を代替Meshへ引き継ぐ。
        // 5: streamingの割り当て単位(slot)を最大のpageからpageの中央値へ変更(変換データに保存するslotの大きさが変わる)。
        private const int LayoutVersion=5;
        public override uint GetVersion(){return (uint)(VirtualGeometryAssetBuild.BuilderVersion*100+LayoutVersion);}

        private void OnPostprocessModel(GameObject root){
            if(!VirtualGeometryModelSettings.TryRead(this.assetImporter.userData,out VirtualGeometryModelSettings settings)||!settings.Enabled){return;}
            settings.Clamp();
            VirtualGeometryBuildSettings buildSettings=settings.ToBuildSettings();
            string guid=AssetDatabase.AssetPathToGUID(this.assetPath);
            var built=new Dictionary<Mesh,VirtualGeometryMeshAsset>();
            var names=new Dictionary<string,int>();
            foreach(MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true)){
                var renderer=filter.GetComponent<MeshRenderer>();
                Mesh mesh=filter.sharedMesh;
                if(renderer==null||mesh==null){continue;}
                if(!built.TryGetValue(mesh,out VirtualGeometryMeshAsset asset)){
                    asset=this.Convert(mesh,buildSettings,guid,Identifier(mesh.name,names));
                    built.Add(mesh,asset);
                }
                // 変換できなかったMeshは元のMeshのまま残し、表示が消えないようにする。
                if(asset==null){continue;}
                var bridge=filter.gameObject.AddComponent<VirtualGeometryRenderer>();
                bridge.hideFlags|=HideFlags.HideInInspector;
                bridge.SetMeshAsset(asset);
            }
        }

        // identifierはモデル内で一意かつ再インポートで変わらない値にする。変わるとsceneからの参照が切れるため、
        // 階層順の番号ではなくMesh名を使い、同名のMeshだけ出現順の番号を付ける。
        private static string Identifier(string meshName,Dictionary<string,int> names){
            names.TryGetValue(meshName,out int count);names[meshName]=count+1;
            if(count==0){return "VirtualGeometry/"+meshName;}
            return "VirtualGeometry/"+meshName+"#"+count;
        }

        private VirtualGeometryMeshAsset Convert(Mesh mesh,VirtualGeometryBuildSettings settings,string guid,string prefix){
            VirtualGeometryAssetBuild.Result result;
            try{
                result=VirtualGeometryAssetBuild.Build(mesh,settings,guid);
            }catch(Exception error){
                this.context.LogImportWarning("Virtual Geometry: Mesh \""+mesh.name+"\" を変換できなかったため、通常のMeshRendererのまま表示します。理由: "+error.Message);
                return null;
            }
            // MeshFilterとMeshColliderは元Meshの代わりに代替Meshを使う。モデルのImport Settingsにある衝突形状の事前ベイク設定を代替Meshへ引き継ぎ、
            // 利用者が通常のモデルと同じ操作で、Player buildでの衝突形状の事前ベイクを選べるようにする。
            if(this.assetImporter is ModelImporter model){
                result.Proxy.SetPreBakeCollisionMesh(false,model.HasPreBakeCollisionMesh(false));
                result.Proxy.SetPreBakeCollisionMesh(true,model.HasPreBakeCollisionMesh(true));
            }
            this.context.AddObjectToAsset(prefix,result.Asset);
            this.context.AddObjectToAsset(prefix+"/Proxy",result.Proxy);
            this.context.AddObjectToAsset(prefix+"/ProxyLit",result.ProxyLit);
            this.context.AddObjectToAsset(prefix+"/ProxyUnlit",result.ProxyUnlit);
            this.context.AddObjectToAsset(prefix+"/FlatNormal",result.FlatNormal);
            return result.Asset;
        }
    }
}
