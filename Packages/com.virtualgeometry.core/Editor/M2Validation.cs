using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object=UnityEngine.Object;

namespace VirtualizedGeometry.Editor{
    /// <summary>M2の階層生成、CPU/GPU選択一致、混在LODの被覆を実GPUで検証する。</summary>
    public static class M2Validation{
        private const string Root="Assets/VirtualizedGeometrySample/M2";
        private const int ReferenceLayer=10;
        private const int VirtualLayer=11;
        private const int CaptureSize=640;
        private static Camera camera;
        private static GameObject referenceObject;
        private static GameObject virtualObject;
        private static VirtualGeometryRenderer virtualRenderer;
        private static VirtualGeometryMeshAsset virtualAsset;
        private static double started;
        private static int warmup;
        private static readonly List<string> checks=new List<string>();

        [MenuItem("Tools/Virtualized Geometry/Create M2 Validation Scene")]
        public static void CreateSample(){
            if(!Application.isBatchMode&&!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()){return;}
            Directory.CreateDirectory(Root);
            AssetDatabase.Refresh();
            Mesh mesh=AssetDatabase.LoadAssetAtPath<Mesh>(Root+"/SourceTerrain.asset");
            if(mesh==null){
                mesh=CreateTerrain(64,48);
                AssetDatabase.CreateAsset(mesh,Root+"/SourceTerrain.asset");
            }
            virtualAsset=VirtualGeometryConversionService.Convert(mesh,Root+"/ConvertedTerrain.asset",new VirtualGeometryBuildSettings{
                MaxTrianglesPerCluster=32,GenerateHierarchy=true,HierarchyFanout=4,SimplificationRatio=0.5f,PositionQuantizationBits=16
            });
            UniversalRendererData renderer=AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Root+"/M2Renderer.asset");
            if(renderer==null){
                renderer=ScriptableObject.CreateInstance<UniversalRendererData>();
                renderer.renderingMode=RenderingMode.Forward;
                renderer.depthPrimingMode=DepthPrimingMode.Disabled;
                renderer.copyDepthMode=CopyDepthMode.AfterTransparents;
                AssetDatabase.CreateAsset(renderer,Root+"/M2Renderer.asset");
            }
            VirtualGeometryRenderFeature feature=null;
            foreach(ScriptableRendererFeature existing in renderer.rendererFeatures){
                if(existing is VirtualGeometryRenderFeature found){feature=found;break;}
            }
            if(feature==null){
                feature=ScriptableObject.CreateInstance<VirtualGeometryRenderFeature>();
                feature.name="Virtual Geometry M2";
                renderer.rendererFeatures.Add(feature);
                AssetDatabase.AddObjectToAsset(feature,renderer);
            }
            SerializedObject featureSerialized=new SerializedObject(feature);
            featureSerialized.FindProperty("selectionShader").objectReferenceValue=
                AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.virtualgeometry.core/Shaders/VirtualGeometrySelect.compute");
            featureSerialized.FindProperty("surfaceShader").objectReferenceValue=
                AssetDatabase.LoadAssetAtPath<Shader>("Packages/com.virtualgeometry.core/Shaders/VirtualGeometryUnlit.shader");
            featureSerialized.ApplyModifiedPropertiesWithoutUndo();
            feature.Create();
            EditorUtility.SetDirty(feature);EditorUtility.SetDirty(renderer);

