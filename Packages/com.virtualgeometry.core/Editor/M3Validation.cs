using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object=UnityEngine.Object;

namespace VirtualizedGeometry.Editor{
    /// <summary>M3サンプルで現フレームHZBとFrustumの画像等価性・状態変化を検証する。</summary>
    public static class M3Validation{
        private const string Root="Assets/VirtualizedGeometrySample/M3";
        private static Camera camera;
        private static VirtualGeometryRenderer instance;
        private static GameObject occluder;
        private static int warmup;
        private static double started;
        private static readonly List<string> checks=new List<string>();
        private static readonly List<string> metrics=new List<string>();
        private static readonly List<string> errors=new List<string>();

        [MenuItem("Tools/Virtualized Geometry/Create M3 Validation Scene")]
        public static void CreateSample(){
            if(!Application.isBatchMode&&!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()){return;}
            Directory.CreateDirectory(Root);AssetDatabase.Refresh();
            Mesh generated=CreateSurface(128,72);
            VirtualGeometryValidationModel.Import(generated,Root+"/Surface.obj",new VirtualGeometryModelSettings{MaxTrianglesPerCluster=32,PositionQuantizationBits=16});
            Object.DestroyImmediate(generated);
            UniversalRendererData renderer=AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Root+"/M3Renderer.asset");
            if(renderer==null){
                renderer=ScriptableObject.CreateInstance<UniversalRendererData>();renderer.renderingMode=RenderingMode.Forward;
                renderer.depthPrimingMode=DepthPrimingMode.Disabled;renderer.copyDepthMode=CopyDepthMode.AfterTransparents;
                AssetDatabase.CreateAsset(renderer,Root+"/M3Renderer.asset");
            }
            VirtualGeometryRenderFeature feature=null;
            foreach(ScriptableRendererFeature existing in renderer.rendererFeatures){if(existing is VirtualGeometryRenderFeature found){feature=found;break;}}
            if(feature==null){
                feature=ScriptableObject.CreateInstance<VirtualGeometryRenderFeature>();feature.name="Virtual Geometry M3";
                renderer.rendererFeatures.Add(feature);AssetDatabase.AddObjectToAsset(feature,renderer);
            }
            SerializedObject serialized=new SerializedObject(feature);
            serialized.FindProperty("selectionShader").objectReferenceValue=AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.virtualgeometry.core/Shaders/VirtualGeometrySelect.compute");
            serialized.FindProperty("surfaceShader").objectReferenceValue=Shader.Find("VirtualizedGeometry/Unlit");
            serialized.ApplyModifiedPropertiesWithoutUndo();feature.Create();
            EditorUtility.SetDirty(feature);EditorUtility.SetDirty(renderer);
            UniversalRenderPipelineAsset pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Root+"/M3Pipeline.asset");
            if(pipeline==null){pipeline=UniversalRenderPipelineAsset.Create(renderer);AssetDatabase.CreateAsset(pipeline,Root+"/M3Pipeline.asset");}
            pipeline.msaaSampleCount=1;pipeline.supportsHDR=false;pipeline.supportsCameraDepthTexture=true;pipeline.renderScale=1;
            GraphicsSettings.defaultRenderPipeline=pipeline;QualitySettings.renderPipeline=pipeline;QualitySettings.vSyncCount=0;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,new[]{GraphicsDeviceType.Direct3D12});
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            camera=new GameObject("M3 Validation Camera").AddComponent<Camera>();
            camera.nearClipPlane=0.1f;camera.farClipPlane=200;camera.fieldOfView=55;camera.clearFlags=CameraClearFlags.SolidColor;
            camera.backgroundColor=new Color(0.02f,0.03f,0.05f,1);camera.allowHDR=false;camera.allowMSAA=false;
            camera.GetUniversalAdditionalCameraData().requiresDepthTexture=true;
            instance=new GameObject("Virtual Geometry Surface").AddComponent<VirtualGeometryRenderer>();
            // NewSceneの未使用アセット解放後に永続アセットを取得し直す。
            instance.SetStreamingConfiguration(VirtualGeometryStreamingMode.AllResident,new VirtualGeometryStreamingSettings());
            instance.SetMeshAsset(VirtualGeometryValidationModel.Load(Root+"/Surface.obj"));
            // 可視判定は青成分で数えるため、照明に左右されない青いUnlitで描く。
            Material surface=AssetDatabase.LoadAssetAtPath<Material>(Root+"/Surface.mat");
            if(surface==null){
                surface=new Material(Shader.Find("Universal Render Pipeline/Unlit"));surface.SetColor("_BaseColor",new Color(0.05f,0.65f,0.95f,1));surface.SetFloat("_Cull",0);
                AssetDatabase.CreateAsset(surface,Root+"/Surface.mat");
            }
            instance.SetMaterials(surface);
            Material wall=AssetDatabase.LoadAssetAtPath<Material>(Root+"/Occluder.mat");
            if(wall==null){
                wall=new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                wall.SetColor("_BaseColor",new Color(0.6f,0.18f,0.05f,1));
                AssetDatabase.CreateAsset(wall,Root+"/Occluder.mat");
            }
            occluder=GameObject.CreatePrimitive(PrimitiveType.Cube);occluder.name="Current Frame Opaque Occluder";
            occluder.transform.position=new Vector3(-1.5f,0.8f,5);
            occluder.transform.localScale=new Vector3(5,12,0.3f);
            occluder.GetComponent<MeshRenderer>().sharedMaterial=wall;
            EditorSceneManager.SaveScene(camera.gameObject.scene,Root+"/M3.unity");
            AssetDatabase.SaveAssets();
        }

        public static void Run(){
            Directory.CreateDirectory("TestResults/M3");
            checks.Clear();metrics.Clear();errors.Clear();warmup=0;started=EditorApplication.timeSinceStartup;
            metrics.Add("scenario,width,height,differingPixels,considered,frustumCulled,hzbCulled,visible,indexCount");
            Application.logMessageReceived+=RecordError;
            // 保存sceneは既定のLOD閾値にして距離によるLOD変化を見られるようにし、検証の間だけ閾値を固定する。
            try{CreateSample();instance.SetLodSettings(0,0);EditorApplication.update+=Tick;}catch(Exception error){Finish(error);}
        }

        private static void RecordError(string condition,string stackTrace,LogType type){
            if(type==LogType.Error||type==LogType.Exception||type==LogType.Assert){errors.Add(condition);}
        }

        private static void Tick(){
            try{
                if(EditorApplication.timeSinceStartup-started>180){throw new TimeoutException("M3 validation timeout.");}
                if(++warmup<8){EditorApplication.QueuePlayerLoopUpdate();return;}
                VirtualGeometryPage debugPage=VirtualGeometryPageCodec.Read(File.ReadAllBytes(VirtualGeometryPagePaths.Resolve(instance.MeshAsset.PageRelativePath)));
                List<float> radii=new List<float>();float minRadius=float.MaxValue;float maxRadius=0;
                foreach(VirtualGeometryHierarchyNode node in debugPage.Nodes){
                    if(node.ChildCount!=0){continue;}
                    for(uint c=0;c<node.ClusterCount;c++){
                        VirtualGeometryCluster cluster=debugPage.Clusters[node.FirstCluster+c];
                        radii.Add(cluster.BoundsRadius);minRadius=Mathf.Min(minRadius,cluster.BoundsRadius);maxRadius=Mathf.Max(maxRadius,cluster.BoundsRadius);
                    }
                }
                radii.Sort();checks.Add("leaf cluster radii min="+minRadius+", median="+radii[radii.Count/2]+", max="+maxRadius);
                ValidateHzbKernels();
                uint[] baseline=Compare("Baseline",640,480);
                if(baseline[1]==0||baseline[2]==0||baseline[3]==0){throw new InvalidOperationException("Frustum, HZB and visible populations must all be nonzero.");}
                occluder.transform.position=new Vector3(-30,0.8f,5);
                uint[] revealed=Compare("OccluderMovedImmediate",640,480);
                if(revealed[3]<=baseline[3]||revealed[2]!=0){throw new InvalidOperationException("Moving the occluder must reveal clusters immediately.");}
                occluder.transform.position=new Vector3(-1.5f,0.8f,5);
                camera.transform.position=new Vector3(12,3,4);camera.transform.LookAt(new Vector3(6,0,10));
                Compare("CameraTeleportImmediate",640,480);
                camera.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);
                Compare("ResizeImmediate",713,431);
                camera.orthographic=true;camera.orthographicSize=5;
                Compare("OrthographicImmediate",713,431);
                instance.transform.localScale=new Vector3(-1.2f,0.7f,1);
                Compare("NegativeNonUniformScale",713,431);
                instance.transform.localScale=Vector3.one;camera.orthographic=false;
                camera.transform.position=new Vector3(0,0,9.8f);
                Compare("NearPlaneIntersection",713,431);
                camera.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);
                if(errors.Count>0){throw new InvalidOperationException("Unity logged errors: "+string.Join(" | ",errors));}
                File.WriteAllText("TestResults/M3/Environment.txt",
                    "UTC="+DateTime.UtcNow.ToString("O")+"\nUnity="+Application.unityVersion+"\nOS="+SystemInfo.operatingSystem+
                    "\nGPU="+SystemInfo.graphicsDeviceName+"\nGraphics="+SystemInfo.graphicsDeviceVersion+
                    "\nAPI="+SystemInfo.graphicsDeviceType+"\nReversedZ="+SystemInfo.usesReversedZBuffer+
                    "\nCompute="+SystemInfo.supportsComputeShaders+"\nIndirect="+SystemInfo.supportsIndirectArgumentsBuffer+
                    "\nHZB=current-frame active depth, padded power-of-two, farthest reduction, no history\n");
                Finish(null);
            }catch(Exception error){Finish(error);}
        }

        private static uint[] Compare(string name,int width,int height){
            // 変化後の最初の描画をカリング有効にし、前フレームへの依存を画像比較で検出する。

            instance.SetCullingSettings(true,true);
            Texture2D culled=M0Validation.Capture(camera,width,height);

            VirtualGeometryGpuSelectionSnapshot snapshot=instance.ReadbackSelectionForDiagnostics();
            instance.SetCullingSettings(false,false);
            Texture2D reference=M0Validation.Capture(camera,width,height);
            File.WriteAllBytes("TestResults/M3/"+name+"Culled.png",culled.EncodeToPNG());
            File.WriteAllBytes("TestResults/M3/"+name+"Reference.png",reference.EncodeToPNG());
            Color32[] a=culled.GetPixels32();Color32[] b=reference.GetPixels32();Color32[] differences=new Color32[a.Length];
            int differing=0;int virtualPixels=0;
            for(int i=0;i<a.Length;i++){
                if(b[i].b>150){virtualPixels++;}
                bool equal=a[i].Equals(b[i]);
                if(!equal){differing++;}
                if(equal){differences[i]=new Color32(0,0,0,255);}else{differences[i]=new Color32(255,0,0,255);}
            }
            Texture2D difference=new Texture2D(width,height,TextureFormat.RGBA32,false,true);
            difference.SetPixels32(differences);difference.Apply();
            File.WriteAllBytes("TestResults/M3/"+name+"Difference.png",difference.EncodeToPNG());
            uint[] stats=snapshot.CullingStatistics;
            metrics.Add(name+","+width+","+height+","+differing+","+string.Join(",",stats)+","+snapshot.Arguments[0]);
            checks.Add(name+": differingPixels="+differing+", virtualPixels="+virtualPixels+", stats="+string.Join("/",stats)+", indices="+snapshot.Arguments[0]);
            File.WriteAllText("TestResults/M3/Metrics.csv",string.Join("\n",metrics)+"\n");
            Object.DestroyImmediate(culled);Object.DestroyImmediate(reference);Object.DestroyImmediate(difference);
            if(differing!=0){throw new InvalidOperationException(name+" lost or changed "+differing+" pixels.");}
            if(virtualPixels==0){throw new InvalidOperationException(name+" has no visible virtual geometry.");}
            if(stats[0]!=stats[1]+stats[2]+stats[3]){throw new InvalidOperationException("Culling populations do not sum to considered.");}
            return stats;
        }

        /// <summary>現GPUの通常ZとReversed ZでNPOTの背景埋めと全mip最遠縮小を検証する。</summary>
        private static void ValidateHzbKernels(){
            ComputeShader shader=Object.Instantiate(AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.virtualgeometry.core/Shaders/VirtualGeometrySelect.compute"));
            Texture2D source=new Texture2D(7,3,TextureFormat.RFloat,false,true);
            RenderTexture hzb=new RenderTexture(8,4,0,RenderTextureFormat.RFloat){
                enableRandomWrite=true,useMipMap=true,autoGenerateMips=false,filterMode=FilterMode.Point
            };
            try{
                hzb.Create();int copy=shader.FindKernel("CopyDepth");int downsample=shader.FindKernel("Downsample");
                shader.SetVector("_VGScreenSize",new Vector4(7,3,0,0));shader.SetVector("_VGHzbSize",new Vector4(8,4,4,0));
                for(int reversed=0;reversed<=1;reversed++){
                    float background=1;
                    if(reversed!=0){background=0;}
                    float[] input=new float[21];
                    for(int i=0;i<input.Length;i++){input[i]=(i%6+1)*0.125f;}
                    input[9]=background;source.SetPixelData(input,0);source.Apply(false);
                    shader.SetInt("_VGReversedZ",reversed);shader.SetTexture(copy,"_VGDepthSource",source);
                    shader.SetTexture(copy,"_VGHzbDestination",hzb,0);shader.Dispatch(copy,1,1,1);
                    int width=8;int height=4;float[] expected=new float[width*height];
                    for(int y=0;y<height;y++){
                        for(int x=0;x<width;x++){
                            float value=background;if(x<7&&y<3){value=input[y*7+x];}expected[y*width+x]=value;
                        }
                    }
                    AssertMip(hzb,0,expected);
                    for(int mip=1;mip<4;mip++){
                        shader.SetInt("_VGSourceMip",mip-1);shader.SetInt("_VGDestinationMip",mip);
                        shader.SetTexture(downsample,"_VGHzbSource",hzb);
                        shader.SetTexture(downsample,"_VGHzbDestination",hzb,mip);shader.Dispatch(downsample,1,1,1);
                        int nextWidth=Mathf.Max(1,width/2);int nextHeight=Mathf.Max(1,height/2);
                        float[] next=new float[nextWidth*nextHeight];
                        for(int y=0;y<nextHeight;y++){
                            for(int x=0;x<nextWidth;x++){
                                int x0=Mathf.Min(x*2,width-1);int x1=Mathf.Min(x*2+1,width-1);
                                int y0=Mathf.Min(y*2,height-1);int y1=Mathf.Min(y*2+1,height-1);
                                next[y*nextWidth+x]=VirtualGeometryCulling.ReduceDepth(expected[y0*width+x0],expected[y0*width+x1],
                                    expected[y1*width+x0],expected[y1*width+x1],reversed!=0);
                            }
                        }
                        AssertMip(hzb,mip,next);expected=next;width=nextWidth;height=nextHeight;
                    }
                    checks.Add("GPU HZB reversedZ="+reversed+": padding and all 4 mips exact PASS");
                }
            }finally{hzb.Release();Object.DestroyImmediate(hzb);Object.DestroyImmediate(source);Object.DestroyImmediate(shader);}
        }

        private static void AssertMip(RenderTexture texture,int mip,float[] expected){
            AsyncGPUReadbackRequest request=AsyncGPUReadback.Request(texture,mip);
            request.WaitForCompletion();
            if(request.hasError){throw new InvalidOperationException("HZB GPU readback failed at mip "+mip);}
            var actual=request.GetData<float>();
            if(actual.Length!=expected.Length){throw new InvalidOperationException("HZB mip size differs at "+mip);}
            for(int i=0;i<expected.Length;i++){
                if(actual[i]!=expected[i]){throw new InvalidOperationException("HZB mip "+mip+" pixel "+i+": "+actual[i]+" != "+expected[i]);}
            }
        }
        private static Mesh CreateSurface(int width,int height){
            Vector3[] vertices=new Vector3[(width+1)*(height+1)];Vector3[] normals=new Vector3[vertices.Length];Vector2[] uv=new Vector2[vertices.Length];
            for(int y=0;y<=height;y++){
                for(int x=0;x<=width;x++){
                    int i=y*(width+1)+x;float px=(float)x/width*32-16;float py=(float)y/height*18-9;
                    vertices[i]=new Vector3(px,py,10+0.02f*Mathf.Sin(px*0.15f)*Mathf.Cos(py*0.15f));normals[i]=Vector3.back;
                    uv[i]=new Vector2((float)x/width,(float)y/height);
                }
            }
            int[] indices=new int[width*height*6];int offset=0;
            for(int y=0;y<height;y++){
                for(int x=0;x<width;x++){
                    int a=y*(width+1)+x;int b=a+1;int c=a+width+1;int d=c+1;
                    indices[offset++]=a;indices[offset++]=c;indices[offset++]=b;
                    indices[offset++]=b;indices[offset++]=c;indices[offset++]=d;
                }
            }
            Mesh mesh=new Mesh{name="Surface",indexFormat=IndexFormat.UInt32};
            mesh.vertices=vertices;mesh.normals=normals;mesh.uv=uv;mesh.triangles=indices;mesh.RecalculateBounds();return mesh;
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=RecordError;
            string text=string.Join(Environment.NewLine,checks)+Environment.NewLine;
            int exitCode=0;
            if(error!=null){text+="FAIL: "+error;Debug.LogException(error);exitCode=1;}
            else{text+="PASS"+Environment.NewLine;Debug.Log("VG M3 PASS");}
            File.WriteAllText("TestResults/M3/Results.txt",text);
            EditorApplication.Exit(exitCode);
        }
    }
}