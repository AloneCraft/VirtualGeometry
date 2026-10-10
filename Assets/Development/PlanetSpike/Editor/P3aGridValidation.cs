using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using VirtualizedGeometry;
using VirtualizedGeometry.Editor;

namespace PlanetSpike.Editor{
    /// <summary>
    /// P3a: 実行時生成clusterの経路(pool・生成・bounds・cull・描画)を確かめる。
    /// 4×4 cell の平面を描き、描画されること、Frustum cull が効くこと、cull の有無で画像が変わらないことを調べる。
    /// </summary>
    internal static class P3aGridValidation{
        private const string Root="Assets/Development/PlanetSpike/P3a";
        private const string Results="TestResults/Planet/P3a";
        private static Camera camera;
        private static PlanetSpikeGridSource source;
        private static int warmup;
        private static double started;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> errors=new List<string>();

        public static void Run(){
            report.Clear();errors.Clear();warmup=0;started=EditorApplication.timeSinceStartup;
            Directory.CreateDirectory(Results);
            Application.logMessageReceived+=Record;
            try{
                Setup();
                EditorApplication.update+=Tick;
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void Record(string condition,string stack,LogType type){
            if(type==LogType.Error||type==LogType.Exception||type==LogType.Assert){errors.Add(condition);}
        }

        private static void Setup(){
            Directory.CreateDirectory(Root);AssetDatabase.Refresh();
            UniversalRendererData renderer=AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Root+"/P3aRenderer.asset");
            if(renderer==null){
                renderer=ScriptableObject.CreateInstance<UniversalRendererData>();renderer.renderingMode=RenderingMode.ForwardPlus;
                renderer.depthPrimingMode=DepthPrimingMode.Disabled;renderer.copyDepthMode=CopyDepthMode.AfterTransparents;
                AssetDatabase.CreateAsset(renderer,Root+"/P3aRenderer.asset");
            }
            VirtualGeometryRenderFeature feature=null;
            foreach(ScriptableRendererFeature existing in renderer.rendererFeatures){if(existing is VirtualGeometryRenderFeature found){feature=found;}}
            if(feature==null){
                feature=ScriptableObject.CreateInstance<VirtualGeometryRenderFeature>();feature.name="Virtual Geometry P3a";
                renderer.rendererFeatures.Add(feature);AssetDatabase.AddObjectToAsset(feature,renderer);
            }
            SerializedObject serialized=new SerializedObject(feature);
            serialized.FindProperty("selectionShader").objectReferenceValue=AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.virtualgeometry.core/Shaders/VirtualGeometrySelect.compute");
            serialized.FindProperty("surfaceShader").objectReferenceValue=Shader.Find("VirtualizedGeometry/Unlit");
            serialized.ApplyModifiedPropertiesWithoutUndo();feature.Create();
            EditorUtility.SetDirty(feature);EditorUtility.SetDirty(renderer);
            UniversalRenderPipelineAsset pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Root+"/P3aPipeline.asset");
            if(pipeline==null){pipeline=UniversalRenderPipelineAsset.Create(renderer);AssetDatabase.CreateAsset(pipeline,Root+"/P3aPipeline.asset");}
            pipeline.msaaSampleCount=1;pipeline.supportsHDR=false;pipeline.supportsCameraDepthTexture=true;pipeline.renderScale=1;
            GraphicsSettings.defaultRenderPipeline=pipeline;QualitySettings.renderPipeline=pipeline;
            AssetDatabase.SaveAssets();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            camera=new GameObject("P3a Camera").AddComponent<Camera>();
            camera.nearClipPlane=0.1f;camera.farClipPlane=500;camera.fieldOfView=60;camera.clearFlags=CameraClearFlags.SolidColor;
            camera.backgroundColor=Color.black;camera.allowHDR=false;camera.allowMSAA=false;
            // 一部のcellが視野の外に出る構図にして、Frustum cullの棄却を観測できるようにする。
            camera.transform.SetPositionAndRotation(new Vector3(8,10,-5),Quaternion.Euler(35,10,0));
            Light sun=new GameObject("P3a Sun").AddComponent<Light>();sun.type=LightType.Directional;sun.transform.rotation=Quaternion.Euler(50,30,0);
            Material material=new Material(Shader.Find("Universal Render Pipeline/Unlit"));material.SetColor("_BaseColor",new Color(0.2f,0.8f,0.3f,1));
            GameObject holder=new GameObject("P3a Grid");holder.SetActive(false);
            source=holder.AddComponent<PlanetSpikeGridSource>();
            source.Generator=AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Development/PlanetSpike/PlanetSpikeGridGenerate.compute");
            source.Material=material;
            holder.SetActive(true);
        }

