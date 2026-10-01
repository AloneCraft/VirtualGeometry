using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object=UnityEngine.Object;
namespace VirtualizedGeometry.Editor{
    /// <summary>他のvalidation fixtureに依存しない小型Lit/streaming/共有instance sample。</summary>
    public static class VirtualGeometrySampleSetup{
        private const string Root="Assets/VirtualizedGeometrySample/Practical";private static Camera camera;private static VirtualGeometryRenderer first,second;private static int frames;private static double started;
        [MenuItem("Tools/Virtualized Geometry/Create Practical Sample")]
        public static void CreateSample(){
            if(!Application.isBatchMode&&!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()){return;}
            Directory.CreateDirectory(Root);AssetDatabase.Refresh();EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            Mesh source=AssetDatabase.LoadAssetAtPath<Mesh>(Root+"/Source.asset");if(source==null){source=CreateMesh();AssetDatabase.CreateAsset(source,Root+"/Source.asset");}
            var converted=VirtualGeometryConversionService.Convert(source,Root+"/Converted.asset",new VirtualGeometryBuildSettings{MaxTrianglesPerCluster=64,HierarchyFanout=4});
            var rendererData=AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Root+"/Renderer.asset");if(rendererData==null){rendererData=ScriptableObject.CreateInstance<UniversalRendererData>();AssetDatabase.CreateAsset(rendererData,Root+"/Renderer.asset");}
            rendererData.renderingMode=RenderingMode.Forward;rendererData.depthPrimingMode=DepthPrimingMode.Disabled;rendererData.copyDepthMode=CopyDepthMode.AfterOpaques;
            VirtualGeometryRendererSetup.EnsureFeature(rendererData);
            var pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Root+"/Pipeline.asset");if(pipeline==null){pipeline=UniversalRenderPipelineAsset.Create(rendererData);AssetDatabase.CreateAsset(pipeline,Root+"/Pipeline.asset");}
            pipeline.msaaSampleCount=1;pipeline.supportsHDR=false;pipeline.supportsCameraDepthTexture=true;pipeline.shadowDistance=60;var pipelineFields=new SerializedObject(pipeline);pipelineFields.FindProperty("m_MainLightShadowsSupported").boolValue=true;pipelineFields.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(pipeline);
            GraphicsSettings.defaultRenderPipeline=pipeline;QualitySettings.renderPipeline=pipeline;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,false);PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,new[]{GraphicsDeviceType.Direct3D12});
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=Color.black;RenderSettings.ambientIntensity=0;RenderSettings.ambientProbe=new SphericalHarmonicsL2();RenderSettings.reflectionIntensity=0;RenderSettings.fog=false;
            first=CreateInstance("共有 instance A",new Vector3(-5,0,0),new Color(.1f,.6f,.85f),converted);second=CreateInstance("共有 instance B",new Vector3(5,0,0),new Color(.85f,.3f,.1f),converted);
            camera=new GameObject("Sample camera").AddComponent<Camera>();camera.tag="MainCamera";camera.transform.position=new Vector3(0,1,-19);camera.transform.LookAt(Vector3.zero);camera.fieldOfView=60;camera.nearClipPlane=.1f;camera.farClipPlane=100;camera.allowMSAA=false;camera.allowHDR=false;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.02f,.02f,.025f);camera.GetUniversalAdditionalCameraData().requiresDepthTexture=true;camera.gameObject.AddComponent<VirtualizedGeometry.VirtualGeometrySampleCameraController>();
            var light=new GameObject("Main directional light").AddComponent<Light>();light.type=LightType.Directional;light.transform.rotation=Quaternion.Euler(20,-25,0);light.shadows=LightShadows.Soft;RenderSettings.sun=light;
            var receiver=GameObject.CreatePrimitive(PrimitiveType.Quad);receiver.name="通常meshのshadow receiver";receiver.transform.position=new Vector3(0,0,2);receiver.transform.localScale=new Vector3(22,12,1);receiver.GetComponent<MeshRenderer>().sharedMaterial=converted.ProxyLitMaterial;
            EditorSceneManager.SaveScene(camera.gameObject.scene,Root+"/Practical.unity");AssetDatabase.SaveAssets();Selection.activeGameObject=first.gameObject;
            Debug.Log("Practical sampleを生成しました。専用Forward pipelineとWindows DX12を設定しました。Scene viewのVirtual Geometry toolbarから表示を操作できます。");
        }
        private static VirtualGeometryRenderer CreateInstance(string name,Vector3 position,Color color,VirtualGeometryMeshAsset asset){
            var result=new GameObject(name).AddComponent<VirtualGeometryRenderer>();result.transform.position=position;result.SetStreamingConfiguration(VirtualGeometryStreamingMode.Streaming,new VirtualGeometryStreamingSettings{GpuGeometryBudgetBytes=(long)asset.SlotGeometryBytes*asset.StreamingPageCount,CpuCacheBudgetBytes=asset.SlotGeometryBytes*4L,TransferBytesPerFrame=asset.SlotGeometryBytes*2,IoConcurrency=2,RequestQueueCapacity=16,IoDelayMilliseconds=25});result.SetMeshAsset(asset);result.SetMaterials(SampleMaterial(name,color,asset));result.SetProxyOptions(true,true);return result;
        }
        /// <summary>instanceごとの色違いURP Lit Material asset。MeshRendererと同じくMaterialで見た目を決める。</summary>
        private static Material SampleMaterial(string instanceName,Color color,VirtualGeometryMeshAsset asset){
            string path=Root+"/"+(instanceName.EndsWith("A")?"InstanceA":"InstanceB")+".mat";
            var material=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(material==null){material=new Material(asset.ProxyLitMaterial);AssetDatabase.CreateAsset(material,path);}
            material.SetColor("_BaseColor",color);material.SetFloat("_Smoothness",.4f);material.SetFloat("_Metallic",0);EditorUtility.SetDirty(material);
            return material;
        }
        private static Mesh CreateMesh(){
            const int n=32;var vertices=new Vector3[(n+1)*(n+1)];var uv=new Vector2[vertices.Length];var indices=new int[n*n*6];int next=0;
            for(int y=0;y<=n;y++){for(int x=0;x<=n;x++){float px=(float)x/n*8-4,py=(float)y/n*8-4;int i=y*(n+1)+x;vertices[i]=new Vector3(px,py,Mathf.Sin(px*1.5f)*Mathf.Cos(py)*.4f);uv[i]=new Vector2((float)x/n,(float)y/n);}}
            for(int y=0;y<n;y++){for(int x=0;x<n;x++){int a=y*(n+1)+x;indices[next++]=a;indices[next++]=a+n+1;indices[next++]=a+1;indices[next++]=a+1;indices[next++]=a+n+1;indices[next++]=a+n+2;}}
            var mesh=new Mesh{name="小型wave source"};mesh.vertices=vertices;mesh.uv=uv;mesh.triangles=indices;mesh.RecalculateNormals();mesh.RecalculateTangents();mesh.RecalculateBounds();return mesh;
        }
        public static void Run(){try{CreateSample();frames=0;started=EditorApplication.timeSinceStartup;EditorApplication.update+=Tick;}catch(Exception error){Debug.LogException(error);EditorApplication.Exit(1);}}
        private static void Tick(){
            try{
                var image=M0Validation.Capture(camera,640,360);frames++;
                if(!first.IsStreamingRootReady||!second.IsStreamingRootReady||frames<30){Object.DestroyImmediate(image);if(EditorApplication.timeSinceStartup-started>60){throw new TimeoutException("Sample root did not become ready.");}EditorApplication.QueuePlayerLoopUpdate();return;}
                if(first.IsFallbackVisible||second.IsFallbackVisible||VirtualGeometryRenderer.SharedResourceCount!=1){throw new InvalidOperationException("Sample did not draw two shared virtual instances.");}
                var metrics=VirtualGeometryDebugMetrics.Aggregate(new[]{first.ReadMetricsForDiagnostics(camera),second.ReadMetricsForDiagnostics(camera)});if(metrics.DrawnTriangles<=0){throw new InvalidOperationException("Sample has no drawn triangles.");}
                Directory.CreateDirectory("TestResults/M5/Sample");File.WriteAllBytes("TestResults/M5/Sample/Practical.png",image.EncodeToPNG());Object.DestroyImmediate(image);
                File.WriteAllText("TestResults/M5/Sample/Results.txt","PASS\nsource triangles="+first.MeshAsset.InputTriangleCount+"\ninstances=2\nshared resources=1\ndrawn triangles="+metrics.DrawnTriangles+"\nGPU geometry="+metrics.GpuGeometryBytes+"\nscene="+Root+"/Practical.unity\n");EditorApplication.update-=Tick;first.SetMeshAsset(null);second.SetMeshAsset(null);EditorApplication.Exit(0);
            }catch(Exception error){EditorApplication.update-=Tick;Debug.LogException(error);EditorApplication.Exit(1);}
        }
    }
}