using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object=UnityEngine.Object;

namespace VirtualizedGeometry.Editor{
    /// <summary>§13の薄形状、穴、鋭角、UV seam、複数materialを通常描画とCPU cutで実GPU比較する。</summary>
    public static class M5GeometryValidation{
        private const string Root="Assets/VirtualizedGeometrySample/M5Geometry",Output="TestResults/M5/Geometry";
        private const int Size=384;private static Camera camera;private static GraphicsBuffer depth;private static VirtualGeometryRenderer instance;
        private static readonly List<string> checks=new List<string>(),errors=new List<string>();private static int frame;
        [Serializable] private sealed class Report{public string unity,gpu,api;public string[] checks;public bool passed;}
        public static void Run(){
            Directory.CreateDirectory(Output);checks.Clear();errors.Clear();frame=0;Application.logMessageReceived+=Log;
            try{Prepare();Setup();EditorApplication.update+=Tick;}catch(Exception error){Finish(error);}
        }
        private static void Log(string message,string stack,LogType type){if(type==LogType.Warning||type==LogType.Error||type==LogType.Exception||type==LogType.Assert){errors.Add(message);}}
        private static void Prepare(){
            Directory.CreateDirectory(Root);AssetDatabase.Refresh();
            string[] names={"Sphere","ThinPlate","ThinColumn","SharpCorners","Hole","UvSeamMaterials"};
            Mesh[] meshes={Sphere(),Box(new Vector3(4,3,.01f),Quaternion.Euler(0,25,15)),Box(new Vector3(.025f,5,.025f),Quaternion.Euler(0,20,17)),Box(new Vector3(3,3,3),Quaternion.Euler(20,30,10)),Ring(),Seam()};
            for(int i=0;i<names.Length;i++){
                meshes[i].name=names[i];
                VirtualGeometryValidationModel.Import(meshes[i],Root+"/"+names[i]+".obj",new VirtualGeometryModelSettings{MaxTrianglesPerCluster=16,HierarchyFanout=4,PositionQuantizationBits=0});
                Object.DestroyImmediate(meshes[i]);
            }
            AssetDatabase.SaveAssets();
        }
        private static void Setup(){
            Directory.CreateDirectory(Root);AssetDatabase.Refresh();EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var renderer=ScriptableObject.CreateInstance<UniversalRendererData>();renderer.renderingMode=RenderingMode.Forward;renderer.depthPrimingMode=DepthPrimingMode.Disabled;renderer.copyDepthMode=CopyDepthMode.AfterTransparents;
            var feature=ScriptableObject.CreateInstance<VirtualGeometryRenderFeature>();var settings=new SerializedObject(feature);
            settings.FindProperty("surfaceShader").objectReferenceValue=Shader.Find("VirtualizedGeometry/Unlit");
            settings.FindProperty("selectionShader").objectReferenceValue=AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.virtualgeometry.core/Shaders/VirtualGeometrySelect.compute");
            settings.ApplyModifiedPropertiesWithoutUndo();feature.Create();renderer.rendererFeatures.Add(feature);
            var probe=ScriptableObject.CreateInstance<VirtualGeometryDepthProbeFeature>();probe.Create();renderer.rendererFeatures.Add(probe);
            var pipeline=UniversalRenderPipelineAsset.Create(renderer);pipeline.msaaSampleCount=1;pipeline.supportsHDR=false;pipeline.supportsCameraDepthTexture=true;
            GraphicsSettings.defaultRenderPipeline=pipeline;QualitySettings.renderPipeline=pipeline;QualitySettings.vSyncCount=0;
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=Color.black;RenderSettings.ambientIntensity=0;RenderSettings.reflectionIntensity=0;RenderSettings.ambientProbe=new SphericalHarmonicsL2();RenderSettings.fog=false;
            var main=new GameObject("Directional reference light").AddComponent<Light>();main.type=LightType.Directional;main.transform.rotation=Quaternion.identity;main.shadows=LightShadows.None;main.intensity=1;RenderSettings.sun=main;
            camera=new GameObject("Geometry camera").AddComponent<Camera>();camera.transform.position=new Vector3(0,0,-8);camera.orthographic=true;camera.orthographicSize=3.5f;camera.nearClipPlane=.1f;camera.farClipPlane=40;
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;camera.allowHDR=false;camera.allowMSAA=false;camera.GetUniversalAdditionalCameraData().requiresDepthTexture=true;
            depth=new GraphicsBuffer(GraphicsBuffer.Target.Structured,Size*Size,4);VirtualGeometryDepthProbeFeature.Target=depth;VirtualGeometryDepthProbeFeature.Width=Size;VirtualGeometryDepthProbeFeature.Height=Size;
        }
        private static void Tick(){
            if(++frame<5){EditorApplication.QueuePlayerLoopUpdate();return;}
            try{
                Validate("Sphere",false,false);
                Validate("ThinPlate",false,false);
                Validate("ThinColumn",false,false);
                Validate("SharpCorners",false,false);
                Validate("Hole",false,true);
                Validate("UvSeamMaterials",true,false);
                if(errors.Count>0){throw new InvalidOperationException(string.Join(" | ",errors));}Finish(null);
            }catch(Exception error){Finish(error);}
        }
        private static void Validate(string name,bool textured,bool hole){
            var mesh=VirtualGeometryValidationModel.SourceMesh(Root+"/"+name+".obj");
            var asset=VirtualGeometryValidationModel.Load(Root+"/"+name+".obj");
            int sourceTriangles=0;for(int m=0;m<mesh.subMeshCount;m++){sourceTriangles+=(int)mesh.GetIndexCount(m)/3;}
            var page=VirtualGeometryPageCodec.Read(File.ReadAllBytes(VirtualGeometryPagePaths.Resolve(asset.PageRelativePath)));
            var reference=new GameObject(name+" reference");reference.layer=8;var filter=reference.AddComponent<MeshFilter>();filter.sharedMesh=mesh;var ordinary=reference.AddComponent<MeshRenderer>();ordinary.shadowCastingMode=ShadowCastingMode.Off;
            var colors=new[]{new Color(.05f,.65f,.95f,1),new Color(.95f,.35f,.08f,1)};var materials=new Material[mesh.subMeshCount];
            var pattern=new Texture2D(4,4,TextureFormat.RGBA32,false,true){filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Repeat};for(int y=0;y<4;y++){for(int x=0;x<4;x++){pattern.SetPixel(x,y,((x+y)%2==0)?Color.white:new Color(.15f,.35f,.6f));}}pattern.Apply();
            for(int m=0;m<materials.Length;m++){materials[m]=new Material(Shader.Find(textured?"Universal Render Pipeline/Lit":"Universal Render Pipeline/Unlit"));materials[m].SetColor("_BaseColor",colors[m%2]);materials[m].SetFloat("_Cull",0);materials[m].SetFloat("_Smoothness",.25f);if(textured){materials[m].SetTexture("_BaseMap",pattern);}}
            ordinary.sharedMaterials=materials;
            var go=new GameObject(name+" virtual");go.layer=9;instance=go.AddComponent<VirtualGeometryRenderer>();instance.SetStreamingConfiguration(VirtualGeometryStreamingMode.AllResident,new VirtualGeometryStreamingSettings());instance.SetMeshAsset(asset);instance.SetProxyOptions(false,false);instance.SetCullingSettings(false,false);instance.SetLodSettings(0,0);
            // 通常MeshRendererと同じMaterialをVGにも割り当て、同じshader(.vgshader経由)で描いた結果を比較する。
            instance.SetMaterials(materials);instance.RefreshAsset();
            int ownedProxies=instance.GetComponentsInChildren<VirtualGeometryProxyOwner>().Length;if(ownedProxies!=1){throw new InvalidOperationException(name+" serialized settings left "+ownedProxies+" proxy roots.");}
            var source=Capture(name+"Source",8,out var sourceDepth);var leaf=Capture(name+"Leaf",9,out var leafDepth);
            if(textured){
                var diagnostic=instance.ReadbackSelectionForDiagnostics();checks.Add("material observation: shader="+instance.GetMaterial(0).shader.name+", fallback="+instance.IsFallbackVisible+", args="+string.Join(",",diagnostic.Arguments));
                for(int m=0;m<materials.Length;m++){uint start=diagnostic.Arguments[m*4+2];checks.Add("material="+m+", indexStart="+start+", firstX="+page.Vertices[diagnostic.VisibleIndices[start]].Position.x+", baseColor="+instance.GetMaterial(m).GetColor("_BaseColor"));}
                foreach(var visual in instance.GetComponentsInChildren<MeshRenderer>()){checks.Add("child="+visual.name+", enabled="+visual.enabled+", shader="+visual.sharedMaterial.shader.name+", shadow="+visual.shadowCastingMode);}
                leaf=Capture(name+"LeafSecond",9,out leafDepth);
            }
            Compare(name+" source/leaf",source,leaf,sourceDepth,leafDepth,true);
            if(instance.IsFallbackVisible){throw new InvalidOperationException(name+" used fallback: "+instance.DiagnosticReason);}
            VerifyAttributes(name,mesh,page);
            Mesh cut=null;
            var cpu=VirtualGeometryLodSelector.Select(page,new VirtualGeometryLodSelectionContext(Matrix4x4.identity,camera.transform.position,true,camera.fieldOfView,camera.orthographicSize,Size,camera.nearClipPlane,float.MaxValue,0),_=>true,null);
            cut=VirtualGeometryValidationModel.CutMesh(page,cpu);filter.sharedMesh=cut;instance.SetLodSettings(float.MaxValue,0);
            var cpuImage=Capture(name+"CpuCoarse",8,out var cpuDepth);var gpuImage=Capture(name+"GpuCoarse",9,out var gpuDepth);Compare(name+" CPU/GPU coarse",cpuImage,gpuImage,cpuDepth,gpuDepth,true);
            var snapshot=instance.ReadbackSelectionForDiagnostics();var actual=new HashSet<int>();for(int i=0;i<snapshot.SelectionFlags.Length;i++){if(snapshot.SelectionFlags[i]!=0){actual.Add(i);}}
            if(!actual.SetEquals(cpu.SelectedNodes)){throw new InvalidOperationException(name+" CPU/GPU node cut differs.");}
            long gpuIndices=0;for(int m=0;m<materials.Length;m++){gpuIndices+=snapshot.Arguments[m*4];if(snapshot.Arguments[m*4]!=cut.GetIndexCount(m)){throw new InvalidOperationException(name+" material index count differs.");}}
            Compare(name+" source/coarse quality",source,gpuImage,sourceDepth,gpuDepth,false);
            if(hole){int center=(Size/2)*Size+Size/2;if(sourceDepth[center]>1e-7f||gpuDepth[center]>1e-7f){throw new InvalidOperationException("Hole was filled.");}checks.Add("Hole center source/coarse depth=0: PASS");}
            checks.Add(name+": sourceTriangles="+sourceTriangles+", coarseTriangles="+gpuIndices/3+", materials="+materials.Length+", nodes="+page.Nodes.Length+", leaf/cut dedicated=true; attributes/CPU nodes/material arguments PASS");
            instance.SetMeshAsset(null);Object.DestroyImmediate(go);Object.DestroyImmediate(reference);Object.DestroyImmediate(cut);foreach(var material in materials){Object.DestroyImmediate(material);}Object.DestroyImmediate(pattern);instance=null;
        }
        private static Color32[] Capture(string name,int layer,out float[] values){camera.cullingMask=1<<layer;var image=M0Validation.Capture(camera,Size,Size);File.WriteAllBytes(Output+"/"+name+".png",image.EncodeToPNG());var colors=image.GetPixels32();Object.DestroyImmediate(image);values=new float[Size*Size];depth.GetData(values);return colors;}
        private static void Compare(string name,Color32[] expected,Color32[] actual,float[] a,float[] b,bool requireExactGeometry){
            int missing=0,xor=0,covered=0,changed=0,maxColor=0;float maxDepth=0;
            for(int i=0;i<a.Length;i++){bool left=a[i]>1e-7f,right=b[i]>1e-7f;if(left){covered++;}if(left&&!right){missing++;}if(left!=right){xor++;}maxDepth=Mathf.Max(maxDepth,Mathf.Abs(a[i]-b[i]));int d=Math.Max(Math.Abs(expected[i].r-actual[i].r),Math.Max(Math.Abs(expected[i].g-actual[i].g),Math.Abs(expected[i].b-actual[i].b)));if(d>0){changed++;}maxColor=Math.Max(maxColor,d);}
            checks.Add(name+": missing="+missing+", silhouetteXor="+xor+", coverage="+covered+", maxDepth="+maxDepth.ToString("R")+", colorChanged="+changed+", maxChannel="+maxColor);
            if(covered<20||(requireExactGeometry&&(missing!=0||xor!=0||maxDepth>1e-6f||maxColor>2))){throw new InvalidOperationException(name+" geometry/material mismatch.");}
        }
        private static void VerifyAttributes(string name,Mesh source,VirtualGeometryPage page){
            // インポート済みモデルのMeshは読み書き無効のため、変換器と同じ読み出しで属性を得る。
            var input=VirtualGeometryMeshInput.FromMesh(source);var vertices=input.Positions;var normals=input.Normals;var uv=input.UV;
            for(int i=0;i<vertices.Length;i++){bool found=false;foreach(var vertex in page.Vertices){if(vertex.Position==vertices[i]&&vertex.Normal==normals[i]&&vertex.UV==uv[i]){found=true;break;}}if(!found){throw new InvalidOperationException(name+" source vertex attributes lost at "+i);}}
        }
        private static Mesh Box(Vector3 scale,Quaternion rotation){var go=GameObject.CreatePrimitive(PrimitiveType.Cube);var mesh=Object.Instantiate(go.GetComponent<MeshFilter>().sharedMesh);Object.DestroyImmediate(go);var vertices=mesh.vertices;var normals=mesh.normals;for(int i=0;i<vertices.Length;i++){vertices[i]=rotation*Vector3.Scale(vertices[i],scale);normals[i]=rotation*normals[i];}mesh.vertices=vertices;mesh.normals=normals;mesh.RecalculateBounds();return mesh;}
        private static Mesh Sphere(){
            var vertices=new List<Vector3>{Vector3.up,Vector3.down,Vector3.left,Vector3.right,Vector3.forward,Vector3.back};var indices=new List<int>{0,4,3,0,2,4,0,5,2,0,3,5,1,3,4,1,4,2,1,2,5,1,5,3};
            for(int level=0;level<3;level++){var edges=new Dictionary<long,int>();var next=new List<int>();int Mid(int a,int b){long key=((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);if(edges.TryGetValue(key,out int cached)){return cached;}int index=vertices.Count;vertices.Add((vertices[a]+vertices[b]).normalized);edges.Add(key,index);return index;}for(int i=0;i<indices.Count;i+=3){int a=indices[i],b=indices[i+1],c=indices[i+2],ab=Mid(a,b),bc=Mid(b,c),ca=Mid(c,a);next.AddRange(new[]{a,ab,ca,ab,b,bc,ca,bc,c,ab,bc,ca});}indices=next;}
            var mesh=new Mesh();mesh.vertices=vertices.Select(v=>v*2.6f).ToArray();mesh.normals=vertices.ToArray();mesh.uv=new Vector2[vertices.Count];mesh.triangles=indices.ToArray();mesh.RecalculateBounds();return mesh;
        }
        private static Mesh Ring(){const int N=96;var vertices=new Vector3[N*2];var uv=new Vector2[N*2];var indices=new int[N*6];for(int i=0;i<N;i++){float angle=i*Mathf.PI*2/N;for(int j=0;j<2;j++){float radius=j==0?1.15f:3;vertices[i*2+j]=new Vector3(Mathf.Cos(angle)*radius,Mathf.Sin(angle)*radius,0);uv[i*2+j]=new Vector2((float)i/N,j);}int a=i*2,b=((i+1)%N)*2;int k=i*6;indices[k]=a;indices[k+1]=a+1;indices[k+2]=b;indices[k+3]=b;indices[k+4]=a+1;indices[k+5]=b+1;}var mesh=new Mesh();mesh.vertices=vertices;mesh.normals=Enumerable.Repeat(Vector3.back,vertices.Length).ToArray();mesh.uv=uv;mesh.triangles=indices;mesh.RecalculateBounds();return mesh;}
        private static Mesh Seam(){
            const int N=16;var vertices=new List<Vector3>();var uv=new List<Vector2>();var groups=new List<int>[2];for(int side=0;side<2;side++){groups[side]=new List<int>();int start=vertices.Count;for(int y=0;y<=N;y++){for(int x=0;x<=N;x++){vertices.Add(new Vector3(-3+side*3+3f*x/N,-2.5f+5f*y/N,0));uv.Add(new Vector2((side==0?1f*x/N:1f-x/(float)N)*1.5f+side*.2f,1.3f*y/N));}}for(int y=0;y<N;y++){for(int x=0;x<N;x++){int a=start+y*(N+1)+x;groups[side].AddRange(new[]{a,a+N+1,a+1,a+1,a+N+1,a+N+2});}}}
            var mesh=new Mesh();mesh.vertices=vertices.ToArray();mesh.normals=Enumerable.Repeat(Vector3.back,vertices.Count).ToArray();mesh.uv=uv.ToArray();mesh.subMeshCount=2;for(int i=0;i<2;i++){mesh.SetTriangles(groups[i],i);}mesh.RecalculateTangents();mesh.RecalculateBounds();return mesh;
        }
        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Log;instance?.SetMeshAsset(null);VirtualGeometryDepthProbeFeature.Target=null;depth?.Dispose();depth=null;
            checks.Add("resources after release="+VirtualGeometryRenderer.SharedResourceCount);if(error!=null){checks.Add("FAIL: "+error);Debug.LogException(error);}else{checks.Add("PASS");Debug.Log("M5 geometry PASS");}
            File.WriteAllText(Output+"/Results.txt",string.Join(Environment.NewLine,checks));File.WriteAllText(Output+"/Results.json",JsonUtility.ToJson(new Report{unity=Application.unityVersion,gpu=SystemInfo.graphicsDeviceName,api=SystemInfo.graphicsDeviceVersion,checks=checks.ToArray(),passed=error==null},true));EditorApplication.Exit(error==null?0:1);
        }
    }
}