            UniversalRenderPipelineAsset pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Root+"/M2Pipeline.asset");
            if(pipeline==null){
                pipeline=UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(pipeline,Root+"/M2Pipeline.asset");
            }
            pipeline.msaaSampleCount=1;pipeline.supportsHDR=false;pipeline.supportsCameraDepthTexture=true;pipeline.renderScale=1;
            GraphicsSettings.defaultRenderPipeline=pipeline;QualitySettings.renderPipeline=pipeline;QualitySettings.vSyncCount=0;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,new[]{GraphicsDeviceType.Direct3D12});

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            camera=new GameObject("M2 Validation Camera").AddComponent<Camera>();
            camera.nearClipPlane=0.1f;camera.farClipPlane=500;camera.fieldOfView=55;camera.clearFlags=CameraClearFlags.SolidColor;
            camera.backgroundColor=new Color(0.02f,0.03f,0.05f,1);camera.allowHDR=false;camera.allowMSAA=false;
            camera.GetUniversalAdditionalCameraData().requiresDepthTexture=true;
            SetCamera(new Vector3(0,9,-10),new Vector3(0,0,8));

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
            virtualObject=new GameObject("Virtual Geometry Hierarchy");
            virtualObject.layer=VirtualLayer;
            virtualRenderer=virtualObject.AddComponent<VirtualGeometryRenderer>();
            virtualRenderer.SetStreamingConfiguration(VirtualGeometryStreamingMode.AllResident,new VirtualGeometryStreamingSettings());
            virtualRenderer.SetMeshAsset(virtualAsset);
            virtualRenderer.SetLodSettings(2,0);
            virtualRenderer.SetCullingSettings(false,false);
            virtualRenderer.SetMaterials(referenceMaterial);

            EditorSceneManager.SaveScene(camera.gameObject.scene,Root+"/M2.unity");
            EditorBuildSettings.scenes=new[]{new EditorBuildSettingsScene(Root+"/M2.unity",true)};
            AssetDatabase.SaveAssets();
        }

        public static void Run(){
            Directory.CreateDirectory("TestResults/M2");
            checks.Clear();started=EditorApplication.timeSinceStartup;warmup=0;
            try{CreateSample();EditorApplication.update+=Tick;}catch(Exception error){Finish(error);}
        }

        private static void Tick(){
            if(EditorApplication.timeSinceStartup-started>120){Finish(new TimeoutException("M2 GPU validation did not finish within 120 seconds."));return;}
            try{
                if(++warmup<10){EditorApplication.QueuePlayerLoopUpdate();return;}
                string pagePath=Path.Combine(Application.streamingAssetsPath,virtualAsset.PageRelativePath);
                VirtualGeometryPage page=VirtualGeometryPageCodec.Read(File.ReadAllBytes(pagePath));
                int leafNodes=0;
                foreach(VirtualGeometryHierarchyNode node in page.Nodes){if(node.ChildCount==0){leafNodes++;}}
                if(page.Nodes.Length<=leafNodes||leafNodes<16){throw new InvalidOperationException("Validation asset did not produce a useful hierarchy.");}
                checks.Add("hierarchy nodes="+page.Nodes.Length+", leaf nodes="+leafNodes+": PASS");

                float threshold=FindMixedThreshold(page,out VirtualGeometryLodSelectionResult cpuSelection);
                virtualRenderer.SetLodSettings(threshold,0);
                camera.cullingMask=1<<ReferenceLayer;
                referenceObject.SetActive(true);
                Texture2D reference=M0Validation.Capture(camera,CaptureSize,CaptureSize);
                referenceObject.SetActive(false);
                File.WriteAllBytes("TestResults/M2/Reference.png",reference.EncodeToPNG());
                camera.cullingMask=1<<VirtualLayer;
                Texture2D virtualImage=M0Validation.Capture(camera,CaptureSize,CaptureSize);
                File.WriteAllBytes("TestResults/M2/VirtualMixed.png",virtualImage.EncodeToPNG());
                VirtualGeometryGpuSelectionSnapshot gpu=virtualRenderer.ReadbackSelectionForDiagnostics();
                CompareSelection(page,cpuSelection,gpu);
                checks.Add("CPU/GPU selected nodes, compacted indices, and indirect count match: PASS");

                CompareOccupancy(reference,virtualImage,out Texture2D difference,out int differing,out int union);
                File.WriteAllBytes("TestResults/M2/Difference.png",difference.EncodeToPNG());
                float mismatch=union==0?1:(float)differing/union;
                if(union<30000||mismatch>0.005f){throw new InvalidOperationException("Mixed LOD occupancy mismatch="+mismatch+" ("+differing+"/"+union+").");}
                checks.Add("mixed LOD occupancy mismatch="+mismatch.ToString("R",CultureInfo.InvariantCulture)+": PASS");
                Object.DestroyImmediate(reference);Object.DestroyImmediate(virtualImage);Object.DestroyImmediate(difference);
                WriteSelectionCsv(page,gpu);

                int mixedIndices=checked((int)gpu.Arguments[0]);
                int closeIndices=CaptureIndexCount(new Vector3(0,2,-1),new Vector3(0,0,7),"TestResults/M2/Close.png");
                int farIndices=CaptureIndexCount(new Vector3(0,90,-150),new Vector3(0,0,8),"TestResults/M2/Far.png");
                if(farIndices>=closeIndices||closeIndices<=0||mixedIndices<=0){throw new InvalidOperationException("LOD triangle counts did not decrease with distance.");}
                checks.Add("indices close="+closeIndices+", mixed="+mixedIndices+", far="+farIndices+": PASS");
                checks.Add("meshoptimizer version="+MeshOptimizerSimplifier.Version+": PASS");
                StringBuilder environment=new StringBuilder();
                environment.Append("UTC=").AppendLine(DateTime.UtcNow.ToString("O"));
                environment.Append("Unity=").AppendLine(Application.unityVersion);
                environment.Append("GPU=").AppendLine(SystemInfo.graphicsDeviceName);
                environment.Append("Graphics=").AppendLine(SystemInfo.graphicsDeviceVersion);
                environment.Append("API=").AppendLine(SystemInfo.graphicsDeviceType.ToString());
                environment.Append("GPU memory MiB=").AppendLine(SystemInfo.graphicsMemorySize.ToString(CultureInfo.InvariantCulture));
                environment.Append("Compute=").AppendLine(SystemInfo.supportsComputeShaders.ToString());
                environment.Append("Indirect=").AppendLine(SystemInfo.supportsIndirectArgumentsBuffer.ToString());
                environment.Append("Threshold pixels=").AppendLine(threshold.ToString("R",CultureInfo.InvariantCulture));
                environment.Append("Leaf triangles=").AppendLine(virtualAsset.LeafTriangleCount.ToString(CultureInfo.InvariantCulture));
                environment.Append("Stored triangles=").AppendLine(virtualAsset.TriangleCount.ToString(CultureInfo.InvariantCulture));
                environment.Append("Clusters=").AppendLine(virtualAsset.ClusterCount.ToString(CultureInfo.InvariantCulture));
                environment.Append("Nodes=").AppendLine(virtualAsset.NodeCount.ToString(CultureInfo.InvariantCulture));
                environment.Append("ContentHash=").AppendLine(virtualAsset.ContentHash);
                File.WriteAllText("TestResults/M2/Environment.txt",environment.ToString());
                Finish(null);
            }catch(Exception error){Finish(error);}
        }

        private static float FindMixedThreshold(VirtualGeometryPage page,out VirtualGeometryLodSelectionResult selection){
            for(float threshold=0.0625f;threshold<=64;threshold*=2){
                VirtualGeometryLodSelectionContext context=CreateContext(threshold);
                VirtualGeometryLodSelectionResult candidate=VirtualGeometryLodSelector.Select(page,context,_=>true,null);
                HashSet<uint> levels=new HashSet<uint>();
                foreach(int nodeIndex in candidate.SelectedNodes){levels.Add(page.Nodes[nodeIndex].Level);}
                if(levels.Count>=2){
                    selection=candidate;
                    checks.Add("mixed LOD threshold="+threshold.ToString("R",CultureInfo.InvariantCulture)+", levels="+levels.Count+": PASS");
                    return threshold;
                }
            }
            throw new InvalidOperationException("No tested screen-error threshold produced a mixed-level cut.");
        }

        private static VirtualGeometryLodSelectionContext CreateContext(float threshold){
            return new VirtualGeometryLodSelectionContext(virtualObject.transform.localToWorldMatrix,camera.transform.position,camera.orthographic,
                camera.fieldOfView,camera.orthographicSize,CaptureSize,camera.nearClipPlane,threshold,0);
        }

        private static void CompareSelection(VirtualGeometryPage page,VirtualGeometryLodSelectionResult cpu,VirtualGeometryGpuSelectionSnapshot gpu){
            HashSet<int> expectedNodes=new HashSet<int>(cpu.SelectedNodes);
            HashSet<int> actualNodes=new HashSet<int>();
            for(int i=0;i<gpu.SelectionFlags.Length;i++){if(gpu.SelectionFlags[i]!=0){actualNodes.Add(i);}}
            if(!expectedNodes.SetEquals(actualNodes)){throw new InvalidOperationException("CPU/GPU selected node sets differ.");}
            List<uint> expectedIndices=new List<uint>();
            foreach(int clusterIndex in cpu.SelectedClusters){
                VirtualGeometryCluster cluster=page.Clusters[clusterIndex];
                for(uint i=0;i<cluster.IndexCount;i++){expectedIndices.Add(page.Indices[cluster.FirstIndex+i]);}
            }
            int gpuCount=checked((int)gpu.Arguments[0]);
            int gpuStart=checked((int)gpu.Arguments[2]);
            if(gpuCount!=expectedIndices.Count){throw new InvalidOperationException("CPU/GPU indirect index counts differ: "+expectedIndices.Count+" vs "+gpuCount);}
            for(int i=0;i<gpuCount;i++){
                if(gpu.VisibleIndices[gpuStart+i]!=expectedIndices[i]){throw new InvalidOperationException("CPU/GPU compacted index differs at "+i+".");}
            }
            int[] coverage=new int[CountLeaves(page)];
            foreach(int nodeIndex in cpu.SelectedNodes){
                VirtualGeometryHierarchyNode node=page.Nodes[nodeIndex];
                for(uint leaf=node.LeafStart;leaf<node.LeafStart+node.LeafCount;leaf++){coverage[leaf]++;}
            }
            for(int i=0;i<coverage.Length;i++){if(coverage[i]!=1){throw new InvalidOperationException("Selected cut covers leaf "+i+" "+coverage[i]+" times.");}}
        }

        private static int CaptureIndexCount(Vector3 position,Vector3 target,string path){
            SetCamera(position,target);camera.cullingMask=1<<VirtualLayer;
            Texture2D image=M0Validation.Capture(camera,CaptureSize,CaptureSize);
            File.WriteAllBytes(path,image.EncodeToPNG());Object.DestroyImmediate(image);
            return checked((int)virtualRenderer.ReadbackSelectionForDiagnostics().Arguments[0]);
        }

        private static void WriteSelectionCsv(VirtualGeometryPage page,VirtualGeometryGpuSelectionSnapshot gpu){
            StringBuilder csv=new StringBuilder("node,selected,level,leafStart,leafCount,error\n");
            for(int i=0;i<page.Nodes.Length;i++){
                VirtualGeometryHierarchyNode node=page.Nodes[i];
                csv.Append(i).Append(',').Append(gpu.SelectionFlags[i]).Append(',').Append(node.Level).Append(',').Append(node.LeafStart).Append(',')
                    .Append(node.LeafCount).Append(',').Append(node.GeometricError.ToString("R",CultureInfo.InvariantCulture)).Append('\n');
            }
            File.WriteAllText("TestResults/M2/Selection.csv",csv.ToString());
        }

        private static int CountLeaves(VirtualGeometryPage page){
            int count=0;foreach(VirtualGeometryHierarchyNode node in page.Nodes){if(node.ChildCount==0){count++;}}return count;
        }

        private static void CompareOccupancy(Texture2D reference,Texture2D virtualImage,out Texture2D difference,out int differing,out int union){
            Color32[] left=reference.GetPixels32();Color32[] right=virtualImage.GetPixels32();Color32[] diff=new Color32[left.Length];
            differing=0;union=0;
            for(int i=0;i<left.Length;i++){
                bool a=left[i].b>80;bool b=right[i].b>80;
                if(a||b){union++;}if(a!=b){differing++;diff[i]=new Color32(255,0,0,255);}
                else if(a){diff[i]=new Color32(90,90,90,255);}else{diff[i]=new Color32(0,0,0,255);}
            }
            difference=new Texture2D(reference.width,reference.height,TextureFormat.RGBA32,false,true);
            difference.SetPixels32(diff);difference.Apply();
        }

        private static void SetCamera(Vector3 position,Vector3 target){
            camera.transform.position=position;camera.transform.LookAt(target,Vector3.up);
        }

        private static Mesh CreateTerrain(int width,int depth){
            Vector3[] vertices=new Vector3[(width+1)*(depth+1)];
            Vector3[] normals=new Vector3[vertices.Length];
            Vector2[] uv=new Vector2[vertices.Length];
            for(int z=0;z<=depth;z++){
                for(int x=0;x<=width;x++){
                    int i=z*(width+1)+x;
                    float px=((float)x/width-0.5f)*12;
                    float pz=(float)z/depth*16;
                    float height=Mathf.Sin(px*1.7f+pz*0.27f)*0.42f+Mathf.Cos(pz*1.13f-px*0.31f)*0.28f+
                        Mathf.Sin((px+pz)*3.1f)*0.06f;
                    vertices[i]=new Vector3(px,height,pz);normals[i]=Vector3.up;uv[i]=new Vector2((float)x/width,(float)z/depth);
                }
            }
            int[] indices=new int[width*depth*6];int offset=0;
            for(int z=0;z<depth;z++){
                for(int x=0;x<width;x++){
                    int a=z*(width+1)+x;int b=a+1;int c=a+width+1;int d=c+1;
                    indices[offset++]=a;indices[offset++]=c;indices[offset++]=b;
                    indices[offset++]=b;indices[offset++]=c;indices[offset++]=d;
                }
            }
            Mesh mesh=new Mesh{name="M2 Dense Terrain",indexFormat=IndexFormat.UInt32};
            mesh.vertices=vertices;mesh.normals=normals;mesh.uv=uv;mesh.triangles=indices;mesh.RecalculateBounds();return mesh;
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;
            string result=string.Join(Environment.NewLine,checks)+Environment.NewLine;int exitCode=0;
            if(error!=null){result+="FAIL: "+error;Debug.LogException(error);exitCode=1;}
            else{result+="PASS"+Environment.NewLine;Debug.Log("VG M2 PASS");}
            File.WriteAllText("TestResults/M2/Results.txt",result);
            EditorApplication.Exit(exitCode);
        }
    }
}
