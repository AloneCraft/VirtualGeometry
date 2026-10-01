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
    /// <summary>GPU debug属性、実測統計、Proxy picking、独立Base cameraとSceneViewを比較する。</summary>
    public static class M5DebugValidation{
        private const string Root="Assets/VirtualizedGeometrySample/M5";private const int Width=480,Height=320;
        private static Camera camera,secondCamera;private static SceneView sceneView;private static VirtualGeometryRenderer first,second;
        private static VirtualGeometryMeshAsset asset;private static GraphicsBuffer depth;private static float[] baselineDepth;private static Color32[] baselineColor;
        private static int frame;private static readonly List<string> results=new List<string>();private static readonly List<string> errors=new List<string>();
        public static void Run(){
            Directory.CreateDirectory("TestResults/M5");results.Clear();errors.Clear();frame=0;Application.logMessageReceived+=Log;
            try{Setup();EditorApplication.update+=Tick;}catch(Exception error){Finish(error);}
        }
        private static void Log(string message,string stack,LogType type){if(type==LogType.Warning||type==LogType.Error||type==LogType.Exception||type==LogType.Assert){errors.Add(message);}}
        private static void Setup(){
            var pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Root+"/M5Pipeline.asset");GraphicsSettings.defaultRenderPipeline=pipeline;QualitySettings.renderPipeline=pipeline;
            var renderer=AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Root+"/M5Renderer.asset");renderer.depthPrimingMode=DepthPrimingMode.Disabled;renderer.copyDepthMode=CopyDepthMode.AfterOpaques;
            foreach(var feature in renderer.rendererFeatures){if(feature is M5DepthConsumerFeature consumer){consumer.SetActive(false);}if(feature is VirtualGeometryRenderFeature vg){vg.Create();}}
            renderer.SetDirty();EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            asset=VirtualGeometryConversionService.Convert(AssetDatabase.LoadAssetAtPath<Mesh>("Assets/VirtualizedGeometrySample/M4/Source.asset"),Root+"/Converted.asset",new VirtualGeometryBuildSettings{MaxTrianglesPerCluster=32,HierarchyFanout=4});
            camera=MakeCamera("Game Base",new Vector3(0,0,-20),60,false);secondCamera=MakeCamera("Independent ortho Base",new Vector3(8,1,-12),35,true);
            first=Instance("Debug A",-9);second=Instance("Debug B",9);VirtualGeometryDiagnostics.Enabled=true;
            depth=new GraphicsBuffer(GraphicsBuffer.Target.Structured,Width*Height,4);VirtualGeometryDepthProbeFeature.Target=depth;VirtualGeometryDepthProbeFeature.Width=Width;VirtualGeometryDepthProbeFeature.Height=Height;
        }
        private static Camera MakeCamera(string name,Vector3 position,float fov,bool ortho){var c=new GameObject(name).AddComponent<Camera>();c.transform.position=position;c.fieldOfView=fov;c.orthographic=ortho;c.orthographicSize=5;c.clearFlags=CameraClearFlags.SolidColor;c.backgroundColor=Color.black;c.nearClipPlane=0.1f;c.farClipPlane=100;c.allowMSAA=false;c.allowHDR=false;c.GetUniversalAdditionalCameraData().requiresDepthTexture=true;return c;}
        private static VirtualGeometryRenderer Instance(string name,float x){var r=new GameObject(name).AddComponent<VirtualGeometryRenderer>();r.transform.position=new Vector3(x,0,0);r.transform.localScale=Vector3.one*0.5f;r.SetStreamingConfiguration(VirtualGeometryStreamingMode.AllResident,new VirtualGeometryStreamingSettings());r.SetMeshAsset(asset);r.SetLodSettings(1,0);r.SetCullingSettings(false,false);r.SetProxyOptions(false,true);return r;}
        private static Color32[] Capture(Camera source,string name,int width=Width,int height=Height){var image=M0Validation.Capture(source,width,height);var colors=image.GetPixels32();File.WriteAllBytes("TestResults/M5/"+name+".png",image.EncodeToPNG());Object.DestroyImmediate(image);return colors;}
        private static void Tick(){
            try{
                frame++;if(frame<5){EditorApplication.QueuePlayerLoopUpdate();return;}
                if(frame==5){
                    baselineColor=Capture(camera,"DebugNone");baselineDepth=new float[Width*Height];depth.GetData(baselineDepth);
                    foreach(VirtualGeometryDebugView mode in Enum.GetValues(typeof(VirtualGeometryDebugView))){
                        if(mode==VirtualGeometryDebugView.None){continue;}first.SetDebugView(mode);second.SetDebugView(mode);var colors=Capture(camera,"Debug"+mode);var actualDepth=new float[Width*Height];depth.GetData(actualDepth);
                        int different=0,missing=0;float depthDifference=0;for(int i=0;i<colors.Length;i++){if(colors[i].r!=baselineColor[i].r||colors[i].g!=baselineColor[i].g||colors[i].b!=baselineColor[i].b){different++;}if(baselineDepth[i]>1e-7f&&actualDepth[i]<=1e-7f){missing++;}depthDifference=Mathf.Max(depthDifference,Mathf.Abs(actualDepth[i]-baselineDepth[i]));}
                        if(different<100||missing!=0||depthDifference>1e-6f){throw new InvalidOperationException("Debug view failed: "+mode);}
                        if(mode<=VirtualGeometryDebugView.ResidentPages){var values=first.ReadDebugVerticesForDiagnostics(camera);var snapshot=first.ReadbackSelectionForDiagnostics(camera);int count=0;foreach(var value in snapshot.Arguments){count++;}uint vertices=snapshot.Arguments[0];var clusters=new HashSet<int>();var levels=new HashSet<int>();float maximumError=0;
                            for(int i=0;i<vertices;i++){if(values[i].x<0||values[i].x>=asset.ClusterCount||values[i].w<0||values[i].w>=asset.StreamingPageCount){throw new InvalidOperationException("Debug GPU metadata outside range.");}clusters.Add((int)values[i].x);levels.Add((int)values[i].y);maximumError=Mathf.Max(maximumError,values[i].z);}
                            results.Add(mode+": actual GPU cluster IDs="+clusters.Count+", levels="+levels.Count+", max screen error="+maximumError);
                        }
                        results.Add(mode+": changed pixels="+different+", depth/missing=0");
                    }
                    first.SetDebugView(VirtualGeometryDebugView.None);second.SetDebugView(VirtualGeometryDebugView.None);Capture(camera,"DebugRestored");
                    var a=first.ReadMetricsForDiagnostics(camera);var b=second.ReadMetricsForDiagnostics(camera);var total=VirtualGeometryDebugMetrics.Aggregate(new[]{a,b});
                    if(total.InputTriangles!=asset.InputTriangleCount||total.UniqueTriangles!=asset.LeafTriangleCount||total.InstanceTriangles!=asset.LeafTriangleCount*2L||total.GpuGeometryBytes!=first.DiagnosticGeometryAllocatedBytes){throw new InvalidOperationException("Shared debug counters were duplicated.");}
                    results.Add("metrics input/unique/instance/drawn="+total.InputTriangles+"/"+total.UniqueTriangles+"/"+total.InstanceTriangles+"/"+total.DrawnTriangles+", selected="+total.SelectedClusters+", draws="+total.DrawCalls+", GPU geometry/metadata/selection="+total.GpuGeometryBytes+"/"+total.GpuMetadataBytes+"/"+total.GpuSelectionBytes);
                    var picked=VirtualGeometryPicking.Pick(new Ray(new Vector3(-9,0,-20),Vector3.forward),out float distance);if(picked!=first){throw new InvalidOperationException("Editor root picking failed.");}results.Add("Editor Proxy picking: PASS, distance="+distance);
                    VirtualGeometryDepthProbeFeature.Target=null;TestCameras();
                }
                Capture(camera,"TimingWarmup");if(frame<12){EditorApplication.QueuePlayerLoopUpdate();return;}
                results.Add("diagnostic VG CPU/GPU ms="+VirtualGeometryDiagnostics.CpuMilliseconds.ToString("R")+"/"+VirtualGeometryDiagnostics.GpuMilliseconds.ToString("R"));
                if(errors.Count>0){throw new InvalidOperationException(string.Join(" | ",errors));}Finish(null);
            }catch(Exception error){Finish(error);}
        }
        private static void TestCameras(){
            sceneView=EditorWindow.GetWindow<SceneView>();sceneView.Show();sceneView.LookAt(new Vector3(0,0,5),Quaternion.identity,25,false,true);
            Camera sceneCamera=sceneView.camera;if(sceneCamera==null){throw new InvalidOperationException("Scene View camera unavailable.");}sceneCamera.allowMSAA=false;
            var cameras=new[]{camera,secondCamera,sceneCamera};int index=0;
            foreach(Camera target in cameras){
                first.SetCullingSettings(false,false);second.SetCullingSettings(false,false);var reference=Capture(target,"Camera"+index+"Reference",index==1?320:480,index==1?256:320);
                first.SetCullingSettings(true,true);second.SetCullingSettings(true,true);var actual=Capture(target,"Camera"+index+"Culled",index==1?320:480,index==1?256:320);CompareExact(reference,actual,"camera "+index);
                results.Add("camera "+index+" type="+target.cameraType+", orthographic="+target.orthographic+", culling on/off color=0");index++;
            }
            uint[] earlier=first.ReadbackSelectionForDiagnostics(camera).Arguments;Capture(secondCamera,"CameraOrderOrtho",320,256);Capture(sceneCamera,"CameraOrderScene");uint[] retained=first.ReadbackSelectionForDiagnostics(camera).Arguments;
            for(int i=0;i<earlier.Length;i++){if(earlier[i]!=retained[i]){throw new InvalidOperationException("Cross-camera overwrite.");}}
            first.transform.localScale=new Vector3(-0.4f,0.6f,1.1f);first.transform.position=new Vector3(-4,-1,0.5f);camera.transform.position=new Vector3(3,2,-10);camera.fieldOfView=48;
            first.SetCullingSettings(false,false);second.SetCullingSettings(false,false);var moved=Capture(camera,"MovedReference");first.SetCullingSettings(true,true);second.SetCullingSettings(true,true);CompareExact(moved,Capture(camera,"MovedCulled"),"moved negative scale");
            results.Add("2 Base cameras + Scene View, reverse order, object movement/negative-nonuniform scale: PASS; per-instance camera states="+first.DiagnosticCameraStateCount+"/"+second.DiagnosticCameraStateCount);
        }
        private static void CompareExact(Color32[] a,Color32[] b,string name){for(int i=0;i<a.Length;i++){if(a[i].r!=b[i].r||a[i].g!=b[i].g||a[i].b!=b[i].b){throw new InvalidOperationException(name+" image differs at "+i);}}}
        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Log;VirtualGeometryDiagnostics.Enabled=false;VirtualGeometryDepthProbeFeature.Target=null;depth?.Dispose();depth=null;
            first?.SetMeshAsset(null);second?.SetMeshAsset(null);if(sceneView!=null){sceneView.Close();}string text=string.Join(Environment.NewLine,results)+Environment.NewLine;
            if(error!=null){text+="FAIL: "+error;Debug.LogException(error);}else{text+="PASS: debug/camera scope only";Debug.Log(text);}File.WriteAllText("TestResults/M5/DebugResults.txt",text);EditorApplication.Exit(error==null?0:1);
        }
    }
}