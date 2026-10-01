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
    /// <summary>M1の変換、保存、読み戻し、全クラスタ描画を画像差分で検証する。</summary>
    public static class M1Validation{
        private const string Root="Assets/VirtualizedGeometrySample/M1";
        private const int ReferenceLayer=8;
        private const int VirtualLayer=9;
        private static Camera camera;
        private static GameObject referenceObject;
        private static VirtualGeometryMeshAsset virtualAsset;
        private static double started;
        private static int warmup;
        private static readonly List<string> checks=new List<string>();

        [MenuItem("Tools/Virtualized Geometry/Create M1 Validation Scene")]
        public static void CreateSample(){
            if(!Application.isBatchMode&&!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()){return;}
            Directory.CreateDirectory(Root);
            AssetDatabase.Refresh();
            Mesh mesh=AssetDatabase.LoadAssetAtPath<Mesh>(Root+"/SourceGrid.asset");
            if(mesh==null){
                mesh=CreateGrid(32,24);
                AssetDatabase.CreateAsset(mesh,Root+"/SourceGrid.asset");
            }
            virtualAsset=VirtualGeometryConversionService.Convert(mesh,Root+"/ConvertedGrid.asset",new VirtualGeometryBuildSettings{MaxTrianglesPerCluster=64,GenerateHierarchy=false,PositionQuantizationBits=0});
            UniversalRendererData renderer=AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Root+"/M1Renderer.asset");
            if(renderer==null){
                renderer=ScriptableObject.CreateInstance<UniversalRendererData>();
                renderer.renderingMode=RenderingMode.Forward;
                renderer.depthPrimingMode=DepthPrimingMode.Disabled;
                renderer.copyDepthMode=CopyDepthMode.AfterTransparents;
                AssetDatabase.CreateAsset(renderer,Root+"/M1Renderer.asset");
            }
            VirtualGeometryRenderFeature feature=null;
            foreach(ScriptableRendererFeature existing in renderer.rendererFeatures){if(existing is VirtualGeometryRenderFeature found){feature=found;break;}}
            if(feature==null){
                feature=ScriptableObject.CreateInstance<VirtualGeometryRenderFeature>();feature.name="Virtual Geometry M1";
                renderer.rendererFeatures.Add(feature);AssetDatabase.AddObjectToAsset(feature,renderer);
            }
            SerializedObject serialized=new SerializedObject(feature);
            serialized.FindProperty("surfaceShader").objectReferenceValue=AssetDatabase.LoadAssetAtPath<Shader>("Packages/com.virtualgeometry.core/Shaders/VirtualGeometryUnlit.shader");
            serialized.FindProperty("selectionShader").objectReferenceValue=AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.virtualgeometry.core/Shaders/VirtualGeometrySelect.compute");
            serialized.FindProperty("depthCopyShader").objectReferenceValue=Shader.Find("Hidden/Universal Render Pipeline/CopyDepth");
            serialized.ApplyModifiedPropertiesWithoutUndo();feature.Create();EditorUtility.SetDirty(feature);EditorUtility.SetDirty(renderer);
            UniversalRenderPipelineAsset pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Root+"/M1Pipeline.asset");
            if(pipeline==null){
                pipeline=UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(pipeline,Root+"/M1Pipeline.asset");
            }
            pipeline.msaaSampleCount=1;
            pipeline.supportsHDR=false;
            pipeline.supportsCameraDepthTexture=true;
            pipeline.renderScale=1;
            GraphicsSettings.defaultRenderPipeline=pipeline;
            QualitySettings.renderPipeline=pipeline;
            QualitySettings.vSyncCount=0;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,new[]{GraphicsDeviceType.Direct3D12});
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            camera=new GameObject("M1 Validation Camera").AddComponent<Camera>();
            camera.transform.position=new Vector3(0,0,-6);
            camera.orthographic=true;
            camera.orthographicSize=2.7f;
            camera.nearClipPlane=0.1f;
            camera.farClipPlane=20;
            camera.clearFlags=CameraClearFlags.SolidColor;
            camera.backgroundColor=new Color(0.02f,0.03f,0.05f,1);
            camera.allowHDR=false;
            camera.allowMSAA=false;
            camera.GetUniversalAdditionalCameraData().requiresDepthTexture=true;
            Material referenceMaterial=AssetDatabase.LoadAssetAtPath<Material>(Root+"/Reference.mat");
            if(referenceMaterial==null){
                referenceMaterial=new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                referenceMaterial.SetColor("_BaseColor",new Color(0.05f,0.65f,0.95f,1));
                referenceMaterial.SetFloat("_Cull",0);
                AssetDatabase.CreateAsset(referenceMaterial,Root+"/Reference.mat");
            }
            referenceObject=new GameObject("Normal Mesh Reference");
            referenceObject.layer=ReferenceLayer;
            referenceObject.AddComponent<MeshFilter>().sharedMesh=mesh;
            referenceObject.AddComponent<MeshRenderer>().sharedMaterial=referenceMaterial;
            // 参照meshは同じ位置に重なりScene/Game viewでz-fightingするため、保存sceneでは無効にして比較captureの間だけ有効にする。
            referenceObject.SetActive(false);
            GameObject virtualObject=new GameObject("Virtual Geometry Result");
            virtualObject.layer=VirtualLayer;
            var virtualRenderer=virtualObject.AddComponent<VirtualGeometryRenderer>();
            virtualRenderer.SetMeshAsset(virtualAsset);
            virtualRenderer.SetMaterials(referenceMaterial);
            EditorSceneManager.SaveScene(camera.gameObject.scene,Root+"/M1.unity");
            EditorBuildSettings.scenes=new[]{new EditorBuildSettingsScene(Root+"/M1.unity",true)};
            AssetDatabase.SaveAssets();
        }

        /// <summary>GPU付きbatchmodeから呼び、Reference/Virtual/Difference PNGと検証値を保存する。</summary>
        public static void Run(){
            Directory.CreateDirectory("TestResults/M1");
            checks.Clear();
            started=EditorApplication.timeSinceStartup;
            warmup=0;
            try{
                CreateSample();
                EditorApplication.update+=Tick;
            }catch(Exception error){Finish(error);}
        }

        private static void Tick(){
            if(EditorApplication.timeSinceStartup-started>90){Finish(new TimeoutException("M1 URP rendering did not become ready within 90 seconds."));return;}
            try{
                if(++warmup<8){EditorApplication.QueuePlayerLoopUpdate();return;}
                camera.cullingMask=1<<ReferenceLayer;
                referenceObject.SetActive(true);
                Texture2D reference=M0Validation.Capture(camera,640,640);
                referenceObject.SetActive(false);
                File.WriteAllBytes("TestResults/M1/Reference.png",reference.EncodeToPNG());
                camera.cullingMask=1<<VirtualLayer;
                Texture2D virtualImage=M0Validation.Capture(camera,640,640);
                File.WriteAllBytes("TestResults/M1/Virtual.png",virtualImage.EncodeToPNG());
                var dedicated=Object.FindAnyObjectByType<VirtualGeometryRenderer>();
                if(dedicated.IsFallbackVisible){throw new InvalidOperationException("M1 unexpectedly used fallback: "+dedicated.DiagnosticReason);}
                var metrics=dedicated.ReadMetricsForDiagnostics(camera);
                if(metrics.DrawnTriangles!=virtualAsset.TriangleCount||metrics.DrawCalls!=virtualAsset.MaterialCount||metrics.SelectedClusters!=virtualAsset.ClusterCount){throw new InvalidOperationException("M1 dedicated indirect draw counts are incorrect.");}
                checks.Add("dedicated legacy indirect draw: fallback=false, triangles="+metrics.DrawnTriangles+", draw calls="+metrics.DrawCalls+", clusters="+metrics.SelectedClusters+": PASS");
                CompareOccupancy(reference,virtualImage,out Texture2D difference,out int referencePixels,out int virtualPixels,out int differingPixels,out int unionPixels);
                File.WriteAllBytes("TestResults/M1/Difference.png",difference.EncodeToPNG());
                Object.DestroyImmediate(reference);
                Object.DestroyImmediate(virtualImage);
                Object.DestroyImmediate(difference);
                if(virtualAsset.ClusterCount<2){throw new InvalidOperationException("Validation mesh did not generate multiple clusters.");}
                checks.Add("converted clusters="+virtualAsset.ClusterCount+": PASS");
                if(unionPixels<20000){throw new InvalidOperationException("Rendered geometry coverage is unexpectedly small: "+unionPixels);}
                float mismatch=(float)differingPixels/unionPixels;
                if(mismatch>0.0025f){throw new InvalidOperationException("Reference/virtual silhouette mismatch="+mismatch+" ("+differingPixels+"/"+unionPixels+").");}
                checks.Add("all-cluster silhouette mismatch="+mismatch+": PASS");
                checks.Add("referencePixels="+referencePixels+", virtualPixels="+virtualPixels+": PASS");
                string pagePath=Path.Combine(Application.streamingAssetsPath,virtualAsset.PageRelativePath);
                VirtualGeometryPage page=VirtualGeometryPageCodec.Read(File.ReadAllBytes(pagePath));
                if(page.Clusters.Length!=virtualAsset.ClusterCount||page.Indices.Length/3!=virtualAsset.TriangleCount){throw new InvalidDataException("Saved page metadata differs from the converted asset.");}
                checks.Add("saved page round-trip and metadata: PASS");
                File.WriteAllText("TestResults/M1/Environment.txt",
                    "UTC="+DateTime.UtcNow.ToString("O")+"\nUnity="+Application.unityVersion+"\nGPU="+SystemInfo.graphicsDeviceName+
                    "\nGraphics="+SystemInfo.graphicsDeviceVersion+"\nAPI="+SystemInfo.graphicsDeviceType+"\nGPU memory MiB="+SystemInfo.graphicsMemorySize+
                    "\nCompute="+SystemInfo.supportsComputeShaders+"\nIndirect="+SystemInfo.supportsIndirectArgumentsBuffer+
                    "\nReversedZ="+SystemInfo.usesReversedZBuffer+"\nInput triangles="+virtualAsset.InputTriangleCount+
                    "\nStored triangles="+virtualAsset.TriangleCount+"\nClusters="+virtualAsset.ClusterCount+"\nContentHash="+virtualAsset.ContentHash+"\n");
                Finish(null);
            }catch(Exception error){Finish(error);}
        }

        private static void CompareOccupancy(Texture2D reference,Texture2D virtualImage,out Texture2D difference,out int referencePixels,out int virtualPixels,out int differingPixels,out int unionPixels){
            Color32[] left=reference.GetPixels32();
            Color32[] right=virtualImage.GetPixels32();
            Color32[] diff=new Color32[left.Length];
            referencePixels=0;virtualPixels=0;differingPixels=0;unionPixels=0;
            for(int i=0;i<left.Length;i++){
                bool leftFilled=left[i].b>80;
                bool rightFilled=right[i].b>80;
                if(leftFilled){referencePixels++;}
                if(rightFilled){virtualPixels++;}
                if(leftFilled||rightFilled){unionPixels++;}
                if(leftFilled!=rightFilled){differingPixels++;diff[i]=new Color32(255,0,0,255);}
                else if(leftFilled){diff[i]=new Color32(90,90,90,255);}
                else{diff[i]=new Color32(0,0,0,255);}
            }
            difference=new Texture2D(reference.width,reference.height,TextureFormat.RGBA32,false,true);
            difference.SetPixels32(diff);
            difference.Apply();
        }

        private static Mesh CreateGrid(int width,int height){
            Vector3[] vertices=new Vector3[(width+1)*(height+1)];
            Vector3[] normals=new Vector3[vertices.Length];
            Vector2[] uv=new Vector2[vertices.Length];
            for(int y=0;y<=height;y++){
                for(int x=0;x<=width;x++){
                    int index=y*(width+1)+x;
                    float px=((float)x/width-0.5f)*4.4f;
                    float py=((float)y/height-0.5f)*3.4f;
                    vertices[index]=new Vector3(px,py,Mathf.Sin(px*2.1f)*Mathf.Cos(py*1.7f)*0.16f);
                    normals[index]=Vector3.back;
                    uv[index]=new Vector2((float)x/width,(float)y/height);
                }
            }
            int[] indices=new int[width*height*6];
            int offset=0;
            for(int y=0;y<height;y++){
                for(int x=0;x<width;x++){
                    int a=y*(width+1)+x;int b=a+1;int c=a+width+1;int d=c+1;
                    indices[offset++]=a;indices[offset++]=c;indices[offset++]=b;
                    indices[offset++]=b;indices[offset++]=c;indices[offset++]=d;
                }
            }
            Mesh mesh=new Mesh{name="M1 Dense Grid",indexFormat=vertices.Length>65535?IndexFormat.UInt32:IndexFormat.UInt16};
            mesh.vertices=vertices;mesh.normals=normals;mesh.uv=uv;mesh.triangles=indices;mesh.RecalculateBounds();
            return mesh;
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;
            string result=string.Join("\n",checks)+"\n";
            int exitCode=0;
            if(error!=null){result+="FAIL: "+error;Debug.LogException(error);exitCode=1;}else{result+="PASS\n";Debug.Log("VG M1 PASS");}
            File.WriteAllText("TestResults/M1/Results.txt",result);
            EditorApplication.Exit(exitCode);
        }
    }
}
