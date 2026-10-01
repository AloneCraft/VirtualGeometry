using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace VirtualizedGeometry.Editor{
    /// <summary>
    /// 現在のURP Rendererを、Virtual Geometryの専用描画ができる設定へ揃える。
    /// 変更内容を一覧してから適用し、利用者の同意なしにpipeline設定を書き換えない。
    /// </summary>
    public static class VirtualGeometryRendererSetup{
        /// <summary>現在使われているURP pipeline assetの既定Renderer。URP以外ならnull。</summary>
        public static UniversalRendererData ActiveRendererData(out UniversalRenderPipelineAsset pipeline){
            pipeline=QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
            if(pipeline==null){pipeline=GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;}
            if(pipeline==null){return null;}
            var serialized=new SerializedObject(pipeline);
            SerializedProperty list=serialized.FindProperty("m_RendererDataList");int index=serialized.FindProperty("m_DefaultRendererIndex").intValue;
            if(list==null||index<0||index>=list.arraySize){return null;}
            return list.GetArrayElementAtIndex(index).objectReferenceValue as UniversalRendererData;
        }

        /// <summary>専用描画に必要な変更の一覧。空なら設定済み。</summary>
        public static List<string> RequiredChanges(UniversalRendererData data,UniversalRenderPipelineAsset pipeline){
            var changes=new List<string>();
            if(data==null){changes.Add("URPのRendererが見つかりません(URP以外のpipelineには対応しません)。");return changes;}
            if(FindFeature(data)==null){changes.Add("Virtual Geometry Renderer Featureを追加する");}
            if(data.renderingMode!=RenderingMode.Forward&&data.renderingMode!=RenderingMode.ForwardPlus){changes.Add("Rendering PathをDeferredからForward+へ変える");}
            if(data.depthPrimingMode!=DepthPrimingMode.Disabled){changes.Add("Depth Primingを無効にする");}
            if(data.copyDepthMode==CopyDepthMode.ForcePrepass){changes.Add("Depth Texture ModeをAfter Opaquesにする");}
            if(pipeline!=null&&pipeline.msaaSampleCount>1){changes.Add("MSAAを無効にする");}
            return changes;
        }

        public static void Apply(UniversalRendererData data,UniversalRenderPipelineAsset pipeline){
            Undo.RecordObject(data,"Configure Virtual Geometry Renderer");
            if(data.renderingMode!=RenderingMode.Forward&&data.renderingMode!=RenderingMode.ForwardPlus){data.renderingMode=RenderingMode.ForwardPlus;}
            if(data.depthPrimingMode!=DepthPrimingMode.Disabled){data.depthPrimingMode=DepthPrimingMode.Disabled;}
            if(data.copyDepthMode==CopyDepthMode.ForcePrepass){data.copyDepthMode=CopyDepthMode.AfterOpaques;}
            EnsureFeature(data);
            if(pipeline!=null&&pipeline.msaaSampleCount>1){Undo.RecordObject(pipeline,"Configure Virtual Geometry Renderer");pipeline.msaaSampleCount=1;EditorUtility.SetDirty(pipeline);}
            EditorUtility.SetDirty(data);AssetDatabase.SaveAssets();
        }

        /// <summary>Rendererにfeatureが無ければ追加し、選択compute・debug用VG Unlit・depth copyを設定する。</summary>
        public static VirtualGeometryRenderFeature EnsureFeature(UniversalRendererData data){
            VirtualGeometryRenderFeature feature=FindFeature(data);
            if(feature==null){
                feature=ScriptableObject.CreateInstance<VirtualGeometryRenderFeature>();feature.name="Virtual Geometry";
                data.rendererFeatures.Add(feature);AssetDatabase.AddObjectToAsset(feature,data);
            }
            var fields=new SerializedObject(feature);
            fields.FindProperty("selectionShader").objectReferenceValue=AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.virtualgeometry.core/Shaders/VirtualGeometrySelect.compute");
            fields.FindProperty("surfaceShader").objectReferenceValue=Shader.Find("VirtualizedGeometry/Unlit");
            fields.FindProperty("depthCopyShader").objectReferenceValue=Shader.Find("Hidden/Universal Render Pipeline/CopyDepth");
            fields.ApplyModifiedPropertiesWithoutUndo();feature.Create();EditorUtility.SetDirty(feature);EditorUtility.SetDirty(data);
            return feature;
        }

        private static VirtualGeometryRenderFeature FindFeature(UniversalRendererData data){
            foreach(ScriptableRendererFeature existing in data.rendererFeatures){if(existing is VirtualGeometryRenderFeature found){return found;}}
            return null;
        }
    }
}
