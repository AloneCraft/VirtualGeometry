using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Debug=UnityEngine.Debug;
using Object=UnityEngine.Object;

namespace VirtualizedGeometry.Editor{
    /// <summary>M0サンプルの生成と実GPU画像検証。batchmodeでは他プロジェクトに触れない。</summary>
    public static class M0Validation{
        private const string Root="Assets/VirtualizedGeometrySample";
        private static Camera camera;
        private static double started;
        private static int warmup;
        private static readonly List<string> checks=new List<string>();

        [MenuItem("Tools/Virtualized Geometry/Create M0 Validation Scene")]
        public static void CreateSample(){
            if(!Application.isBatchMode&&!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()){
                return;
            }
            Directory.CreateDirectory(Root);
            AssetDatabase.Refresh();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            UniversalRendererData renderer=AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Root+"/M0Renderer.asset");
            if(renderer==null){
                renderer=ScriptableObject.CreateInstance<UniversalRendererData>();
                renderer.renderingMode=RenderingMode.Forward;
                renderer.depthPrimingMode=DepthPrimingMode.Disabled;
                renderer.copyDepthMode=CopyDepthMode.AfterTransparents;
                AssetDatabase.CreateAsset(renderer,Root+"/M0Renderer.asset");
            }
            // Featureが未実装でも基準シーンを生成でき、画像検証の失敗を観測できる。
            Type featureType=Type.GetType("VirtualizedGeometry.Diagnostics.IndirectTriangleFeature, VirtualizedGeometry.Runtime");
            if(featureType!=null&&renderer.rendererFeatures.Count==0){
                ScriptableRendererFeature feature=(ScriptableRendererFeature)ScriptableObject.CreateInstance(featureType);
                feature.name="Virtual Geometry M0 Indirect Probe";
                SerializedObject settings=new SerializedObject(feature);
                settings.FindProperty("generator").objectReferenceValue=AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.virtualgeometry.core/Shaders/M0Generate.compute");
                settings.FindProperty("surfaceShader").objectReferenceValue=AssetDatabase.LoadAssetAtPath<Shader>("Packages/com.virtualgeometry.core/Shaders/M0Indirect.shader");
                settings.ApplyModifiedPropertiesWithoutUndo();
                renderer.rendererFeatures.Add(feature);
                AssetDatabase.AddObjectToAsset(feature,renderer);
                EditorUtility.SetDirty(renderer);
            }
            UniversalRenderPipelineAsset pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Root+"/M0Pipeline.asset");
            if(pipeline==null){
                pipeline=UniversalRenderPipelineAsset.Create(renderer);
                pipeline.msaaSampleCount=1;
                pipeline.supportsHDR=false;
                pipeline.supportsCameraDepthTexture=true;
                pipeline.renderScale=1;
                AssetDatabase.CreateAsset(pipeline,Root+"/M0Pipeline.asset");
            }
            GraphicsSettings.defaultRenderPipeline=pipeline;
            QualitySettings.renderPipeline=pipeline;
            QualitySettings.vSyncCount=0;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,new[]{GraphicsDeviceType.Direct3D12});
            camera=new GameObject("M0 Validation Camera").AddComponent<Camera>();
            camera.transform.position=new Vector3(0,0,-5);
            camera.clearFlags=CameraClearFlags.SolidColor;
            camera.backgroundColor=new Color(0.02f,0.03f,0.05f,1);
            camera.orthographic=true;
            camera.orthographicSize=2;
            camera.nearClipPlane=0.1f;
            camera.farClipPlane=20;
            camera.allowHDR=false;
            camera.allowMSAA=false;
            camera.GetUniversalAdditionalCameraData().requiresDepthTexture=true;
            MakeQuad("Foreground - red",new Vector3(-0.65f,-0.3f,-1),new Vector3(0.5f,0.5f,1),Color.red);
            MakeQuad("Background - green",new Vector3(0.65f,-0.3f,1),new Vector3(0.5f,0.5f,1),Color.green);
            EditorSceneManager.SaveScene(camera.gameObject.scene,Root+"/M0.unity");
            EditorBuildSettings.scenes=new[]{new EditorBuildSettingsScene(Root+"/M0.unity",true)};
            AssetDatabase.SaveAssets();
        }

        private static void MakeQuad(string name,Vector3 position,Vector3 scale,Color color){
            GameObject quad=GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name=name;
            quad.transform.position=position;
            quad.transform.localScale=scale;
            Object.DestroyImmediate(quad.GetComponent<Collider>());
            string path=Root+"/"+name.Split(' ')[0]+".mat";
            Material material=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(material==null){
                material=new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                material.SetColor("_BaseColor",color);
                AssetDatabase.CreateAsset(material,path);
            }
            quad.GetComponent<MeshRenderer>().sharedMaterial=material;
        }

        /// <summary>-executeMethodから呼び出し、GPU付きbatchmodeで結果とPNGを保存する。</summary>
        public static void Run(){
            Directory.CreateDirectory("TestResults/M0");
            started=EditorApplication.timeSinceStartup;
            warmup=0;
            checks.Clear();
            try{
                CreateSample();
                WriteEnvironment();
                EditorApplication.update+=Tick;
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void Tick(){
            if(EditorApplication.timeSinceStartup-started>90){
                Finish(new TimeoutException("URP rendering did not become ready within 90 seconds."));
                return;
            }
            try{
                if(++warmup<8){
                    EditorApplication.QueuePlayerLoopUpdate();
                    return;
                }
                Texture2D image=Capture(camera,640,640);
                File.WriteAllBytes("TestResults/M0/Color.png",image.EncodeToPNG());
                AssertColor(image,320,320,new Color(0.05f,0.65f,0.95f,1),"GPU indirect triangle at center");
                AssertColor(image,216,272,Color.red,"ordinary foreground remains in front");
                AssertColor(image,424,272,new Color(0.05f,0.65f,0.95f,1),"GPU triangle occludes ordinary background");
                Object.DestroyImmediate(image);
                image=Capture(camera,960,540);
                File.WriteAllBytes("TestResults/M0/Resized.png",image.EncodeToPNG());
                AssertColor(image,480,270,new Color(0.05f,0.65f,0.95f,1),"resized camera retains indirect draw");
                Object.DestroyImmediate(image);
                Finish(null);
            }catch(Exception error){
                Finish(error);
            }
        }

        /// <summary>実際のURP StandardRequestで描画する。同期ReadPixelsはテスト専用。</summary>
        public static Texture2D Capture(Camera source,int width,int height){
            RenderTexture target=RenderTexture.GetTemporary(width,height,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear,1);
            RenderTexture previous=RenderTexture.active;
            RenderTexture oldTarget=source.targetTexture;
            try{
                source.targetTexture=target;
                RenderPipeline.StandardRequest request=new RenderPipeline.StandardRequest{destination=target};
                if(!RenderPipeline.SupportsRenderRequest(source,request)){
                    throw new InvalidOperationException("Active pipeline does not support StandardRequest.");
                }
                RenderPipeline.SubmitRenderRequest(source,request);
                RenderTexture.active=target;
                Texture2D result=new Texture2D(width,height,TextureFormat.RGBA32,false,true);
                result.ReadPixels(new Rect(0,0,width,height),0,0);
                result.Apply();
                return result;
            }finally{
                source.targetTexture=oldTarget;
                RenderTexture.active=previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        private static void AssertColor(Texture2D image,int x,int y,Color expected,string label){
            Color actual=image.GetPixel(x,y);
            if(Mathf.Abs(actual.r-expected.r)>0.06f||Mathf.Abs(actual.g-expected.g)>0.06f||Mathf.Abs(actual.b-expected.b)>0.06f){
                throw new InvalidOperationException(label+": expected "+expected+", actual "+actual+" at "+x+","+y);
            }
            checks.Add(label+": PASS");
        }

        private static void WriteEnvironment(){
            File.WriteAllText("TestResults/M0/Environment.txt",
                "UTC="+DateTime.UtcNow.ToString("O")+"\nUnity="+Application.unityVersion+"\nOS="+SystemInfo.operatingSystem+
                "\nGPU="+SystemInfo.graphicsDeviceName+"\nGraphics="+SystemInfo.graphicsDeviceVersion+"\nAPI="+SystemInfo.graphicsDeviceType+
                "\nGPU memory MiB="+SystemInfo.graphicsMemorySize+"\nCompute="+SystemInfo.supportsComputeShaders+
                "\nIndirect="+SystemInfo.supportsIndirectArgumentsBuffer+"\nAsyncReadback="+SystemInfo.supportsAsyncGPUReadback+
                "\nReversedZ="+SystemInfo.usesReversedZBuffer+"\nShaderLevel="+SystemInfo.graphicsShaderLevel+
                "\nVSync="+QualitySettings.vSyncCount+"\nMSAA=1\nRenderGraph=enabled\n");
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;
            string result=string.Join("\n",checks)+"\n";
            int exitCode=0;
            if(error!=null){
                result+="FAIL: "+error;
                Debug.LogException(error);
                exitCode=1;
            }else{
                result+="PASS\n";
                Debug.Log("VG M0 PASS");
            }
            File.WriteAllText("TestResults/M0/Results.txt",result);
            EditorApplication.Exit(exitCode);
        }
    }
}
