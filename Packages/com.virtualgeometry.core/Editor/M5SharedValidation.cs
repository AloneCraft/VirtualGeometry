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
    /// <summary>M5の共有geometry、root bootstrap Proxy、camera別選択、退場時refcountを実GPUで確認する。</summary>
    public static class M5SharedValidation{
        private const string Root="Assets/VirtualizedGeometrySample/M5";private const int Width=320;private const int Height=240;
        private static Camera camera;private static Camera secondCamera;private static VirtualGeometryRenderer first;private static VirtualGeometryRenderer second;
        private static VirtualGeometryMeshAsset asset;private static VirtualGeometryStreamingSettings settings;
        private static Color32[] cutColor;private static float[] cutDepth;
        private static GraphicsBuffer depthProbe;private static Color32[] referenceColor;private static float[] referenceDepth;
        private static int frame;private static int fallbackFrames;private static double started;
        private static readonly List<string> results=new List<string>();private static readonly List<string> errors=new List<string>();
        public static void Run(){
            Directory.CreateDirectory("TestResults/M5");results.Clear();errors.Clear();frame=0;fallbackFrames=0;started=EditorApplication.timeSinceStartup;
            Application.logMessageReceived+=Log;
            // 保存sceneは既定のLOD閾値にして距離によるLOD変化を見られるようにし、検証の間だけ閾値を固定する。
            // 閾値0で常駐範囲の最も細かい段まで描く。準備直後は常駐pageがProxyと同じ段までなので、Proxyと同じ形で切り替わることを確かめる。
            try{Setup();first.SetLodSettings(0,0);second.SetLodSettings(0,0);EditorApplication.update+=Tick;}catch(Exception error){Finish(error);}
        }
        private static void Log(string message,string stack,LogType type){if(type==LogType.Warning||type==LogType.Error||type==LogType.Exception||type==LogType.Assert){errors.Add(message);}}
        private static void Setup(){
            Directory.CreateDirectory(Root);AssetDatabase.Refresh();
            if(VirtualGeometryValidationModel.SourceMesh("Assets/VirtualizedGeometrySample/M4/Source.obj")==null){throw new InvalidOperationException("Run M4Validation once to generate the source fixture.");}
            // M4の元Meshを、M5の共有検証用の設定で別モデルとして変換する。
            asset=VirtualGeometryValidationModel.Import(VirtualGeometryValidationModel.SourceMesh("Assets/VirtualizedGeometrySample/M4/Source.obj"),Root+"/Shared.obj",new VirtualGeometryModelSettings{MaxTrianglesPerCluster=32,HierarchyFanout=4});
            var renderer=AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Root+"/M5Renderer.asset");
            if(renderer==null){renderer=ScriptableObject.CreateInstance<UniversalRendererData>();AssetDatabase.CreateAsset(renderer,Root+"/M5Renderer.asset");}
            renderer.renderingMode=RenderingMode.Forward;renderer.depthPrimingMode=DepthPrimingMode.Disabled;renderer.copyDepthMode=CopyDepthMode.AfterTransparents;
            VirtualGeometryRenderFeature feature=null;VirtualGeometryDepthProbeFeature probe=null;
            foreach(var existing in renderer.rendererFeatures){if(existing is VirtualGeometryRenderFeature vg){feature=vg;}if(existing is VirtualGeometryDepthProbeFeature dp){probe=dp;}}
            if(feature==null){feature=ScriptableObject.CreateInstance<VirtualGeometryRenderFeature>();renderer.rendererFeatures.Add(feature);AssetDatabase.AddObjectToAsset(feature,renderer);}
            if(probe==null){probe=ScriptableObject.CreateInstance<VirtualGeometryDepthProbeFeature>();renderer.rendererFeatures.Add(probe);AssetDatabase.AddObjectToAsset(probe,renderer);}
            var serialized=new SerializedObject(feature);serialized.FindProperty("surfaceShader").objectReferenceValue=Shader.Find("VirtualizedGeometry/Unlit");
            serialized.FindProperty("selectionShader").objectReferenceValue=AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.virtualgeometry.core/Shaders/VirtualGeometrySelect.compute");
            serialized.ApplyModifiedPropertiesWithoutUndo();feature.Create();probe.Create();EditorUtility.SetDirty(renderer);EditorUtility.SetDirty(feature);
            var pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Root+"/M5Pipeline.asset");
            if(pipeline==null){pipeline=UniversalRenderPipelineAsset.Create(renderer);AssetDatabase.CreateAsset(pipeline,Root+"/M5Pipeline.asset");}
            pipeline.msaaSampleCount=1;pipeline.supportsHDR=false;pipeline.supportsCameraDepthTexture=true;
            GraphicsSettings.defaultRenderPipeline=pipeline;QualitySettings.renderPipeline=pipeline;QualitySettings.vSyncCount=0;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,false);PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,new[]{GraphicsDeviceType.Direct3D12});
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            camera=CreateCamera("M5 Primary",new Vector3(0,0,-20),60);secondCamera=CreateCamera("M5 Secondary",new Vector3(9,0,-10),28);
            VirtualGeometryStreamMetadata metadata;
            using(var file=File.OpenRead(VirtualGeometryPagePaths.Resolve(asset.PageRelativePath))){metadata=VirtualGeometryStreamCodec.ReadMetadata(file);}
            // 最大のpage 6個分のGPU予算。全pageは入らず、読込と退避が起きる。
            settings=new VirtualGeometryStreamingSettings{GpuGeometryBudgetBytes=metadata.MaxPageBytes*6L,CpuCacheBudgetBytes=metadata.MaxPageBytes*4L,
                TransferBytesPerFrame=1024,IoConcurrency=2,RequestQueueCapacity=8,IoDelayMilliseconds=250};
            first=CreateInstance("Shared A",-9,11);second=CreateInstance("Shared B",9,11);
            // 準備中はProxyを表示する。基準はProxy Meshの通常描画。
            GameObject proxyFirst=ProxyReference("Reference A",-9);GameObject proxySecond=ProxyReference("Reference B",9);
            depthProbe=new GraphicsBuffer(GraphicsBuffer.Target.Structured,Width*Height,4);VirtualGeometryDepthProbeFeature.Target=depthProbe;VirtualGeometryDepthProbeFeature.Width=Width;VirtualGeometryDepthProbeFeature.Height=Height;
            camera.cullingMask=1<<10;Texture2D image=M0Validation.Capture(camera,Width,Height);referenceColor=image.GetPixels32();referenceDepth=new float[Width*Height];depthProbe.GetData(referenceDepth);
            File.WriteAllBytes("TestResults/M5/SharedCoarseReference.png",image.EncodeToPNG());Object.DestroyImmediate(image);
            Object.DestroyImmediate(proxyFirst);Object.DestroyImmediate(proxySecond);
            // 準備直後のVGは起動時に固定したpageだけで描く。その常駐範囲でCPU選択した形を通常描画して、最初のVG描画の基準にする。
            int slots=VirtualGeometryStreamingController.SlotCountFor(metadata,settings);
            bool[] pinned=VirtualGeometryStreamingController.SelectPinnedPages(metadata,slots);
            VirtualGeometryPage page=VirtualGeometryPageCodec.Read(File.ReadAllBytes(VirtualGeometryPagePaths.Resolve(asset.PageRelativePath)));
            GameObject cutFirst=CutReference("Pinned cut A",first,page,pinned);GameObject cutSecond=CutReference("Pinned cut B",second,page,pinned);
            camera.cullingMask=1<<10;image=M0Validation.Capture(camera,Width,Height);cutColor=image.GetPixels32();cutDepth=new float[Width*Height];depthProbe.GetData(cutDepth);
            File.WriteAllBytes("TestResults/M5/SharedPinnedCutReference.png",image.EncodeToPNG());Object.DestroyImmediate(image);
            Object.DestroyImmediate(cutFirst.GetComponent<MeshFilter>().sharedMesh);Object.DestroyImmediate(cutFirst);Object.DestroyImmediate(cutSecond.GetComponent<MeshFilter>().sharedMesh);Object.DestroyImmediate(cutSecond);
            camera.cullingMask=1<<11;
            // sceneは利用者が開いて近づけば詳細化される既定の設定で保存し、検証用の小さい予算と読込遅延は保存の後に適用する。
            EditorSceneManager.SaveScene(camera.gameObject.scene,Root+"/M5Shared.unity");AssetDatabase.SaveAssets();
            first.SetStreamingConfiguration(VirtualGeometryStreamingMode.Streaming,settings);second.SetStreamingConfiguration(VirtualGeometryStreamingMode.Streaming,settings);
        }
        private static Camera CreateCamera(string name,Vector3 position,float fov){
            var result=new GameObject(name).AddComponent<Camera>();result.transform.position=position;result.fieldOfView=fov;result.nearClipPlane=0.1f;result.farClipPlane=200;
            result.clearFlags=CameraClearFlags.SolidColor;result.backgroundColor=Color.black;result.allowHDR=false;result.allowMSAA=false;result.cullingMask=1<<11;
            result.GetUniversalAdditionalCameraData().requiresDepthTexture=true;return result;
        }
        private static GameObject CutReference(string name,VirtualGeometryRenderer instance,VirtualGeometryPage page,bool[] pinned){
            var context=new VirtualGeometryLodSelectionContext(instance.transform.localToWorldMatrix,camera.transform.position,camera.orthographic,camera.fieldOfView,camera.orthographicSize,Height,camera.nearClipPlane,0,0);
            Mesh cut=VirtualGeometryValidationModel.CutMesh(page,VirtualGeometryLodSelector.Select(page,context,id=>pinned[(int)id],null));
            var result=new GameObject(name){layer=10};result.transform.position=instance.transform.position;result.transform.localScale=instance.transform.localScale;
            result.AddComponent<MeshFilter>().sharedMesh=cut;var visual=result.AddComponent<MeshRenderer>();
            visual.sharedMaterial=asset.ProxyLitMaterial;visual.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;
            return result;
        }
        private static GameObject ProxyReference(string name,float x){
            var result=new GameObject(name){layer=10};result.transform.position=new Vector3(x,0,0);result.transform.localScale=Vector3.one*0.5f;
            result.AddComponent<MeshFilter>().sharedMesh=asset.ProxyMesh;var visual=result.AddComponent<MeshRenderer>();
            visual.sharedMaterial=asset.ProxyLitMaterial;visual.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;
            return result;
        }
        // streamingは既定の設定のまま作る。検証用の予算と遅延はsceneを保存した後に適用する。
        private static VirtualGeometryRenderer CreateInstance(string name,float x,int layer){
            var result=new GameObject(name).AddComponent<VirtualGeometryRenderer>();result.gameObject.layer=layer;result.transform.position=new Vector3(x,0,0);result.transform.localScale=Vector3.one*0.5f;
            result.SetMeshAsset(asset);result.SetCullingSettings(false,false);result.SetProxyOptions(true,true);return result;
        }
        private static void Tick(){
            try{
                if(EditorApplication.timeSinceStartup-started>90||frame++>300){throw new TimeoutException("Shared/proxy validation timeout.");}
                Texture2D image=M0Validation.Capture(camera,Width,Height);float[] depth=new float[Width*Height];depthProbe.GetData(depth);
                int missing=0;int different=0;float maxDepth=0;Color32[] colors=image.GetPixels32();
                // Proxy表示中はProxy Mesh、VGへ切り替わった最初のframeは固定page範囲のCPU選択と比べる。
                Color32[] expectedColor=cutColor;float[] expectedDepth=cutDepth;
                if(first.IsFallbackVisible){expectedColor=referenceColor;expectedDepth=referenceDepth;}
                for(int i=0;i<depth.Length;i++){
                    if(expectedDepth[i]>1e-7f&&depth[i]<=1e-7f){missing++;}
                    if(colors[i].r!=expectedColor[i].r||colors[i].g!=expectedColor[i].g||colors[i].b!=expectedColor[i].b){different++;}
                    maxDepth=Mathf.Max(maxDepth,Mathf.Abs(depth[i]-expectedDepth[i]));
                }
                if(missing!=0||different!=0||maxDepth>1e-6f){throw new InvalidOperationException("Proxy/virtual coarse mismatch: missing="+missing+", color="+different+", depth="+maxDepth+", reason="+first.DiagnosticReason);}
                if(!first.SharesStreamingResourceWith(second)||VirtualGeometryRenderer.SharedResourceCount!=1||!ReferenceEquals(first.StreamingStatistics,second.StreamingStatistics)){throw new InvalidOperationException("Instances duplicated streaming resources.");}
                if(first.IsFallbackVisible!=second.IsFallbackVisible){throw new InvalidOperationException("Shared root publication was inconsistent.");}
                if(first.IsFallbackVisible){
                    fallbackFrames++;if(fallbackFrames==1){File.WriteAllBytes("TestResults/M5/BootstrapProxy.png",image.EncodeToPNG());}Object.DestroyImmediate(image);return;
                }
                File.WriteAllBytes("TestResults/M5/SharedVirtualRoot.png",image.EncodeToPNG());Object.DestroyImmediate(image);
                if(fallbackFrames==0){throw new InvalidOperationException("Bootstrap did not exercise Proxy.");}
                results.Add("shared instances=2, resources=1, GPU geometry="+first.DiagnosticGeometryAllocatedBytes+", I/O="+first.StreamingStatistics.IoBytes+", bootstrap Proxy frames="+fallbackFrames+", mismatch=0");
                uint[] firstCameraArguments=first.ReadbackSelectionForDiagnostics(camera).Arguments;
                first.SetCullingSettings(true,false);second.SetCullingSettings(true,false);
                image=M0Validation.Capture(secondCamera,Width,Height);Object.DestroyImmediate(image);
                uint[] preserved=first.ReadbackSelectionForDiagnostics(camera).Arguments;
                for(int i=0;i<preserved.Length;i++){if(preserved[i]!=firstCameraArguments[i]){throw new InvalidOperationException("Second camera overwrote the first camera arguments.");}}
                if(first.DiagnosticCameraStateCount!=2||second.DiagnosticCameraStateCount!=2){throw new InvalidOperationException("Camera state was not separated.");}
                results.Add("per-instance/per-camera selection states=4; earlier camera args preserved: PASS");
                first.SetFallbackOverride(false,true);second.SetFallbackOverride(true);
                image=M0Validation.Capture(camera,Width,Height);File.WriteAllBytes("TestResults/M5/ForcedFallback.png",image.EncodeToPNG());Object.DestroyImmediate(image);
                if(!first.IsFallbackVisible||!second.IsFallbackVisible){throw new InvalidOperationException("Forced fallback did not activate.");}
                Ray ray=new Ray(new Vector3(-9,0,-20),Vector3.forward);
                if(first.PhysicsProxy==null||!first.PhysicsProxy.Raycast(ray,out RaycastHit hit,100)){throw new InvalidOperationException("Proxy raycast missed.");}
                results.Add("forced unsupported/normal mode fallback: PASS; proxy triangles="+asset.ProxyTriangleCount+", proxy error="+asset.ProxyGeometricError+", raycast distance="+hit.distance);
                first.enabled=false;
                if(VirtualGeometryRenderer.SharedResourceCount!=1){throw new InvalidOperationException("First disable released a shared resource too early.");}
                second.enabled=false;
                if(VirtualGeometryRenderer.SharedResourceCount!=0){throw new InvalidOperationException("Last disable leaked a shared resource.");}
                results.Add("last-ref release and camera selection disposal: PASS");
                if(errors.Count>0){throw new InvalidOperationException(string.Join(" | ",errors));}
                Finish(null);
            }catch(Exception error){Finish(error);}
        }
        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Log;
            if(first!=null){first.SetMeshAsset(null);}if(second!=null){second.SetMeshAsset(null);}
            VirtualGeometryDepthProbeFeature.Target=null;depthProbe?.Dispose();depthProbe=null;
            string text=string.Join(Environment.NewLine,results)+Environment.NewLine;
            if(error!=null){text+="FAIL: "+error;Debug.LogException(error);}else{text+="PASS: M5 shared/proxy scope only";Debug.Log(text);}
            File.WriteAllText("TestResults/M5/SharedProxyResults.txt",text);EditorApplication.Exit(error==null?0:1);
        }
    }
}
