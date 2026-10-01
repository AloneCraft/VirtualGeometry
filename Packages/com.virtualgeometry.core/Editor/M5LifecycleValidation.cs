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
    /// <summary>実Play/StopをDomain Reload無効で反復し、共有lease・camera state・診断recorderを再初期化する。</summary>
    public static class M5LifecycleValidation{
        private const string Root="Assets/VirtualizedGeometrySample/M5";private static int stage,ticks,cycles;private static bool oldEnabled,finishing;private static EnterPlayModeOptions oldOptions;
        private static double started;private static SceneView sceneView;private static EditorWindow gameView;private static Exception failure;
        private static readonly List<string> checks=new List<string>(),errors=new List<string>();
        public static void Run(){
            Directory.CreateDirectory("TestResults/M5");stage=0;ticks=0;cycles=0;finishing=false;failure=null;checks.Clear();errors.Clear();started=EditorApplication.timeSinceStartup;
            oldEnabled=EditorSettings.enterPlayModeOptionsEnabled;oldOptions=EditorSettings.enterPlayModeOptions;Application.logMessageReceived+=Log;
            try{
                var pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Root+"/M5Pipeline.asset");GraphicsSettings.defaultRenderPipeline=pipeline;QualitySettings.renderPipeline=pipeline;
                var data=AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Root+"/M5Renderer.asset");data.depthPrimingMode=DepthPrimingMode.Disabled;data.copyDepthMode=CopyDepthMode.AfterOpaques;foreach(var f in data.rendererFeatures){if(f is M5DepthConsumerFeature d){d.SetActive(false);}if(f is VirtualGeometryRenderFeature vg){vg.Create();}}data.SetDirty();
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                var asset=VirtualGeometryValidationModel.Load(Root+"/Shared.obj");
                for(int i=0;i<2;i++){
                    var go=new GameObject("Lifecycle "+i);go.SetActive(false);go.transform.position=new Vector3(i==0?-9:9,0,0);go.transform.localScale=Vector3.one*.5f;var renderer=go.AddComponent<VirtualGeometryRenderer>();renderer.SetStreamingConfiguration(VirtualGeometryStreamingMode.AllResident,new VirtualGeometryStreamingSettings());renderer.SetMeshAsset(asset);renderer.SetProxyOptions(false,false);
                }
                if(VirtualGeometryRenderer.SharedResourceCount!=0){throw new InvalidOperationException("Inactive configuration allocated GPU resources.");}checks.Add("inactive configuration: shared resources=0");
                foreach(var renderer in Object.FindObjectsByType<VirtualGeometryRenderer>(FindObjectsInactive.Include)){renderer.gameObject.SetActive(true);}
                if(VirtualGeometryRenderer.SharedResourceCount!=1){throw new InvalidOperationException("Active instances did not share one resource.");}
                var camera=new GameObject("Lifecycle Game camera").AddComponent<Camera>();camera.tag="MainCamera";camera.transform.position=new Vector3(0,0,-20);camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;camera.allowMSAA=false;camera.allowHDR=false;camera.GetUniversalAdditionalCameraData().requiresDepthTexture=true;
                EditorSceneManager.SaveScene(camera.gameObject.scene,Root+"/M5Lifecycle.unity");AssetDatabase.SaveAssets();
                gameView=EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView"));gameView.Show();sceneView=EditorWindow.GetWindow<SceneView>();sceneView.Show();sceneView.LookAt(new Vector3(0,0,5),Quaternion.identity,25,false,true);
                EditorSettings.enterPlayModeOptionsEnabled=true;EditorSettings.enterPlayModeOptions=EnterPlayModeOptions.DisableDomainReload;
                EditorApplication.playModeStateChanged+=StateChanged;EditorApplication.update+=Tick;
            }catch(Exception error){Finish(error);}
        }
        private static void Log(string message,string stack,LogType type){if(type==LogType.Warning||type==LogType.Error||type==LogType.Exception){errors.Add(message);}}
        private static void StateChanged(PlayModeStateChange state){
            if(state==PlayModeStateChange.EnteredPlayMode){stage=2;ticks=0;}
            if(state==PlayModeStateChange.EnteredEditMode){if(finishing){Complete();}else{stage=3;ticks=0;}}
        }
        private static void Capture(Camera camera,string name){
            camera.allowMSAA=false;var image=M0Validation.Capture(camera,320,240);int colored=0;foreach(var pixel in image.GetPixels32()){if(pixel.r+pixel.g+pixel.b>6){colored++;}}File.WriteAllBytes("TestResults/M5/"+name+".png",image.EncodeToPNG());Object.DestroyImmediate(image);if(colored<100){throw new InvalidOperationException("Lifecycle image is empty: "+name);}checks.Add(name+": non-background="+colored);
        }
        private static void Tick(){
            try{
                if(finishing){return;}if(EditorApplication.timeSinceStartup-started>180){throw new TimeoutException("Lifecycle validation timed out at stage "+stage);}ticks++;gameView?.Repaint();sceneView?.Repaint();EditorApplication.QueuePlayerLoopUpdate();
                if(ticks<5){return;}
                if(stage==0){Capture(Camera.main,"LifecycleGameView");Capture(sceneView.camera,"LifecycleSceneView");checks.Add("Game View + Scene View windows open; independent camera captures: PASS");stage=1;EditorApplication.EnterPlaymode();}
                else if(stage==2){
                    var renderers=Object.FindObjectsByType<VirtualGeometryRenderer>();if(renderers.Length!=2||VirtualGeometryRenderer.SharedResourceCount!=1){throw new InvalidOperationException("Play entered with invalid shared ownership.");}
                    Capture(Camera.main,"LifecyclePlay"+cycles);foreach(var renderer in renderers){renderer.enabled=false;}if(VirtualGeometryRenderer.SharedResourceCount!=0){throw new InvalidOperationException("Disable leaked resources.");}
                    foreach(var renderer in renderers){renderer.enabled=true;}Capture(Camera.main,"LifecycleReenabled"+cycles);if(VirtualGeometryRenderer.SharedResourceCount!=1){throw new InvalidOperationException("Enable did not restore one resource.");}
                    VirtualGeometryDiagnostics.Enabled=true;Capture(Camera.main,"LifecycleDiagnostic"+cycles);_=VirtualGeometryDiagnostics.GpuMilliseconds;
                    VirtualGeometryRenderer.ReleaseResourcesForReload();if(VirtualGeometryRenderer.SharedResourceCount!=0||VirtualGeometryDiagnostics.Enabled){throw new InvalidOperationException("Reload reset retained resources/recording.");}
                    Capture(Camera.main,"LifecycleReloadBootstrap"+cycles);Capture(Camera.main,"LifecycleReloadRestored"+cycles);
                    if(VirtualGeometryRenderer.SharedResourceCount!=1){throw new InvalidOperationException("Reload did not restore shared resource.");}foreach(var renderer in renderers){if(renderer.IsFallbackVisible){throw new InvalidOperationException("Reload remained in Proxy.");}}
                    checks.Add("cycle "+cycles+": Play, disable resources=0, enable resources=1, reload resources=0->1, diagnostic reset: PASS");stage=1;EditorApplication.ExitPlaymode();
                }else if(stage==3){
                    if(VirtualGeometryRenderer.SharedResourceCount!=1){throw new InvalidOperationException("Play Stop did not restore Editor ownership.");}cycles++;if(cycles<3){stage=1;EditorApplication.EnterPlaymode();}else{Finish(null);}
                }
            }catch(Exception error){Finish(error);}
        }
        private static void Finish(Exception error){if(finishing){return;}finishing=true;failure=error;if(EditorApplication.isPlaying){EditorApplication.ExitPlaymode();return;}Complete();}
        private static void Complete(){
            EditorApplication.update-=Tick;EditorApplication.playModeStateChanged-=StateChanged;
            foreach(var renderer in Object.FindObjectsByType<VirtualGeometryRenderer>(FindObjectsInactive.Include)){renderer.enabled=false;}VirtualGeometryRenderer.ReleaseResourcesForReload();
            checks.Add("final shared resources="+VirtualGeometryRenderer.SharedResourceCount);EditorSettings.enterPlayModeOptionsEnabled=oldEnabled;EditorSettings.enterPlayModeOptions=oldOptions;
            if(gameView!=null){gameView.Close();}if(sceneView!=null){sceneView.Close();}Application.logMessageReceived-=Log;
            if(failure==null&&errors.Count>0){failure=new InvalidOperationException(string.Join(" | ",errors));}string text=string.Join(Environment.NewLine,checks)+Environment.NewLine+"PlayOptions restored="+(EditorSettings.enterPlayModeOptionsEnabled==oldEnabled&&EditorSettings.enterPlayModeOptions==oldOptions)+Environment.NewLine;
            if(failure!=null){text+="FAIL: "+failure;Debug.LogException(failure);}else{text+="PASS: 3 actual Play/Stop cycles with Domain Reload disabled";Debug.Log(text);}File.WriteAllText("TestResults/M5/LifecycleResults.txt",text);EditorApplication.Exit(failure==null?0:1);
        }
    }
}