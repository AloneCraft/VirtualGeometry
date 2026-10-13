using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using VirtualGeometry.World;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>build した scene の部品。</summary>
    public sealed class PlaneSinkObjects{
        public GameObject ScaledPlane;
        public GameObject Wall;
        public GameObject Ground;
        public GameObject Ball;
        public GameObject CameraObject;
        public GameObject Sun;
        public PlanetTerrainSource GroundSource;
        public PlanetTerrainSource BallSource;
        public PlaneSinkDemo Demo;
        public Camera Camera;
        public Material LeftMaterial;
        public Material WallMaterial;
        public Material GroundMaterial;
        public Material BallMaterial;
    }

    /// <summary>
    /// 「単純に拡大した面」と「VG の面」に同じボールを沈める比べのデモ scene を作る(<see cref="PlaneSinkDemo"/>)。
    /// 左の面と壁は素の MeshRenderer(Omni の Logical.Transform で拡大)、右の面とボールは <see cref="PlanetTerrainSource"/>。
    /// 左の面の mesh は実行時に <see cref="PlaneSinkDemo"/> が作る(巨大な mesh asset を保存しない)。
    /// </summary>
    public static class PlaneSinkSceneBuilder{
        public const string Root="Assets/VirtualizedGeometrySample/PlaneSink";
        public const string MaterialsFolder=Root+"/Materials";
        public const string ScenePath=Root+"/PlaneSink.unity";
        public const string LeftMaterialPath=MaterialsFolder+"/LeftPlane.mat";
        /// <summary>フォーカス専用の目印の全直径(m)。Scene View が表面の試験点のまわりをこの大きさで見る。</summary>
        public const float FocusDiameter=20f;
        public const string WallMaterialPath=MaterialsFolder+"/Wall.mat";
        public const string GroundMaterialPath=MaterialsFolder+"/RightGround.mat";
        public const string BallMaterialPath=MaterialsFolder+"/Ball.mat";
        private const string ShaderFolder="Packages/com.virtualgeometry.omni/Shaders";
        // 左の面と壁は素の MeshRenderer なので .shader、右の面とボールは VG の pass が要るので .vgshader。
        private const string LeftShaderPath=ShaderFolder+"/RadialScaledPlane.shader";
        private const string WallShaderPath=ShaderFolder+"/RadialMoonGrid.shader";
        private const string GroundShaderPath=ShaderFolder+"/RadialMoonGrid.vgshader";
        private const string BallShaderPath=ShaderFolder+"/RadialBall.vgshader";

        [MenuItem("Tools/Virtualized Geometry/Plane Sink/Build Scene")]
        private static void BuildFromMenu(){
            if(!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()){return;}
            BuildScene();
            EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single);
        }

        /// <summary>batchmode の入口。pipeline、material、scene を作り、終了コードで結果を返す。</summary>
        public static void Run(){
            try{
                BuildScene();
                EditorApplication.Exit(0);
            }catch(Exception error){
                Debug.LogException(error);
                EditorApplication.Exit(1);
            }
        }

        public static void BuildScene(){
            MoonBallSceneBuilder.UsePipeline();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            Populate();
            if(!EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(),ScenePath)){
                throw new IOException("Could not save scene: "+ScenePath);
            }
        }

        private static Material EnsureMaterial(string path,string shaderPath,Action<Material> configure){
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

        private static GameObject AddPlainMesh(string name,Material material){
            var result=new GameObject(name);
            result.AddComponent<global::Omni.Logical.Transform>();
            result.AddComponent<MeshFilter>();
            MeshRenderer renderer=result.AddComponent<MeshRenderer>();
            renderer.sharedMaterial=material;
            renderer.shadowCastingMode=ShadowCastingMode.Off;
            renderer.receiveShadows=false;
            renderer.lightProbeUsage=LightProbeUsage.Off;
            renderer.reflectionProbeUsage=ReflectionProbeUsage.Off;
            return result;
        }

        /// <summary>開いている scene に、左の面・壁・右の天体・ボール・camera・太陽を置く。</summary>
        public static PlaneSinkObjects Populate(){
            var result=new PlaneSinkObjects();
            result.LeftMaterial=EnsureMaterial(LeftMaterialPath,LeftShaderPath,material=>{
                material.SetColor("_BaseColor",new Color(0.30f,0.40f,0.55f,1));material.SetColor("_GridColor",new Color(0.92f,0.9f,0.82f,1));
                material.SetFloat("_Smoothness",0.05f);material.SetFloat("_FlatColor",0);
            });
            result.WallMaterial=EnsureMaterial(WallMaterialPath,WallShaderPath,material=>{
                material.SetColor("_BaseColor",new Color(0.45f,0.45f,0.5f,1));material.SetFloat("_Smoothness",0.05f);material.SetFloat("_FlatColor",0);
            });
            result.GroundMaterial=EnsureMaterial(GroundMaterialPath,GroundShaderPath,material=>{
                material.SetColor("_BaseColor",new Color(0.30f,0.50f,0.30f,1));material.SetColor("_GridColor",new Color(0.92f,0.9f,0.82f,1));
                material.SetFloat("_Smoothness",0.05f);material.SetFloat("_FlatColor",0);
            });
            result.BallMaterial=EnsureMaterial(BallMaterialPath,BallShaderPath,material=>{
                material.SetFloat("_Smoothness",0.55f);material.SetFloat("_FlatColor",0);
            });

            PlaneSinkLayout layout=PlaneSinkLayout.Default;

            // 左: 素の mesh を拡大した面と、継ぎ目の壁
            result.ScaledPlane=AddPlainMesh("Scaled Plane (plain scale-up)",result.LeftMaterial);
            result.Wall=AddPlainMesh("Step Wall",result.WallMaterial);

            // 右: VG の天体(高さ 0。上端が試験点)
            result.Ground=new GameObject("VG Ground");result.Ground.SetActive(false);
            result.Ground.AddComponent<global::Omni.Logical.Transform>();
            result.GroundSource=result.Ground.AddComponent<PlanetTerrainSource>();
            result.Ground.AddComponent<OmniPlanetPlacement>();
            MoonBallSceneBuilder.Configure(result.GroundSource,(float)layout.RightRadius,0f,1000f,1,30,4096,64,false);
            result.GroundSource.Material=result.GroundMaterial;
            result.Ground.SetActive(true);

            // VG Ground の Transform は天体の中心(Y=-R)で、Renderer も無く Scale が 1 なので、Hierarchy のダブルクリック(Omni の論理フォーカス)では
            // 中心の 1 m 手前(地表の R 下)へ寄ってしまう。地表の試験点にフォーカス専用の目印を置く(Renderer なし。Scale が全直径になる)。
            var focus=new GameObject("VG Ground Focus (surface)");
            var focusLogical=focus.AddComponent<global::Omni.Logical.Transform>();
            if(global::Omni.Logical.Vector3.TryFromFloat3(new Unity.Mathematics.float3((float)layout.RightSpotX,0,0),out global::Omni.Logical.Vector3 focusPosition)&&
                global::Omni.Logical.Scalar.TryFromFloat(FocusDiameter,out global::Omni.Logical.Scalar focusScale)){
                focusLogical.LocalPosition=focusPosition;
                focusLogical.LocalScale=new global::Omni.Logical.Vector3(in focusScale,in focusScale,in focusScale);
                focusLogical.SyncUnityTransform();
            }

            // ボール(高さのない球)
            result.Ball=new GameObject("Ball");result.Ball.SetActive(false);
            result.Ball.AddComponent<global::Omni.Logical.Transform>();
            result.BallSource=result.Ball.AddComponent<PlanetTerrainSource>();
            result.Ball.AddComponent<OmniPlanetPlacement>();
            MoonBallSceneBuilder.Configure(result.BallSource,0.11f,0f,0.1f,1,10,512,32,false);
            result.BallSource.Material=result.BallMaterial;
            result.Ball.SetActive(true);

            // camera(三人称。位置と向きは PlaneSinkDemo が Logical で決める)
            result.CameraObject=new GameObject("Main Camera");result.CameraObject.tag="MainCamera";
            result.Camera=result.CameraObject.AddComponent<Camera>();
            result.Camera.fieldOfView=50;result.Camera.nearClipPlane=0.3f;result.Camera.farClipPlane=100000;
            result.Camera.clearFlags=CameraClearFlags.SolidColor;result.Camera.backgroundColor=new Color(0.005f,0.005f,0.012f,1);
            result.Camera.allowHDR=false;result.Camera.allowMSAA=false;
            result.CameraObject.AddComponent<global::Omni.Logical.Transform>();
            // 論理 near = Camera の Near ÷ 10 = 0.03 m(MoonAndBall と同じ)。
            global::Omni.RadialCamera radial=result.CameraObject.AddComponent<global::Omni.RadialCamera>();
            radial.SetViewScale(10f);
            result.Demo=result.CameraObject.AddComponent<PlaneSinkDemo>();
            result.Demo.ScaledPlane=result.ScaledPlane;result.Demo.Wall=result.Wall;result.Demo.Ground=result.GroundSource;result.Demo.Ball=result.BallSource;

            // 太陽: camera の後ろの上から。
            result.Sun=new GameObject("Sun");
            Light sun=result.Sun.AddComponent<Light>();sun.type=LightType.Directional;sun.intensity=1.3f;sun.color=new Color(1f,0.96f,0.9f);sun.shadows=LightShadows.None;
            result.Sun.transform.rotation=Quaternion.LookRotation(new Vector3(0.35f,-0.8f,0.5f).normalized,Vector3.up);
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(0.08f,0.08f,0.1f);
            RenderSettings.skybox=null;
            return result;
        }
    }
}
