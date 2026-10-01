using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace VirtualizedGeometry.Editor{
    /// <summary>3経路を同じsceneへ保存し、Player側で1経路だけ保持して別process比較する。</summary>
    public static class M5BenchmarkBuild{
        public static void Run(){
            int grid=724;string[] args=Environment.GetCommandLineArgs();for(int i=0;i<args.Length-1;i++){if(args[i]=="-vgGrid"){grid=int.Parse(args[i+1]);}}
            string root="Assets/VirtualizedGeometrySample/Benchmark"+grid,output="TestResults/M5/Benchmark"+grid;
            try{
                var asset=VirtualGeometryValidationModel.Load(root+"/Source.obj");var source=VirtualGeometryValidationModel.SourceMesh(root+"/Source.obj");var middle=AssetDatabase.LoadAssetAtPath<Mesh>(root+"/LodMiddle.asset");if(asset==null||source==null||middle==null){throw new InvalidOperationException("Benchmark preparation assets are missing.");}
                if(asset.GeometryBytes<=8L*1024*1024||asset.SlotGeometryBytes>8*1024*1024){throw new InvalidOperationException("Benchmark must exceed the geometry pool while its pinned root fits.");}
                var pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(root+"/Pipeline.asset");if(pipeline==null){pipeline=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/VirtualizedGeometrySample/M5/PlayerPipeline.asset"));AssetDatabase.CreateAsset(pipeline,root+"/Pipeline.asset");}
                var serialized=new SerializedObject(pipeline);serialized.FindProperty("m_MainLightShadowsSupported").boolValue=false;serialized.ApplyModifiedPropertiesWithoutUndo();pipeline.shadowDistance=0;pipeline.msaaSampleCount=1;pipeline.supportsHDR=false;pipeline.supportsCameraDepthTexture=true;EditorUtility.SetDirty(pipeline);GraphicsSettings.defaultRenderPipeline=pipeline;QualitySettings.renderPipeline=pipeline;
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,false);PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,new[]{GraphicsDeviceType.Direct3D12});PlayerSettings.enableFrameTimingStats=true;PlayerSettings.defaultScreenWidth=1920;PlayerSettings.defaultScreenHeight=1080;PlayerSettings.fullScreenMode=FullScreenMode.Windowed;PlayerSettings.runInBackground=true;
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=Color.black;RenderSettings.ambientIntensity=0;RenderSettings.ambientProbe=new SphericalHarmonicsL2();RenderSettings.reflectionIntensity=0;RenderSettings.fog=false;RenderSettings.skybox=null;
                var material=AssetDatabase.LoadAssetAtPath<Material>(root+"/Material.mat");if(material==null){material=new Material(Shader.Find("Universal Render Pipeline/Lit"));AssetDatabase.CreateAsset(material,root+"/Material.mat");}material.SetColor("_BaseColor",new Color(.65f,.4f,.2f,1));material.SetFloat("_Metallic",0);material.SetFloat("_Smoothness",.25f);EditorUtility.SetDirty(material);
                // NewSceneの未使用アセット解放で先に読んだassetは使えなくなる(MeshFilterへ代替Meshが入らない)。永続アセットを取得し直す。
                asset=VirtualGeometryValidationModel.Load(root+"/Source.obj");source=VirtualGeometryValidationModel.SourceMesh(root+"/Source.obj");middle=AssetDatabase.LoadAssetAtPath<Mesh>(root+"/LodMiddle.asset");
                var normal=MeshObject("Normal MeshRenderer",source,material);normal.SetActive(false);
                var lod=new GameObject("Automatic LODGroup");lod.SetActive(false);var group=lod.AddComponent<LODGroup>();group.fadeMode=LODFadeMode.None;var meshes=new[]{source,middle,asset.ProxyMesh};var thresholds=new[]{.75f,.4f,.001f};var levels=new LOD[3];for(int i=0;i<3;i++){var child=MeshObject("LOD"+i,meshes[i],material);child.transform.SetParent(lod.transform,false);levels[i]=new LOD(thresholds[i],new Renderer[]{child.GetComponent<MeshRenderer>()});}group.SetLODs(levels);group.RecalculateBounds();
                var virtualObject=new GameObject("Streaming Virtual Geometry");virtualObject.SetActive(false);var vg=virtualObject.AddComponent<VirtualGeometryRenderer>();var stream=new VirtualGeometryStreamingSettings{GpuGeometryBudgetBytes=8L*1024*1024,CpuCacheBudgetBytes=8L*1024*1024,IoConcurrency=2,TransferBytesPerFrame=65536,RequestQueueCapacity=64,IoDelayMilliseconds=25};vg.SetStreamingConfiguration(VirtualGeometryStreamingMode.Streaming,stream);vg.SetMeshAsset(asset);vg.SetLodSettings(2,.1f);vg.SetCullingSettings(true,true);vg.SetMaterials(material);vg.SetProxyOptions(false,false);
                var camera=new GameObject("Benchmark camera").AddComponent<Camera>();camera.transform.position=new Vector3(0,0,-240);camera.fieldOfView=60;camera.nearClipPlane=.1f;camera.farClipPlane=1000;camera.allowMSAA=false;camera.allowHDR=false;camera.backgroundColor=Color.black;camera.clearFlags=CameraClearFlags.SolidColor;camera.GetUniversalAdditionalCameraData().requiresDepthTexture=true;
                Light light=new GameObject("Directional light").AddComponent<Light>();light.type=LightType.Directional;light.transform.rotation=Quaternion.Euler(20,-25,0);light.intensity=1;light.shadows=LightShadows.None;RenderSettings.sun=light;
                var runner=new GameObject("Benchmark runner").AddComponent<VirtualGeometryBenchmark>();runner.Normal=normal;runner.Lod=lod;runner.Virtual=virtualObject;runner.Camera=camera;runner.InputTriangles=asset.InputTriangleCount;runner.UniqueGeometryBytes=asset.GeometryBytes;runner.GeometryBudget=stream.GpuGeometryBudgetBytes;runner.CpuBudget=stream.CpuCacheBudgetBytes;
                string scene=root+"/Benchmark.unity";EditorSceneManager.SaveScene(camera.gameObject.scene,scene);AssetDatabase.SaveAssets();
                // -vgSceneOnlyはsceneの生成だけで終える(Player buildと計測をしない再生成用)。
                if(Array.IndexOf(args,"-vgSceneOnly")>=0){Debug.Log("Benchmark scene "+grid+" generated");EditorApplication.Exit(0);return;}
                BuildReport report=VirtualGeometryPlayerBuild.Build(new BuildPlayerOptions{scenes=new[]{scene},locationPathName="Builds/Benchmark"+grid+"/VirtualGeometryBenchmark.exe",target=BuildTarget.StandaloneWindows64,options=BuildOptions.Development|BuildOptions.DetailedBuildReport});
                File.WriteAllText(output+"/BuildReport.txt","Result="+report.summary.result+"\nBytes="+report.summary.totalSize+"\nWarnings="+report.summary.totalWarnings+"\nErrors="+report.summary.totalErrors+"\nNormalTriangles="+source.triangles.Length/3+"\nMiddleTriangles="+middle.triangles.Length/3+"\nCoarseTriangles="+asset.ProxyTriangleCount+"\nLOD screen-height thresholds=0.75,0.4,0.001\nVG target=2px, pool=8388608bytes, cache=8388608bytes, I/O=2, delay=25ms, transfer=65536/frame\nSource is intentionally included for this baseline benchmark; M5Player inclusion report is separate.\n");
                File.WriteAllLines(output+"/BuildMessages.txt",report.steps.SelectMany(step=>step.messages.Select(message=>step.name+": "+message.type+": "+message.content)));
                if(report.summary.result!=BuildResult.Succeeded||report.summary.totalErrors!=0||report.summary.totalWarnings!=0){throw new InvalidOperationException("Benchmark build failed or reported a warning.");}Debug.Log("Benchmark build "+grid+" PASS");
                // 計測はbuild済みPlayerで行うため、数百MBの元モデルを普段のprojectに残さない(Editorの再インポートが長くなるため)。
                M5BenchmarkPreparation.DeleteGenerated(root);EditorApplication.Exit(0);
            }catch(Exception error){File.WriteAllText(output+"/BuildFailure.txt",error.ToString());Debug.LogException(error);EditorApplication.Exit(1);}
        }
        private static GameObject MeshObject(string name,Mesh mesh,Material material){var go=new GameObject(name);go.AddComponent<MeshFilter>().sharedMesh=mesh;var renderer=go.AddComponent<MeshRenderer>();renderer.sharedMaterial=material;renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;return go;}
    }
}
