using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using VirtualGeometry.World;
using VirtualizedGeometry;
using VirtualizedGeometry.Editor;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>月とボールの大きさと、月の地形の形。</summary>
    public struct MoonBallSettings{
        public string Name;
        public string ScenePath;
        public float MoonRadius;
        public float MoonHeight;
        public float MoonWavelength;
        public int MoonOctaves;
        public int MoonMaximumLevel;
        public float BallRadius;

        /// <summary>月: 半径 1,737.4 km(Omni の CosmicScale と同じ)、ボール: 半径 0.11 m(直径 0.22 m の 5 号球)。</summary>
        public static MoonBallSettings Moon=>new MoonBallSettings{
            Name="MoonAndBall",ScenePath=MoonBallSceneBuilder.Root+"/MoonAndBall.unity",
            MoonRadius=1737400f,MoonHeight=300f,MoonWavelength=1.6e6f,MoonOctaves=28,MoonMaximumLevel=40,BallRadius=0.11f,
        };

        /// <summary>半径 10^20 m の天体(月の約 6×10^13 倍)とボール。</summary>
        public static MoonBallSettings Giant=>new MoonBallSettings{
            Name="GiantAndBall",ScenePath=MoonBallSceneBuilder.Root+"/GiantAndBall.unity",
            MoonRadius=1e20f,MoonHeight=100f,MoonWavelength=1e19f,MoonOctaves=30,MoonMaximumLevel=90,BallRadius=0.11f,
        };
    }

    /// <summary>build した scene の部品。</summary>
    public sealed class MoonBallObjects{
        public GameObject Moon;
        public GameObject Ball;
        public GameObject CameraObject;
        public GameObject Sun;
        public PlanetTerrainSource MoonSource;
        public PlanetTerrainSource BallSource;
        public MoonBallDemo Demo;
        public Camera Camera;
        public Material MoonMaterial;
        public Material BallMaterial;
    }

    /// <summary>
    /// 月とサッカーボールのデモ scene を作る。Omni の Logical.Transform と RadialCamera を持つ camera、月(半径 1,737.4 km)とボール(半径 0.11 m)を
    /// それぞれ PlanetTerrainSource(cube-sphere の Cell に分けた実行時生成 cluster)で描く。操作は <see cref="MoonBallDemo"/>。
    /// </summary>
    public static class MoonBallSceneBuilder{
        public const string Root="Assets/VirtualizedGeometrySample/MoonAndBall";
        public const string RenderingFolder=Root+"/Rendering";
        public const string MaterialsFolder=Root+"/Materials";
        public const string RendererPath=RenderingFolder+"/MoonAndBallRenderer.asset";
        public const string PipelinePath=RenderingFolder+"/MoonAndBallPipeline.asset";
        public const string MoonMaterialPath=MaterialsFolder+"/Moon.mat";
        public const string BallMaterialPath=MaterialsFolder+"/SoccerBall.mat";
        private const string MoonShaderPath="Packages/com.virtualgeometry.omni/Shaders/RadialMoonGrid.vgshader";
        private const string BallShaderPath="Packages/com.virtualgeometry.omni/Shaders/RadialBall.vgshader";

        [MenuItem("Tools/Virtualized Geometry/Moon And Ball/Use Moon And Ball Pipeline")]
        public static void UsePipeline(){
            UniversalRenderPipelineAsset pipeline=EnsurePipeline();
            GraphicsSettings.defaultRenderPipeline=pipeline;
            QualitySettings.renderPipeline=pipeline;
        }

        [MenuItem("Tools/Virtualized Geometry/Moon And Ball/Build Scenes")]
        private static void BuildFromMenu(){
            if(!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()){return;}
            BuildAll();
            EditorSceneManager.OpenScene(MoonBallSettings.Moon.ScenePath,OpenSceneMode.Single);
        }

        /// <summary>batchmode の入口。pipeline、material、2 つの scene(月、半径 10^20 m)を作り、終了コードで結果を返す。</summary>
        public static void Run(){
            try{
                BuildAll();
                EditorApplication.Exit(0);
            }catch(Exception error){
                Debug.LogException(error);
                EditorApplication.Exit(1);
            }
        }

        public static void BuildAll(){
            UsePipeline();
            BuildScene(MoonBallSettings.Moon);
            BuildScene(MoonBallSettings.Giant);
        }

        public static void BuildScene(MoonBallSettings settings){
            UsePipeline();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            Populate(settings);
            if(!EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(),settings.ScenePath)){
                throw new IOException("Could not save scene: "+settings.ScenePath);
            }
        }

        public static UniversalRenderPipelineAsset EnsurePipeline(){
            Directory.CreateDirectory(RenderingFolder);AssetDatabase.Refresh();
            UniversalRendererData renderer=AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if(renderer==null){
                renderer=ScriptableObject.CreateInstance<UniversalRendererData>();
                renderer.renderingMode=RenderingMode.ForwardPlus;renderer.depthPrimingMode=DepthPrimingMode.Disabled;renderer.copyDepthMode=CopyDepthMode.AfterOpaques;
                AssetDatabase.CreateAsset(renderer,RendererPath);
            }
            VirtualGeometryRendererSetup.EnsureFeature(renderer);
            EditorUtility.SetDirty(renderer);
            UniversalRenderPipelineAsset pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            if(pipeline==null){
                pipeline=UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(pipeline,PipelinePath);
            }
            pipeline.msaaSampleCount=1;pipeline.supportsHDR=false;pipeline.supportsCameraDepthTexture=true;pipeline.renderScale=1;
            EditorUtility.SetDirty(pipeline);
            AssetDatabase.SaveAssets();
            return pipeline;
        }

        public static Material EnsureMaterial(string path,string shaderPath,Action<Material> configure){
            Directory.CreateDirectory(MaterialsFolder);
            Shader shader=AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            if(shader==null){throw new InvalidOperationException("Shader not found: "+shaderPath);}
            Material material=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(material==null){
                material=new Material(shader);
                AssetDatabase.CreateAsset(material,path);
            }
            material.shader=shader;
            configure(material);
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            return material;
        }

        /// <summary>開いている scene に、月・ボール・camera・太陽を置く。</summary>
        public static MoonBallObjects Populate(MoonBallSettings settings){
            var result=new MoonBallObjects();
            result.MoonMaterial=EnsureMaterial(MoonMaterialPath,MoonShaderPath,material=>{
                material.SetColor("_BaseColor",new Color(0.42f,0.41f,0.4f,1));material.SetColor("_GridColor",new Color(0.92f,0.9f,0.82f,1));
                material.SetFloat("_Smoothness",0.05f);material.SetFloat("_FlatColor",0);
            });
            result.BallMaterial=EnsureMaterial(BallMaterialPath,BallShaderPath,material=>{
                material.SetFloat("_Smoothness",0.55f);material.SetFloat("_FlatColor",0);
            });

            // 月
            result.Moon=new GameObject("Moon");result.Moon.SetActive(false);
            result.Moon.AddComponent<global::Omni.Logical.Transform>();
            result.MoonSource=result.Moon.AddComponent<PlanetTerrainSource>();
            result.Moon.AddComponent<OmniPlanetPlacement>();
            Configure(result.MoonSource,settings.MoonRadius,settings.MoonHeight,settings.MoonWavelength,settings.MoonOctaves,settings.MoonMaximumLevel,4096,64,true);
            result.MoonSource.Material=result.MoonMaterial;
            result.Moon.SetActive(true);

            // ボール(高さのない球)
            result.Ball=new GameObject("Soccer Ball");result.Ball.SetActive(false);
            result.Ball.AddComponent<global::Omni.Logical.Transform>();
            result.BallSource=result.Ball.AddComponent<PlanetTerrainSource>();
            result.Ball.AddComponent<OmniPlanetPlacement>();
            Configure(result.BallSource,settings.BallRadius,0f,0.1f,1,10,512,32,false);
            result.BallSource.Material=result.BallMaterial;
            result.Ball.SetActive(true);

            // camera
            result.CameraObject=new GameObject("Main Camera");result.CameraObject.tag="MainCamera";
            result.Camera=result.CameraObject.AddComponent<Camera>();
            result.Camera.fieldOfView=50;result.Camera.nearClipPlane=0.3f;result.Camera.farClipPlane=100000;
            result.Camera.clearFlags=CameraClearFlags.SolidColor;result.Camera.backgroundColor=new Color(0.005f,0.005f,0.012f,1);
            result.Camera.allowHDR=false;result.Camera.allowMSAA=false;
            result.CameraObject.AddComponent<global::Omni.Logical.Transform>();
            // Omni の CosmicScale と同じ: 論理 near = Camera の Near ÷ 10 = 0.03 m。
            global::Omni.RadialCamera radial=result.CameraObject.AddComponent<global::Omni.RadialCamera>();
            radial.SetViewScale(10f);
            PlanetDescentCamera descent=result.CameraObject.AddComponent<PlanetDescentCamera>();
            descent.Planet=result.MoonSource;
            var descentSettings=new SerializedObject(descent);
            descentSettings.FindProperty("eyeHeight").floatValue=0.05f;
            descentSettings.FindProperty("minimumSpeed").floatValue=0.05f;
            descentSettings.ApplyModifiedPropertiesWithoutUndo();
            result.Demo=result.CameraObject.AddComponent<MoonBallDemo>();
            result.Demo.Moon=result.MoonSource;result.Demo.Ball=result.BallSource;result.Demo.Descent=descent;

            // 太陽: camera の後ろの上から、接点の方向へ。
            Vector3 up=new Vector3(0.3f,1f,0.2f).normalized;
            Vector3 u=MoonBallDemo.TangentU(up);Vector3 v=Vector3.Cross(up,u).normalized;
            Vector3 travel=(-up*0.6f-v*0.7f-u*0.3f).normalized;
            result.Sun=new GameObject("Sun");
            Light sun=result.Sun.AddComponent<Light>();sun.type=LightType.Directional;sun.intensity=1.3f;sun.color=new Color(1f,0.96f,0.9f);sun.shadows=LightShadows.None;
            result.Sun.transform.rotation=Quaternion.LookRotation(travel,up);
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(0.03f,0.03f,0.04f);
            RenderSettings.skybox=null;
            return result;
        }

        private static void Configure(PlanetTerrainSource source,float radius,float height,float wavelength,int octaves,int maximumLevel,int pool,int budget,bool moonShape){
            var serialized=new SerializedObject(source);
            serialized.FindProperty("radius").floatValue=radius;
            serialized.FindProperty("heightAmplitude").floatValue=height;
            serialized.FindProperty("baseWavelength").floatValue=wavelength;
            serialized.FindProperty("octaves").intValue=octaves;
            serialized.FindProperty("maximumLevel").intValue=maximumLevel;
            serialized.FindProperty("targetPixels").floatValue=2f;
            serialized.FindProperty("poolCapacity").intValue=pool;
            serialized.FindProperty("maximumDrawSlots").intValue=pool;
            serialized.FindProperty("generationBudget").intValue=budget;
            if(moonShape){
                // 海のない、なだらかな地面(大陸の値が常に海面より上)。山と細部は穏やかに。
                SerializedProperty shape=serialized.FindProperty("heightSettings");
                shape.FindPropertyRelative("SeaLevel").floatValue=-5f;
                shape.FindPropertyRelative("ShoreWidth").floatValue=0.05f;
                shape.FindPropertyRelative("OceanDepth").floatValue=0f;
                shape.FindPropertyRelative("PlainsHeight").floatValue=0.15f;
                shape.FindPropertyRelative("MountainOctave").intValue=3;
                shape.FindPropertyRelative("MountainAmplitude").floatValue=0.45f;
                shape.FindPropertyRelative("DetailOctave").intValue=4;
                shape.FindPropertyRelative("DetailAmplitude").floatValue=0.2f;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
