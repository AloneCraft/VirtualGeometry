using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace VirtualizedGeometry.Editor{
    /// <summary>
    /// 変換結果の索引を読み取り専用で表示し、変換・再変換・キャンセル・配置・page cache削除の入口をまとめる。
    /// 索引の値は変換だけが書き込むため、生のserialized fieldは編集させない。
    /// </summary>
    [CustomEditor(typeof(VirtualGeometryMeshAsset))]
    internal sealed class VirtualGeometryMeshAssetInspector:UnityEditor.Editor{
        private ObjectField source;private IntegerField cluster,fanout,quantization;private FloatField ratio;
        private HelpBox info;private Button convert,cancel,place;private ProgressBar progress;private HelpBox result;

        public override VisualElement CreateInspectorGUI(){
            var asset=(VirtualGeometryMeshAsset)this.target;
            var root=new VisualElement();
            this.info=new HelpBox(string.Empty,HelpBoxMessageType.Info);root.Add(this.info);
            this.source=new ObjectField("元 Mesh"){name="source",objectType=typeof(Mesh),allowSceneObjects=false,value=VirtualGeometryConversionService.ResolveSourceMesh(asset),
                tooltip="変換元のMesh。元Meshは変更しません。変換済みassetでは変換時のMeshが入ります。"};
            root.Add(this.source);
            this.convert=new Button(this.StartConversion){name="convert"};root.Add(this.convert);
            this.cancel=new Button(()=>VirtualGeometryConversionRunner.Cancel()){name="cancel",text="キャンセル",tooltip="実行中の変換を中止します。公開前の途中結果は残りません。"};root.Add(this.cancel);
            this.progress=new ProgressBar{name="progress",lowValue=0,highValue=100};root.Add(this.progress);
            this.place=new Button(this.Place){name="place",text="選択GameObjectに配置",tooltip="選択中のGameObjectへVirtual Geometry Rendererを付けてこのassetを割り当てます。選択が無ければ新しいGameObjectを作ります。元Meshを表示中のMeshRendererは無効化します。Undoで戻せます。"};
            root.Add(this.place);
            var details=new Foldout{text="詳細",value=false};
            var defaults=new VirtualGeometryBuildSettings();
            this.cluster=new IntegerField("Cluster最大triangles"){value=defaults.MaxTrianglesPerCluster,tooltip="1 clusterの三角形数(1〜128)。最粗のrootもこの数以下になるまでLOD段を作ります。"};
            this.fanout=new IntegerField("階層fanout"){value=defaults.HierarchyFanout,tooltip="何個の子nodeを束ねて1つの親を作るか(2〜8)。"};
            this.ratio=new FloatField("親の目標簡略化率"){value=defaults.SimplificationRatio,tooltip="1段ごとに三角形数を何倍へ減らすか(0.1〜0.9)。0.5で半分。"};
            this.quantization=new IntegerField("位置量子化 bits"){value=defaults.PositionQuantizationBits,tooltip="頂点座標をmesh全体の格子へ丸める精度(8〜24、0で丸めない)。"};
            details.Add(this.cluster);details.Add(this.fanout);details.Add(this.ratio);details.Add(this.quantization);
            details.Add(new Button(this.ClearCache){name="clear",text="参照page cacheを削除",tooltip="このassetが参照するStreamingAssetsのpageファイルを削除します。削除後はProxy表示になり、次の変換で再生成します。"});
            root.Add(details);
            this.result=new HelpBox(string.Empty,HelpBoxMessageType.Info);this.result.style.display=DisplayStyle.None;root.Add(this.result);
            this.Refresh();
            root.schedule.Execute(this.Refresh).Every(100);
            return root;
        }

        private void Refresh(){
            if(this.target==null||this.info==null){return;}
            var asset=(VirtualGeometryMeshAsset)this.target;
            bool converted=!string.IsNullOrEmpty(asset.PageRelativePath);
            if(converted){
                this.info.text=Describe(asset);this.info.messageType=HelpBoxMessageType.Info;
                this.convert.text="再変換";this.convert.tooltip="元Meshと詳細の設定で作り直し、このassetを更新します。";
            }else{
                this.info.text="未変換です。元 Mesh を選んで「このアセットへ変換」を押すと、LOD階層・page・Proxyを生成してこのアセットに設定します。";this.info.messageType=HelpBoxMessageType.Warning;
                this.convert.text="このアセットへ変換";this.convert.tooltip="元Meshを変換し、このassetに結果を設定します。";
            }
            if(converted&&asset.BuilderVersion!=VirtualGeometryConversionService.CurrentBuilderVersion){
                this.info.text+="\n旧builder "+asset.BuilderVersion+" の変換結果です。再変換で現在の階層(builder "+VirtualGeometryConversionService.CurrentBuilderVersion+")へ更新できます。";
                this.info.messageType=HelpBoxMessageType.Warning;
            }
            bool busy=VirtualGeometryConversionRunner.IsBusy;
            bool mine=VirtualGeometryConversionRunner.CurrentOutputPath==AssetDatabase.GetAssetPath(asset);
            this.convert.SetEnabled(!busy);this.cancel.SetEnabled(busy);this.place.SetEnabled(converted&&!busy);
            this.progress.style.display=DisplayStyle.None;
            if(mine){this.progress.style.display=DisplayStyle.Flex;this.progress.value=VirtualGeometryConversionRunner.CurrentProgress*100;this.progress.title="変換 "+this.progress.value.ToString("F0")+"%";}
        }

        private void StartConversion(){
            var asset=(VirtualGeometryMeshAsset)this.target;
            Mesh mesh=VirtualGeometryConvertMenu.EnsureReadable(this.source.value as Mesh,new Dictionary<string,bool>());
            if(mesh==null){this.ShowResult("元 Mesh を指定してください(Read/Write が有効な project 内の Mesh)。",HelpBoxMessageType.Error);return;}
            var settings=new VirtualGeometryBuildSettings{MaxTrianglesPerCluster=this.cluster.value,HierarchyFanout=this.fanout.value,SimplificationRatio=this.ratio.value,
                PositionQuantizationBits=this.quantization.value,GenerateHierarchy=true,GenerateStreamingPages=true};
            string path=AssetDatabase.GetAssetPath(asset);
            this.ShowResult("変換中。build・encode はバックグラウンドで進みます。",HelpBoxMessageType.Info);
            VirtualGeometryConversionRunner.Enqueue(mesh,path,settings,true,(converted,error)=>{
                if(converted==null){this.ShowResult("変換を反映しませんでした: "+error,HelpBoxMessageType.Error);return;}
                this.ShowResult("変換完了: "+converted.NodeCount+" nodes / "+converted.StreamingPageCount+" pages",HelpBoxMessageType.Info);
            });
            this.Refresh();
        }

        private void Place(){
            GameObject placed=VirtualGeometryConversionService.PlaceRenderer((VirtualGeometryMeshAsset)this.target,Selection.activeGameObject);
            Selection.activeGameObject=placed;
            this.ShowResult("配置しました。root準備中は低密度Proxyを表示します。",HelpBoxMessageType.Info);
        }

        private void ClearCache(){
            bool deleted=VirtualGeometryConversionService.DeleteReferencedPage((VirtualGeometryMeshAsset)this.target);
            if(deleted){this.ShowResult("参照pageを削除しました。Proxyを表示し、次の変換で再生成します。",HelpBoxMessageType.Info);}
            else{this.ShowResult("削除対象pageがありません。",HelpBoxMessageType.Info);}
        }

        private void ShowResult(string text,HelpBoxMessageType type){
            if(this.result==null){return;}
            this.result.text=text;this.result.messageType=type;this.result.style.display=DisplayStyle.Flex;
        }

        private static string Describe(VirtualGeometryMeshAsset asset){
            var text=new StringBuilder();
            text.Append("入力 ").Append(asset.InputTriangleCount.ToString("N0")).Append(" / leaf ").Append(asset.LeafTriangleCount.ToString("N0")).Append(" triangles\n");
            text.Append("cluster ").Append(asset.ClusterCount).Append(" / node ").Append(asset.NodeCount).Append(" / material ").Append(asset.MaterialCount).Append('\n');
            text.Append("page ").Append(asset.StreamingPageCount).Append(" / geometry ").Append(asset.GeometryBytes.ToString("N0")).Append(" B / slot ").Append(asset.SlotGeometryBytes.ToString("N0")).Append(" B\n");
            text.Append("Proxy ").Append(asset.ProxyTriangleCount).Append(" triangles / 誤差 ").Append(asset.ProxyGeometricError.ToString("G5")).Append('\n');
            text.Append("format v").Append(asset.FormatVersion).Append(" / builder ").Append(asset.BuilderVersion).Append(" / ").Append(asset.SettingsHash);
            return text.ToString();
        }
    }
}