        private static void Tick(){
            try{
                if(EditorApplication.timeSinceStartup-started>180){throw new TimeoutException("P3a validation timeout.");}
                if(++warmup<6){EditorApplication.QueuePlayerLoopUpdate();M0Validation.Capture(camera,320,240);return;}
                report.Add("cells="+source.CellCount+" pending="+source.PendingGeneration+" free="+source.FreeSlots+" lastError="+(source.LastError??"(none)"));

                source.EnableFrustumCulling=true;source.EnableOcclusionCulling=true;
                Texture2D culled=M0Validation.Capture(camera,640,480);
                uint[] culledStats=source.ReadbackStatisticsForDiagnostics(camera);uint[] culledArgs=source.ReadbackArgumentsForDiagnostics(camera);
                source.EnableFrustumCulling=false;source.EnableOcclusionCulling=false;
                Texture2D reference=M0Validation.Capture(camera,640,480);
                uint[] referenceStats=source.ReadbackStatisticsForDiagnostics(camera);uint[] referenceArgs=source.ReadbackArgumentsForDiagnostics(camera);
                File.WriteAllBytes(Results+"/Culled.png",culled.EncodeToPNG());File.WriteAllBytes(Results+"/Reference.png",reference.EncodeToPNG());
                int covered=0;int differing=0;
                Color32[] a=culled.GetPixels32();Color32[] b=reference.GetPixels32();
                for(int i=0;i<a.Length;i++){
                    if(b[i].g>40){covered++;}
                    if(!a[i].Equals(b[i])){differing++;}
                }
                report.Add("culled stats(considered,frustum,hzb,visible)="+string.Join(",",culledStats)+" indexCount="+culledArgs[0]);
                report.Add("reference stats(considered,frustum,hzb,visible)="+string.Join(",",referenceStats)+" indexCount="+referenceArgs[0]);
                report.Add("coveredPixels="+covered+" of "+a.Length+" differingPixels="+differing);
                if(source.CellCount!=16){throw new InvalidOperationException("16 cellを確保できませんでした。");}
                if(source.PendingGeneration!=0){throw new InvalidOperationException("生成待ちのslotが残っています。");}
                if(referenceArgs[0]==0||referenceStats[3]==0){throw new InvalidOperationException("実行時生成clusterが描かれていません。");}
                if(referenceStats[0]!=16u*64u){throw new InvalidOperationException("候補cluster数が16 cell×64と一致しません。");}
                if(culledStats[1]==0){throw new InvalidOperationException("Frustum cullが1 clusterも棄却していません(視野外のcellがある構図)。");}
                if(covered<a.Length/10){throw new InvalidOperationException("描画された画素が少なすぎます。");}
                if(differing!=0){throw new InvalidOperationException("cullの有無で画像が変わりました。");}
                if(errors.Count>0){throw new InvalidOperationException("Unity logged errors: "+string.Join(" | ",errors));}
                Finish(null);
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Record;
            if(error!=null){report.Add("FAILED: "+error);}else{report.Add("PASSED");}
            File.WriteAllLines(Results+"/Report.txt",report);
            EditorApplication.Exit(error==null?0:1);
        }
    }
}
