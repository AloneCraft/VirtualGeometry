using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object=UnityEngine.Object;

namespace VirtualizedGeometry.Editor{
    /// <summary>DX12 Development Playerの実ビルドとpacked asset収録検査。</summary>
    public static class M5PlayerBuildValidation{
        private const string Root="Assets/VirtualizedGeometrySample/M5";
        public static void Run(){
            Directory.CreateDirectory("TestResults/M5/PlayerBuild");
            try{
                Setup();
                var options=new BuildPlayerOptions{scenes=new[]{Root+"/M5Player.unity"},locationPathName="Builds/M5Player/VirtualGeometryM5.exe",target=BuildTarget.StandaloneWindows64,options=BuildOptions.Development|BuildOptions.DetailedBuildReport};
                BuildReport report=VirtualGeometryPlayerBuild.Build(options);
                File.WriteAllLines("TestResults/M5/PlayerBuild/BuildMessages.txt",report.steps.SelectMany(step=>step.messages.Select(message=>step.name+": "+message.type+": "+message.content)));
                var entries=new List<string>();var sourcePaths=new HashSet<string>();
                foreach(var packed in report.packedAssets){foreach(var item in packed.contents){entries.Add(packed.shortPath+","+item.sourceAssetPath+","+item.type+","+item.packedSize);sourcePaths.Add(item.sourceAssetPath);}}
                File.WriteAllLines("TestResults/M5/PlayerBuild/PackedAssets.csv",new[]{"packed_file,source_asset,type,packed_bytes"}.Concat(entries));
                string[] files=Directory.GetFiles("Builds/M5Player","*",SearchOption.AllDirectories);
                string[] archives=files.Where(path=>path.EndsWith(".vgpage",StringComparison.OrdinalIgnoreCase)).ToArray();
                string[] forbidden=files.Where(VirtualGeometryBuildInclusion.IsForbiddenPlayerPath).ToArray();
                bool highSourceIncluded=sourcePaths.Contains("Assets/VirtualizedGeometrySample/M4/Source.asset");
                string text="BuildResult="+report.summary.result+"\nBytes="+report.summary.totalSize+"\nDuration="+report.summary.totalTime+"\nWarnings="+report.summary.totalWarnings+"\nErrors="+report.summary.totalErrors+"\nArchives="+archives.Length+"\nHigh source packed="+highSourceIncluded+"\nForbidden native/source files="+forbidden.Length+"\n"+string.Join("\n",archives)+"\n";
                File.WriteAllText("TestResults/M5/PlayerBuild/BuildReport.txt",text);File.WriteAllText("TestResults/M5/PlayerBuild/Environment.txt","Unity="+Application.unityVersion+"\nGPU="+SystemInfo.graphicsDeviceName+"\nAPI="+SystemInfo.graphicsDeviceType+"\nOS="+SystemInfo.operatingSystem+"\nEditor archive restored="+Directory.Exists("Assets/StreamingAssets/VirtualGeometry")+"\n");
                if(report.summary.result!=BuildResult.Succeeded||report.summary.totalErrors!=0||report.summary.totalWarnings!=0||archives.Length!=1||highSourceIncluded||forbidden.Length>0){throw new InvalidOperationException("Player build/inclusion validation failed.\n"+text);}
                Debug.Log(text+"PASS: Player build and inclusion scope");EditorApplication.Exit(0);
            }catch(Exception error){File.WriteAllText("TestResults/M5/PlayerBuild/Failure.txt",error.ToString());Debug.LogException(error);EditorApplication.Exit(1);}
        }
        public static void InspectLastReport(){var report=BuildReport.GetLatestReport();File.WriteAllLines("TestResults/M5/PlayerBuild/BuildMessages.txt",report.steps.SelectMany(step=>step.messages.Select(message=>step.name+": "+message.type+": "+message.content)));Debug.Log("Build warning messages exported.");EditorApplication.Exit(0);}
        private static void Setup(){
            VirtualGeometryMeshAsset asset=AssetDatabase.LoadAssetAtPath<VirtualGeometryMeshAsset>(Root+"/Converted.asset");if(asset==null){throw new InvalidOperationException("M5 converted fixture is missing.");}
            var renderer=AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Root+"/PlayerRenderer.asset");if(renderer==null){renderer=ScriptableObject.CreateInstance<UniversalRendererData>();AssetDatabase.CreateAsset(renderer,Root+"/PlayerRenderer.asset");}
            renderer.renderingMode=RenderingMode.Forward;renderer.copyDepthMode=CopyDepthMode.AfterOpaques;renderer.depthPrimingMode=DepthPrimingMode.Disabled;
            VirtualGeometryRenderFeature feature=renderer.rendererFeatures.OfType<VirtualGeometryRenderFeature>().FirstOrDefault();if(feature==null){feature=ScriptableObject.CreateInstance<VirtualGeometryRenderFeature>();renderer.rendererFeatures.Add(feature);AssetDatabase.AddObjectToAsset(feature,renderer);}
            var settings=new SerializedObject(feature);settings.FindProperty("surfaceShader").objectReferenceValue=Shader.Find("VirtualizedGeometry/Unlit");settings.FindProperty("depthCopyShader").objectReferenceValue=Shader.Find("Hidden/Universal Render Pipeline/CopyDepth");settings.FindProperty("selectionShader").objectReferenceValue=AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.virtualgeometry.core/Shaders/VirtualGeometrySelect.compute");settings.ApplyModifiedPropertiesWithoutUndo();feature.Create();EditorUtility.SetDirty(feature);EditorUtility.SetDirty(renderer);
            var pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Root+"/PlayerPipeline.asset");if(pipeline==null){pipeline=UniversalRenderPipelineAsset.Create(renderer);AssetDatabase.CreateAsset(pipeline,Root+"/PlayerPipeline.asset");}
            pipeline.msaaSampleCount=1;pipeline.supportsHDR=false;pipeline.supportsCameraDepthTexture=true;pipeline.maxAdditionalLightsCount=8;pipeline.shadowDistance=50;pipeline.shadowCascadeCount=1;
            var serialized=new SerializedObject(pipeline);serialized.FindProperty("m_MainLightShadowsSupported").boolValue=true;serialized.FindProperty("m_AdditionalLightsRenderingMode").intValue=(int)LightRenderingMode.PerPixel;serialized.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(pipeline);
            GraphicsSettings.defaultRenderPipeline=pipeline;QualitySettings.renderPipeline=pipeline;QualitySettings.vSyncCount=0;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,false);PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,new[]{GraphicsDeviceType.Direct3D12});PlayerSettings.defaultScreenWidth=640;PlayerSettings.defaultScreenHeight=480;PlayerSettings.fullScreenMode=FullScreenMode.Windowed;PlayerSettings.runInBackground=true;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=Color.black;RenderSettings.ambientIntensity=0;RenderSettings.reflectionIntensity=0;RenderSettings.ambientProbe=new SphericalHarmonicsL2();RenderSettings.fog=false;
            Camera primary=Camera("Player perspective",new Vector3(0,0,-20),false);Camera secondary=Camera("Player orthographic",new Vector3(9,0,-20),true);secondary.orthographicSize=11;
            Light light=new GameObject("Main directional").AddComponent<Light>();light.type=LightType.Directional;light.transform.rotation=Quaternion.Euler(20,-25,0);light.intensity=1;light.shadows=LightShadows.Hard;RenderSettings.sun=light;
            var stream=new VirtualGeometryStreamingSettings{GpuGeometryBudgetBytes=asset.SlotGeometryBytes*6L,CpuCacheBudgetBytes=asset.SlotGeometryBytes*4L,TransferBytesPerFrame=1024,IoConcurrency=2,RequestQueueCapacity=8,IoDelayMilliseconds=300};
            var first=Instance("Player shared A",asset,-9,stream);var second=Instance("Player shared B",asset,9,stream);
            // Mesh Asset Inspectorの「選択GameObjectに配置」と同じsource参照。build cloneだけで除去されることを検証する。
            first.gameObject.AddComponent<MeshFilter>().sharedMesh=AssetDatabase.LoadAssetAtPath<Mesh>("Assets/VirtualizedGeometrySample/M4/Source.asset");var original=first.gameObject.AddComponent<MeshRenderer>();original.sharedMaterial=asset.ProxyUnlitMaterial;original.enabled=false;
            var runner=new GameObject("M5 Player validation").AddComponent<VirtualGeometryPlayerValidation>();runner.First=first;runner.Second=second;runner.Primary=primary;runner.Secondary=secondary;runner.GpuBudget=stream.GpuGeometryBudgetBytes;runner.CpuBudget=stream.CpuCacheBudgetBytes;runner.TransferCap=stream.TransferBytesPerFrame;runner.IoCap=stream.IoConcurrency;
            EditorSceneManager.SaveScene(primary.gameObject.scene,Root+"/M5Player.unity");AssetDatabase.SaveAssets();VirtualGeometryRenderer.ReleaseResourcesForReload();
        }
        private static Camera Camera(string name,Vector3 position,bool orthographic){var camera=new GameObject(name).AddComponent<Camera>();camera.transform.position=position;camera.fieldOfView=60;camera.orthographic=orthographic;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;camera.nearClipPlane=0.1f;camera.farClipPlane=200;camera.allowHDR=false;camera.allowMSAA=false;camera.GetUniversalAdditionalCameraData().requiresDepthTexture=true;return camera;}
        private static VirtualGeometryRenderer Instance(string name,VirtualGeometryMeshAsset asset,float x,VirtualGeometryStreamingSettings settings){var renderer=new GameObject(name).AddComponent<VirtualGeometryRenderer>();renderer.transform.position=new Vector3(x,0,0);renderer.transform.localScale=Vector3.one*0.5f;renderer.SetStreamingConfiguration(VirtualGeometryStreamingMode.Streaming,settings);renderer.SetMeshAsset(asset);renderer.SetLodSettings(float.MaxValue,0);renderer.SetCullingSettings(false,false);renderer.SetProxyOptions(false,true);return renderer;}
    }
}