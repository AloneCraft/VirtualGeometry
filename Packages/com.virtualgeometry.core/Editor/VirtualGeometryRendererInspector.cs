using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;
namespace VirtualizedGeometry.Editor{
    /// <summary>
    /// 普段触る項目だけを常に表示し、既定値で足りる項目は「詳細」へ折りたたむ。
    /// Proxy強制・debug view・カリング切替はScene viewのVirtual Geometry toolbarで、I/O遅延などの検証専用値はAPIで扱うため表示しない。
    /// </summary>
    [CustomEditor(typeof(VirtualGeometryRenderer))]
    public sealed class VirtualGeometryRendererInspector:UnityEditor.Editor{
        public override VisualElement CreateInspectorGUI(){
            var root=new VisualElement();
            var status=new HelpBox(string.Empty,HelpBoxMessageType.Info);
            root.Add(this.Field("meshAsset"));
            root.Add(this.Field("lodErrorThresholdPixels"));
            root.Add(this.Field("materials"));
            var materialStatus=new HelpBox(string.Empty,HelpBoxMessageType.Warning);materialStatus.style.display=DisplayStyle.None;root.Add(materialStatus);
            // 対応するVirtual Geometry Shaderが無いMaterialは、.vgshaderの作成とshaderの切替を1クリックで行えるようにする。
            var fixes=new VisualElement();root.Add(fixes);string shownFixes=string.Empty;
            var details=new Foldout{text="詳細",value=false};
            details.Add(this.Field("lodHysteresis"));
            details.Add(this.Field("enableShadowProxy"));
            details.Add(this.Field("enablePhysicsProxy"));
            details.Add(this.Field("streamingMode"));
            SerializedProperty streaming=this.serializedObject.FindProperty("streamingSettings");
            foreach(string name in new[]{"GpuGeometryBudgetBytes","CpuCacheBudgetBytes","IoConcurrency","TransferBytesPerFrame","RequestQueueCapacity","RequestTimeoutFrames"}){
                details.Add(new PropertyField(streaming.FindPropertyRelative(name)));
            }
            root.Add(details);
            root.Add(status);
            // URP Rendererが専用描画できる設定でなければ、変更内容を示して1クリックで揃えられるようにする。
            var pipelineStatus=new HelpBox(string.Empty,HelpBoxMessageType.Warning);pipelineStatus.style.display=DisplayStyle.None;root.Add(pipelineStatus);
            var configure=new Button(ConfigureRenderer){text="URP RendererをVirtual Geometry用に設定",tooltip="下に表示された変更を、現在のURP pipelineの既定Rendererへ適用します。Undoで戻せます。"};configure.style.display=DisplayStyle.None;root.Add(configure);
            root.schedule.Execute(()=>{
                UniversalRendererData data=VirtualGeometryRendererSetup.ActiveRendererData(out UniversalRenderPipelineAsset pipeline);
                List<string> changes=VirtualGeometryRendererSetup.RequiredChanges(data,pipeline);
                pipelineStatus.style.display=DisplayStyle.None;configure.style.display=DisplayStyle.None;
                if(changes.Count>0){
                    pipelineStatus.text="URP Rendererが専用描画できる設定ではないため通常Proxyで表示されます。必要な変更:\n・"+string.Join("\n・",changes);
                    pipelineStatus.style.display=DisplayStyle.Flex;
                    if(data!=null){configure.style.display=DisplayStyle.Flex;}
                }
            }).Every(1000);
            root.schedule.Execute(()=>{
                if(!(this.target is VirtualGeometryRenderer renderer)){return;}
                materialStatus.style.display=DisplayStyle.None;
                if(renderer.MeshAsset!=null){
                    var problems=new System.Text.StringBuilder();
                    if(renderer.Materials==null||renderer.Materials.Length<renderer.MeshAsset.MaterialCount){problems.Append("Material数がsubmesh数("+renderer.MeshAsset.MaterialCount+")より少ないslotは白のURP Litで描きます。\n");}
                    for(int slot=0;slot<renderer.MeshAsset.MaterialCount;slot++){
                        if(!VirtualGeometryMaterials.IsSupported(renderer.GetMaterial(slot),out string reason)){problems.Append("slot ").Append(slot).Append(": ").Append(reason).Append('\n');}
                    }
                    if(problems.Length>0){materialStatus.text=problems.ToString().TrimEnd();materialStatus.style.display=DisplayStyle.Flex;}
                }
                var needed=new List<UnityEngine.Material>();
                if(renderer.MeshAsset!=null){
                    for(int slot=0;slot<renderer.MeshAsset.MaterialCount;slot++){
                        UnityEngine.Material material=renderer.GetMaterial(slot);
                        if(VirtualGeometryMaterials.NeedsVirtualShader(material)&&!needed.Contains(material)){needed.Add(material);}
                    }
                }
                var key=new System.Text.StringBuilder();foreach(UnityEngine.Material material in needed){key.Append(material.GetEntityId().ToString()).Append(',');}
                // 毎回作り直すとボタンが点滅するため、対象が変わったときだけ並べ直す。
                if(key.ToString()!=shownFixes){
                    shownFixes=key.ToString();fixes.Clear();
                    foreach(UnityEngine.Material material in needed){
                        UnityEngine.Material target=material;
                        fixes.Add(new Button(()=>CreateVirtualShader(target)){text="「"+target.name+"」用のVirtual Geometry Shaderを作成して切り替え",
                            tooltip="shader \""+target.shader.name+"\" から.vgshaderを作り、このMaterialのshaderを生成shaderへ切り替えます。Propertyの値は保たれます。Undoで戻せます。"});
                    }
                }
                var asset=renderer.MeshAsset;
                if(asset==null){status.text="Mesh を右クリック →「仮想ジオメトリに変換」で作ったアセットを Mesh Asset に割り当ててください。";status.messageType=HelpBoxMessageType.Warning;return;}
                status.messageType=HelpBoxMessageType.Info;
                status.text="入力 "+asset.InputTriangleCount+" / Proxy "+asset.ProxyTriangleCount+" triangles\nProxy誤差="+asset.ProxyGeometricError.ToString("G5")+"（簡略化器由来の幾何誤差、実測最大距離ではありません）\n"+
                    "GPU geometry="+renderer.DiagnosticGeometryAllocatedBytes+" B / camera選択="+renderer.DiagnosticSelectionAllocatedBytes+" B\n"+renderer.DiagnosticReason;
            }).Every(250);
            return root;
        }

