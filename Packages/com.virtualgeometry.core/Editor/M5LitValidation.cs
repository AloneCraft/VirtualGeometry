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
    /// <summary>専用Litを同じroot meshの通常URP Litと比較し、各入力と影の寄与を実測する。</summary>
    public static class M5LitValidation{
        private const string Root="Assets/VirtualizedGeometrySample/M5";private const int Width=384;private const int Height=288;
        private static VirtualGeometryRenderer instance;private static Camera camera;private static Light main;private static Light point;private static Light spot;
        private static VirtualGeometryMeshAsset asset;private static UniversalRendererData renderer;private static UniversalRenderPipelineAsset pipeline;
        private static GameObject occluder;private static GameObject receiver;private static Texture2D baseMap;private static Texture2D normalMap;
        private static readonly List<string> results=new List<string>();private static readonly List<string> errors=new List<string>();private static int warmup;
        public static void Run(){
            Directory.CreateDirectory("TestResults/M5");results.Clear();errors.Clear();warmup=0;Application.logMessageReceived+=Log;
            try{Setup();EditorApplication.update+=Tick;}catch(Exception error){Finish(error);}
        }
        private static void Log(string message,string stack,LogType type){if(type==LogType.Warning||type==LogType.Error||type==LogType.Exception||type==LogType.Assert){errors.Add(message);}}
        private static void Setup(){
            renderer=AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Root+"/M5Renderer.asset");pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Root+"/M5Pipeline.asset");
            if(renderer==null||pipeline==null){throw new InvalidOperationException("Run M5SharedValidation first.");}
            renderer.copyDepthMode=CopyDepthMode.AfterOpaques;renderer.depthPrimingMode=DepthPrimingMode.Disabled;
            var pipelineSettings=new SerializedObject(pipeline);pipelineSettings.FindProperty("m_MainLightShadowsSupported").boolValue=true;pipelineSettings.FindProperty("m_AdditionalLightsRenderingMode").intValue=(int)LightRenderingMode.PerPixel;pipelineSettings.ApplyModifiedPropertiesWithoutUndo();pipeline.shadowDistance=40;pipeline.shadowCascadeCount=1;pipeline.mainLightShadowmapResolution=2048;
            pipeline.maxAdditionalLightsCount=8;
            GraphicsSettings.defaultRenderPipeline=pipeline;QualitySettings.renderPipeline=pipeline;QualitySettings.vSyncCount=0;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=Color.black;RenderSettings.ambientIntensity=0;RenderSettings.reflectionIntensity=0;RenderSettings.ambientProbe=new SphericalHarmonicsL2();
            Mesh source=AssetDatabase.LoadAssetAtPath<Mesh>(Root+"/LitSource.asset");
            if(source==null){source=CreatePlane();AssetDatabase.CreateAsset(source,Root+"/LitSource.asset");}
            asset=VirtualGeometryConversionService.Convert(source,Root+"/LitConverted.asset",new VirtualGeometryBuildSettings{MaxTrianglesPerCluster=32,HierarchyFanout=4});
            camera=new GameObject("Lit camera").AddComponent<Camera>();camera.transform.position=new Vector3(0,0,-8);camera.orthographic=true;camera.orthographicSize=3.5f;
            camera.nearClipPlane=0.1f;camera.farClipPlane=40;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;camera.allowHDR=false;camera.allowMSAA=false;
            camera.GetUniversalAdditionalCameraData().requiresDepthTexture=true;
            instance=new GameObject("Lit virtual surface").AddComponent<VirtualGeometryRenderer>();instance.SetStreamingConfiguration(VirtualGeometryStreamingMode.AllResident,new VirtualGeometryStreamingSettings());
            instance.SetMeshAsset(asset);instance.SetLodSettings(float.MaxValue,0);instance.SetCullingSettings(false,false);instance.SetProxyOptions(false,true);
            main=new GameObject("Main directional").AddComponent<Light>();main.type=LightType.Directional;main.transform.rotation=Quaternion.Euler(20,-25,0);main.intensity=1;main.color=Color.white;main.shadows=LightShadows.None;RenderSettings.sun=main;
            point=new GameObject("Point").AddComponent<Light>();point.type=LightType.Point;point.transform.position=new Vector3(-1,0,-2);point.range=10;point.intensity=5;point.color=new Color(0.2f,0.7f,1);point.enabled=false;
            spot=new GameObject("Spot").AddComponent<Light>();spot.type=LightType.Spot;spot.transform.position=new Vector3(1,0,-3);spot.transform.rotation=Quaternion.identity;spot.range=10;spot.spotAngle=65;spot.innerSpotAngle=35;spot.intensity=8;spot.color=new Color(1,0.4f,0.2f);spot.enabled=false;
            occluder=GameObject.CreatePrimitive(PrimitiveType.Cube);occluder.name="Ordinary shadow caster";occluder.transform.position=new Vector3(-0.3f,0.3f,-1);occluder.transform.localScale=new Vector3(1,1,0.25f);
            occluder.GetComponent<MeshRenderer>().sharedMaterial=asset.ProxyLitMaterial;occluder.SetActive(false);
            baseMap=CreateTexture("BasePattern",false);normalMap=CreateTexture("NormalPattern",true);
            EditorSceneManager.SaveScene(camera.gameObject.scene,Root+"/M5Lit.unity");AssetDatabase.SaveAssets();
        }
        private static Mesh CreatePlane(){
            const int N=16;Vector3[] vertices=new Vector3[(N+1)*(N+1)];Vector3[] normals=new Vector3[vertices.Length];Vector2[] uv=new Vector2[vertices.Length];int[] indices=new int[N*N*6];
            for(int y=0;y<=N;y++){for(int x=0;x<=N;x++){int i=y*(N+1)+x;vertices[i]=new Vector3((float)x/N*8-4,(float)y/N*6-3,0);normals[i]=Vector3.back;uv[i]=new Vector2((float)x/N,(float)y/N);}}
            int next=0;for(int y=0;y<N;y++){for(int x=0;x<N;x++){int a=y*(N+1)+x;indices[next++]=a;indices[next++]=a+N+1;indices[next++]=a+1;indices[next++]=a+1;indices[next++]=a+N+1;indices[next++]=a+N+2;}}
            Mesh mesh=new Mesh{name="M5 Lit plane"};mesh.vertices=vertices;mesh.normals=normals;mesh.uv=uv;mesh.triangles=indices;mesh.RecalculateTangents();mesh.RecalculateBounds();return mesh;
        }
        private static Texture2D CreateTexture(string name,bool normal){
            string path=Root+"/"+name+".png";Texture2D image=new Texture2D(8,8,TextureFormat.RGBA32,false,true);
            for(int y=0;y<8;y++){for(int x=0;x<8;x++){image.SetPixel(x,y,normal?new Color(0.15f,0.5f,0.8570714f,1):((x+y)%2==0?new Color(0.6f,0.25f,0.1f,1):new Color(0.15f,0.65f,0.8f,1)));}}
            image.Apply();File.WriteAllBytes(path,image.EncodeToPNG());Object.DestroyImmediate(image);AssetDatabase.ImportAsset(path);
            var importer=(TextureImporter)AssetImporter.GetAtPath(path);importer.textureType=normal?TextureImporterType.NormalMap:TextureImporterType.Default;importer.sRGBTexture=!normal;importer.textureCompression=TextureImporterCompression.Uncompressed;importer.mipmapEnabled=false;importer.filterMode=FilterMode.Point;importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
        /// <summary>検証用のURP Lit設定。1つのMaterialへ値とkeywordを反映し、VGとProxyの両方が同じMaterialで描く。</summary>
        private sealed class LitSurface{
            public Color BaseColor=Color.white;public Texture2D BaseMap;public Texture2D NormalMap;public float Metallic;public float Smoothness=0.5f;
            public Vector4 BaseMapTransform=new Vector4(1,1,0,0);
            private Material material;
            public Material Apply(){
                if(this.material==null){this.material=new Material(asset.ProxyLitMaterial){name="M5 Lit surface"};}
                this.material.SetColor("_BaseColor",this.BaseColor);this.material.SetFloat("_Metallic",this.Metallic);this.material.SetFloat("_Smoothness",this.Smoothness);
                Texture baseMap=this.BaseMap;if(baseMap==null){baseMap=Texture2D.whiteTexture;}
                this.material.SetTexture("_BaseMap",baseMap);
                this.material.SetTextureScale("_BaseMap",new Vector2(this.BaseMapTransform.x,this.BaseMapTransform.y));this.material.SetTextureOffset("_BaseMap",new Vector2(this.BaseMapTransform.z,this.BaseMapTransform.w));
                if(this.NormalMap!=null){this.material.SetTexture("_BumpMap",this.NormalMap);this.material.EnableKeyword("_NORMALMAP");}
                else{this.material.SetTexture("_BumpMap",asset.ProxyLitMaterial.GetTexture("_BumpMap"));this.material.DisableKeyword("_NORMALMAP");}
                return this.material;
            }
        }
        private static Color32[] Capture(string name,bool proxy){
            instance.SetFallbackOverride(proxy);Texture2D image=M0Validation.Capture(camera,Width,Height);Color32[] pixels=image.GetPixels32();File.WriteAllBytes("TestResults/M5/"+name+".png",image.EncodeToPNG());Object.DestroyImmediate(image);
            if(instance.IsFallbackVisible!=proxy){throw new InvalidOperationException("Unexpected fallback in "+name+": "+instance.DiagnosticReason);}return pixels;
        }
        private static Color32[] Compare(string name,LitSurface surface){
                        instance.SetMaterials(surface.Apply());
            Color32[] reference=Capture(name+"Proxy",true);Color32[] actual=Capture(name+"Virtual",false);
            results.Add(name+" center proxy="+reference[(Height/2)*Width+Width/2]+", virtual="+actual[(Height/2)*Width+Width/2]);
            Difference(reference,actual,out double mean,out int maximum,out int changed);results.Add(name+": mean RGB error="+mean.ToString("R")+", max channel error="+maximum+", changed pixels="+changed);
            if(mean>0.008||maximum>12){errors.Add(name+" differs from URP Lit: "+mean+" / "+maximum);}return actual;
        }
        private static void Difference(Color32[] a,Color32[] b,out double mean,out int maximum,out int changed){
            long sum=0;maximum=0;changed=0;for(int i=0;i<a.Length;i++){int r=Math.Abs(a[i].r-b[i].r),g=Math.Abs(a[i].g-b[i].g),v=Math.Abs(a[i].b-b[i].b);sum+=r+g+v;maximum=Math.Max(maximum,Math.Max(r,Math.Max(g,v)));if(r+g+v>3){changed++;}}mean=(double)sum/(a.Length*3*255);
        }
        private static void Changed(string name,Color32[] a,Color32[] b){Difference(a,b,out double mean,out int maximum,out int changed);results.Add(name+": contribution mean="+mean.ToString("R")+", changed pixels="+changed);if(changed<100){errors.Add(name+" has no measurable contribution.");}}
        private static void Tick(){
            if(++warmup<5){EditorApplication.QueuePlayerLoopUpdate();return;}
            try{
                var surface=new LitSurface{BaseColor=new Color(0.7f,0.55f,0.3f,1),Smoothness=0.25f};
                results.Add("normal texture format="+normalMap.format+", Proxy normal keyword="+asset.ProxyLitMaterial.IsKeywordEnabled("_NORMALMAP"));
                RenderSettings.ambientProbe=new SphericalHarmonicsL2();
                Color32[] baseline=Compare("LitBase",surface);
                RenderSettings.ambientLight=new Color(0.6f,0.35f,0.15f);RenderSettings.ambientIntensity=1;var ambient=new SphericalHarmonicsL2();ambient.AddAmbientLight(new Color(0.6f,0.35f,0.15f));RenderSettings.ambientProbe=ambient;Color32[] ambientImage=Compare("LitAmbient",surface);Changed("Ambient",baseline,ambientImage);results.Add("ambient SH00="+RenderSettings.ambientProbe[0,0]);RenderSettings.ambientLight=Color.black;RenderSettings.ambientIntensity=0;RenderSettings.ambientProbe=new SphericalHarmonicsL2();
                surface.BaseMap=baseMap;Color32[] textured=Compare("LitBaseMap",surface);Changed("Base map",baseline,textured);
                surface.NormalMap=normalMap;Color32[] normal=Compare("LitNormal",surface);Changed("Normal map",textured,normal);
                                surface.BaseMapTransform=new Vector4(-1,1,1,0);Compare("LitMirroredTextureScale",surface);surface.BaseMapTransform=new Vector4(1,1,0,0);
                instance.transform.localScale=new Vector3(-0.8f,0.7f,1.3f);Compare("LitNegativeNonuniformScale",surface);instance.transform.localScale=Vector3.one;
                surface.NormalMap=null;surface.Metallic=0.8f;Color32[] metal=Compare("LitMetal",surface);Changed("Metallic",textured,metal);
                surface.Smoothness=0.8f;Color32[] smooth=Compare("LitSmooth",surface);Changed("Smoothness",metal,smooth);
                surface.Metallic=0;surface.Smoothness=0.25f;point.enabled=true;Color32[] pointImage=Compare("LitPoint",surface);Changed("Point",textured,pointImage);
                point.enabled=false;spot.enabled=true;Color32[] spotImage=Compare("LitSpot",surface);Changed("Spot",textured,spotImage);spot.enabled=false;
                RenderSettings.fog=true;RenderSettings.fogMode=FogMode.Linear;RenderSettings.fogStartDistance=2;RenderSettings.fogEndDistance=15;RenderSettings.fogColor=new Color(0.05f,0.1f,0.2f);
                Color32[] fog=Compare("LitFog",surface);Changed("Fog",textured,fog);RenderSettings.fog=false;
                surface.BaseMap=null;occluder.SetActive(true);main.shadows=LightShadows.None;Color32[] noShadow=Compare("LitNoShadow",surface);
                main.shadows=LightShadows.Hard;Color32[] shadow=Compare("LitShadowReceive",surface);Changed("Main shadow receive",noShadow,shadow);
                TestShadowProxy(surface);
                if(errors.Count>0){throw new InvalidOperationException(string.Join(" | ",errors));}Finish(null);
            }catch(Exception error){Finish(error);}
        }
        private static void TestShadowProxy(LitSurface surface){
            occluder.SetActive(false);instance.transform.localScale=new Vector3(0.35f,0.4f,1);instance.transform.position=new Vector3(-0.8f,0,-0.6f);instance.SetMaterials(surface.Apply());
            receiver=GameObject.CreatePrimitive(PrimitiveType.Quad);receiver.name="Ordinary shadow receiver";receiver.transform.position=new Vector3(0,0,0.6f);receiver.transform.localScale=new Vector3(8,6,1);
            var material=new Material(Shader.Find("Universal Render Pipeline/Lit"));material.SetColor("_BaseColor",new Color(0.7f,0.7f,0.7f));material.SetFloat("_Smoothness",0.1f);receiver.GetComponent<MeshRenderer>().sharedMaterial=material;
            instance.SetProxyOptions(false,true);Color32[] off=Capture("ShadowProxyOff",false);instance.SetProxyOptions(true,true);Color32[] on=Capture("ShadowProxyOn",false);Changed("Shadow Proxy casting onto ordinary mesh",off,on);
            Object.DestroyImmediate(material);
        }
        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Log;instance?.SetMeshAsset(null);string text=string.Join(Environment.NewLine,results)+Environment.NewLine;
            if(error!=null){text+="FAIL: "+error;Debug.LogException(error);}else{text+="PASS: Lit/shadow scope only";Debug.Log(text);}File.WriteAllText("TestResults/M5/LitResults.txt",text);EditorApplication.Exit(error==null?0:1);
        }
    }
}