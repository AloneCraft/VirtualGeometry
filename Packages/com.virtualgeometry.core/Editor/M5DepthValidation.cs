using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object=UnityEngine.Object;

namespace VirtualizedGeometry.Editor{
    /// <summary>normal mesh、transparent、camera depth、先行depth/normal設定を同じgeometryで比較する。</summary>
    public static class M5DepthValidation{
        private const string Root="Assets/VirtualizedGeometrySample/M5";private const int Width=384,Height=288;
        private static Camera camera;private static VirtualGeometryRenderer instance;private static UniversalRendererData renderer;
        private static M5DepthConsumerFeature consumer;private static GraphicsBuffer output;private static int warmup;
        private static readonly List<string> results=new List<string>();private static readonly List<string> errors=new List<string>();
        public static void Run(){
            Directory.CreateDirectory("TestResults/M5");results.Clear();errors.Clear();warmup=0;Application.logMessageReceived+=Log;
            try{Setup();EditorApplication.update+=Tick;}catch(Exception error){Finish(error);}
        }
        private static void Log(string message,string stack,LogType type){if(type==LogType.Warning||type==LogType.Error||type==LogType.Exception||type==LogType.Assert){errors.Add(message);}}
        private static void Setup(){
            renderer=AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Root+"/M5Renderer.asset");var pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Root+"/M5Pipeline.asset");
            renderer.depthPrimingMode=DepthPrimingMode.Disabled;renderer.copyDepthMode=CopyDepthMode.AfterOpaques;
            foreach(var feature in renderer.rendererFeatures){if(feature is M5DepthConsumerFeature existing){consumer=existing;}}
            if(consumer==null){consumer=ScriptableObject.CreateInstance<M5DepthConsumerFeature>();renderer.rendererFeatures.Add(consumer);AssetDatabase.AddObjectToAsset(consumer,renderer);}
            consumer.NormalPrepass=false;consumer.SetActive(true);consumer.Create();renderer.SetDirty();GraphicsSettings.defaultRenderPipeline=pipeline;QualitySettings.renderPipeline=pipeline;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            camera=new GameObject("Depth consumer camera").AddComponent<Camera>();camera.transform.position=new Vector3(0,0,-8);camera.orthographic=true;camera.orthographicSize=3.5f;
            camera.nearClipPlane=0.1f;camera.farClipPlane=40;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;camera.allowHDR=false;camera.allowMSAA=false;camera.GetUniversalAdditionalCameraData().requiresDepthTexture=true;
            instance=new GameObject("Depth virtual").AddComponent<VirtualGeometryRenderer>();instance.SetStreamingConfiguration(VirtualGeometryStreamingMode.AllResident,new VirtualGeometryStreamingSettings());
            instance.SetMeshAsset(VirtualGeometryValidationModel.Load(Root+"/LitSource.obj"));instance.SetLodSettings(float.MaxValue,0);instance.SetCullingSettings(false,false);instance.SetProxyOptions(false,false);
            Quad("Foreground",new Vector3(-2,0,-0.6f),new Vector3(1,2,1),Color.red);Quad("Background",new Vector3(2,0,0.6f),new Vector3(1,2,1),Color.green);
            var transparent=GameObject.CreatePrimitive(PrimitiveType.Quad);transparent.name="Transparent camera depth sample";transparent.transform.position=new Vector3(0,0,-0.3f);transparent.transform.localScale=new Vector3(4,2,1);
            transparent.GetComponent<MeshRenderer>().sharedMaterial=new Material(Shader.Find("Hidden/VirtualizedGeometry/M5DepthConsumer"));
            output=new GraphicsBuffer(GraphicsBuffer.Target.Structured,Width*Height,16);M5DepthConsumerFeature.Output=output;M5DepthConsumerFeature.Width=Width;M5DepthConsumerFeature.Height=Height;
            AssetDatabase.SaveAssets();
        }
        private static void Quad(string name,Vector3 position,Vector3 scale,Color color){var go=GameObject.CreatePrimitive(PrimitiveType.Quad);go.name=name;go.transform.position=position;go.transform.localScale=scale;var material=new Material(Shader.Find("Universal Render Pipeline/Unlit"));material.SetColor("_BaseColor",color);go.GetComponent<MeshRenderer>().sharedMaterial=material;}
        private static Color32[] Capture(string name,bool forced,out Vector4[] depth){
            instance.SetFallbackOverride(forced);Texture2D image=M0Validation.Capture(camera,Width,Height);File.WriteAllBytes("TestResults/M5/"+name+".png",image.EncodeToPNG());Color32[] colors=image.GetPixels32();Object.DestroyImmediate(image);depth=new Vector4[Width*Height];output.GetData(depth);return colors;
        }
        private static void Check(string name,bool expectedFallback,bool normals){
            Color32[] reference=Capture(name+"Reference",true,out var referenceDepth);Color32[] actual=Capture(name,false,out var depth);
            int missing=0,colorDifferent=0,normalsCovered=0;float activeDifference=0,copiedDifference=0;
            for(int i=0;i<depth.Length;i++){
                activeDifference=Mathf.Max(activeDifference,Mathf.Abs(referenceDepth[i].x-depth[i].x));copiedDifference=Mathf.Max(copiedDifference,Mathf.Abs(depth[i].x-depth[i].y));
                if(referenceDepth[i].x>1e-7f&&depth[i].x<=1e-7f){missing++;}if(depth[i].z>0.5f){normalsCovered++;}
                if(reference[i].r!=actual[i].r||reference[i].g!=actual[i].g||reference[i].b!=actual[i].b){colorDifferent++;}
            }
            results.Add(name+": fallback="+instance.IsFallbackVisible+", activeDepthDifference="+activeDifference+", cameraDepthDifference="+copiedDifference+", missing="+missing+", colorDifference="+colorDifferent+", normalsCoverage="+normalsCovered+", reason="+instance.DiagnosticReason);
            if(instance.IsFallbackVisible!=expectedFallback||missing!=0||activeDifference>1e-6f||copiedDifference>1e-6f||colorDifferent!=0||(normals&&normalsCovered<100)){throw new InvalidOperationException(name+" depth integration mismatch.");}
        }
        private static void Tick(){
            if(++warmup<5){EditorApplication.QueuePlayerLoopUpdate();return;}
            try{
                Check("DepthCopyAfterOpaques",false,false);
                var overlayObject=new GameObject("Unsupported stack overlay");overlayObject.SetActive(false);var overlay=overlayObject.AddComponent<Camera>();overlay.GetUniversalAdditionalCameraData().renderType=CameraRenderType.Overlay;
                camera.GetUniversalAdditionalCameraData().cameraStack.Add(overlay);Check("CameraStackFallback",true,false);camera.GetUniversalAdditionalCameraData().cameraStack.Clear();Object.DestroyImmediate(overlayObject);Check("RestoredIndependentBase",false,false);
                renderer.copyDepthMode=CopyDepthMode.AfterTransparents;renderer.SetDirty();Check("DepthCopyAfterTransparents",false,false);
                renderer.copyDepthMode=CopyDepthMode.ForcePrepass;renderer.SetDirty();Check("DepthOnlyFallback",true,false);
                renderer.copyDepthMode=CopyDepthMode.AfterOpaques;renderer.depthPrimingMode=DepthPrimingMode.Forced;renderer.SetDirty();Check("DepthPrimingFallback",true,false);
                renderer.depthPrimingMode=DepthPrimingMode.Disabled;consumer.NormalPrepass=true;consumer.Create();renderer.SetDirty();Check("DepthNormalsFallback",true,true);
                consumer.NormalPrepass=false;consumer.Create();renderer.SetDirty();
                VirtualGeometryRenderFeature vgFeature=null;foreach(var feature in renderer.rendererFeatures){if(feature is VirtualGeometryRenderFeature vg){vgFeature=vg;}}
                var featureSettings=new SerializedObject(vgFeature);var compute=featureSettings.FindProperty("selectionShader").objectReferenceValue;featureSettings.FindProperty("selectionShader").objectReferenceValue=null;featureSettings.ApplyModifiedPropertiesWithoutUndo();vgFeature.Create();renderer.SetDirty();
                Check("MissingComputeFallback",true,false);
                featureSettings.Update();featureSettings.FindProperty("selectionShader").objectReferenceValue=compute;featureSettings.ApplyModifiedPropertiesWithoutUndo();vgFeature.Create();renderer.SetDirty();Check("RestoredComputeVirtual",false,false);
                var parent=new GameObject("Sheared parent");parent.transform.localScale=new Vector3(1.2f,.8f,1);instance.transform.SetParent(parent.transform,false);instance.transform.localRotation=Quaternion.Euler(0,0,25);Check("ShearFallback",true,false);
                instance.transform.SetParent(null,false);instance.transform.localRotation=Quaternion.identity;Check("RestoredTrsVirtual",false,false);Object.DestroyImmediate(parent);
                if(errors.Count>0){throw new InvalidOperationException(string.Join(" | ",errors));}Finish(null);
            }catch(Exception error){Finish(error);}
        }
        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Log;instance?.SetMeshAsset(null);M5DepthConsumerFeature.Output=null;output?.Dispose();output=null;
            if(consumer!=null){consumer.NormalPrepass=false;consumer.SetActive(false);consumer.Create();}if(renderer!=null){renderer.depthPrimingMode=DepthPrimingMode.Disabled;renderer.copyDepthMode=CopyDepthMode.AfterOpaques;renderer.SetDirty();}
            string text=string.Join(Environment.NewLine,results)+Environment.NewLine;if(error!=null){text+="FAIL: "+error;Debug.LogException(error);}else{text+="PASS: depth integration scope only";Debug.Log(text);}File.WriteAllText("TestResults/M5/DepthResults.txt",text);EditorApplication.Exit(error==null?0:1);
        }
    }
}