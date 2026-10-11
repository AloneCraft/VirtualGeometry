using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using VirtualGeometry.World;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>積む 4 つの天体の大きさと、地形の形。</summary>
    public struct BodyStackSettings{
        public string ScenePath;
        public float GroundRadius;
        public float GroundHeight;
        public float GroundWavelength;
        public int GroundOctaves;
        public int GroundMaximumLevel;
        public float MoonRadius;
        public float SphereRadius;
        public float BallRadius;

        /// <summary>巨大な天体: 半径 10^20 m、月: 1,737.4 km、球: 半径 1 m、ボール: 半径 0.11 m。</summary>
        public static BodyStackSettings Default=>new BodyStackSettings{
            ScenePath=BodyStackSceneBuilder.Root+"/BodyStack.unity",
            GroundRadius=1e20f,GroundHeight=100f,GroundWavelength=1e19f,GroundOctaves=30,GroundMaximumLevel=90,
            MoonRadius=1737400f,SphereRadius=1f,BallRadius=0.11f,
        };
    }

    /// <summary>build した scene の部品。</summary>
    public sealed class BodyStackObjects{
        public GameObject[] BodyObjects=new GameObject[BodyStackDemo.BodyCount];
        public PlanetTerrainSource[] Sources=new PlanetTerrainSource[BodyStackDemo.BodyCount];
        public Material[] Materials=new Material[BodyStackDemo.BodyCount];
        public GameObject CameraObject;
        public Camera Camera;
        public GameObject Sun;
        public BodyStackDemo Demo;
    }

    /// <summary>
    /// 4 つの天体(巨大な天体、月、球、ボール)を積んだデモ scene を作る(<see cref="BodyStackDemo"/>)。
    /// pipeline(Renderer)は月とボールのデモのものを使う。
    /// </summary>
    public static class BodyStackSceneBuilder{
        public const string Root="Assets/VirtualizedGeometrySample/BodyStack";
        public const string MaterialsFolder=Root+"/Materials";
        private const string GridShaderPath="Packages/com.virtualgeometry.omni/Shaders/RadialMoonGrid.vgshader";
        private const string BallShaderPath="Packages/com.virtualgeometry.omni/Shaders/RadialBall.vgshader";

        [MenuItem("Tools/Virtualized Geometry/Body Stack/Build Scene")]
        private static void BuildFromMenu(){
            if(!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()){return;}
            BuildScene(BodyStackSettings.Default);
            EditorSceneManager.OpenScene(BodyStackSettings.Default.ScenePath,OpenSceneMode.Single);
        }

        /// <summary>batchmode の入口。scene を作り、終了コードで結果を返す。</summary>
        public static void Run(){
            try{
                BuildScene(BodyStackSettings.Default);
                EditorApplication.Exit(0);
            }catch(Exception error){
                Debug.LogException(error);
                EditorApplication.Exit(1);
            }
        }

        public static void BuildScene(BodyStackSettings settings){
            MoonBallSceneBuilder.UsePipeline();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            Populate(settings);
            if(!EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(),settings.ScenePath)){
                throw new IOException("Could not save scene: "+settings.ScenePath);
            }
        }

        private static Material EnsureMaterial(string name,string shaderPath,Action<Material> configure){
            Directory.CreateDirectory(MaterialsFolder);
            Shader shader=AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            if(shader==null){throw new InvalidOperationException("Shader not found: "+shaderPath);}
            string path=MaterialsFolder+"/"+name+".mat";
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

        private static GameObject AddBody(string name,out PlanetTerrainSource source){
            var body=new GameObject(name);body.SetActive(false);
            body.AddComponent<global::Omni.Logical.Transform>();
            source=body.AddComponent<PlanetTerrainSource>();
            body.AddComponent<OmniPlanetPlacement>();
            return body;
        }

        /// <summary>開いている scene に、4 つの天体・camera・太陽を置く。</summary>
        public static BodyStackObjects Populate(BodyStackSettings settings){
            var result=new BodyStackObjects();
            result.Materials[0]=EnsureMaterial("Ground",GridShaderPath,material=>{
                material.SetColor("_BaseColor",new Color(0.30f,0.40f,0.28f,1));material.SetColor("_GridColor",new Color(0.92f,0.9f,0.82f,1));
                material.SetFloat("_Smoothness",0.05f);material.SetFloat("_FlatColor",0);
            });
            result.Materials[1]=EnsureMaterial("Moon",GridShaderPath,material=>{
                material.SetColor("_BaseColor",new Color(0.42f,0.41f,0.4f,1));material.SetColor("_GridColor",new Color(0.92f,0.9f,0.82f,1));
                material.SetFloat("_Smoothness",0.05f);material.SetFloat("_FlatColor",0);
            });
            result.Materials[2]=EnsureMaterial("Sphere",BallShaderPath,material=>{
                material.SetColor("_WhiteColor",new Color(0.95f,0.55f,0.15f,1));material.SetColor("_BlackColor",new Color(0.1f,0.2f,0.5f,1));
                material.SetColor("_SeamColor",new Color(0.15f,0.15f,0.15f,1));
                material.SetFloat("_Smoothness",0.4f);material.SetFloat("_FlatColor",0);
            });
            result.Materials[3]=EnsureMaterial("Ball",BallShaderPath,material=>{
                material.SetFloat("_Smoothness",0.55f);material.SetFloat("_FlatColor",0);
            });

            result.BodyObjects[0]=AddBody("Ground",out result.Sources[0]);
            MoonBallSceneBuilder.Configure(result.Sources[0],settings.GroundRadius,settings.GroundHeight,settings.GroundWavelength,settings.GroundOctaves,settings.GroundMaximumLevel,4096,64,true);
            result.BodyObjects[1]=AddBody("Moon",out result.Sources[1]);
            MoonBallSceneBuilder.Configure(result.Sources[1],settings.MoonRadius,300f,1.6e6f,28,40,4096,64,true);
            result.BodyObjects[2]=AddBody("Sphere",out result.Sources[2]);
            MoonBallSceneBuilder.Configure(result.Sources[2],settings.SphereRadius,0f,0.1f,1,12,512,32,false);
            result.BodyObjects[3]=AddBody("Soccer Ball",out result.Sources[3]);
            MoonBallSceneBuilder.Configure(result.Sources[3],settings.BallRadius,0f,0.1f,1,10,512,32,false);
            for(int i=0;i<BodyStackDemo.BodyCount;i++){
                result.Sources[i].Material=result.Materials[i];
                result.BodyObjects[i].SetActive(true);
            }

            // camera(Omni の CosmicScale と同じ: 論理 near = Camera の Near ÷ 10 = 0.03 m)
            result.CameraObject=new GameObject("Main Camera");result.CameraObject.tag="MainCamera";
            result.Camera=result.CameraObject.AddComponent<Camera>();
            result.Camera.fieldOfView=50;result.Camera.nearClipPlane=0.3f;result.Camera.farClipPlane=100000;
            result.Camera.clearFlags=CameraClearFlags.SolidColor;result.Camera.backgroundColor=new Color(0.005f,0.005f,0.012f,1);
            result.Camera.allowHDR=false;result.Camera.allowMSAA=false;
            result.CameraObject.AddComponent<global::Omni.Logical.Transform>();
            global::Omni.RadialCamera radial=result.CameraObject.AddComponent<global::Omni.RadialCamera>();
            radial.SetViewScale(10f);
            result.Demo=result.CameraObject.AddComponent<BodyStackDemo>();
            var demoSettings=new SerializedObject(result.Demo);
            SerializedProperty bodies=demoSettings.FindProperty("bodies");
            for(int i=0;i<BodyStackDemo.BodyCount;i++){bodies.GetArrayElementAtIndex(i).objectReferenceValue=result.Sources[i];}
            demoSettings.ApplyModifiedPropertiesWithoutUndo();

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
    }
}