        private static void ConfigureRenderer(){
            UniversalRendererData data=VirtualGeometryRendererSetup.ActiveRendererData(out UniversalRenderPipelineAsset pipeline);
            List<string> changes=VirtualGeometryRendererSetup.RequiredChanges(data,pipeline);
            if(data==null||changes.Count==0){return;}
            if(!EditorUtility.DisplayDialog("URP Rendererの設定",AssetDatabase.GetAssetPath(data)+" に次の変更を加えます。\n\n・"+string.Join("\n・",changes),"適用","キャンセル")){return;}
            VirtualGeometryRendererSetup.Apply(data,pipeline);
        }

        private static void CreateVirtualShader(UnityEngine.Material material){
            if(material==null){return;}
            UnityEngine.Shader source=material.shader;
            if(!EditorUtility.DisplayDialog("Virtual Geometry Shaderの作成","shader \""+source.name+"\" からVirtual Geometry Shader(.vgshader)を作り、Material \""+material.name+"\" のshaderをそれへ切り替えます。\n生成shaderは元のpassをすべて含むため、このMaterialは通常のMeshでも同じように描けます。","作成して切り替え","キャンセル")){return;}
            string path=VirtualGeometryShaderImporter.Create(source);
            UnityEngine.Shader generated=AssetDatabase.LoadAssetAtPath<UnityEngine.Shader>(path);
            if(generated==null){EditorUtility.DisplayDialog("Virtual Geometry Shaderの作成",path+" を変換できませんでした。ConsoleのVirtual Geometry Shaderのimportエラーに理由があります。","OK");return;}
            Undo.RecordObject(material,"Use Virtual Geometry Shader");
            int queue=material.renderQueue;material.shader=generated;material.renderQueue=queue;EditorUtility.SetDirty(material);
        }

        private PropertyField Field(string name){
            return new PropertyField(this.serializedObject.FindProperty(name));
        }
    }
}
