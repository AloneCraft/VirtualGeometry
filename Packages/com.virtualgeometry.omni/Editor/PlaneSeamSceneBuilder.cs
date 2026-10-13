using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using VirtualGeometry.World;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>build した scene の部品。</summary>
    public sealed class PlaneSeamObjects{
        public GameObject ScaledPlane;
        public GameObject Ground;
        public GameObject CameraObject;
        public GameObject Sun;
        public PlanarTerrainSource GroundSource;
        public PlaneSeamDemo Demo;
        public Camera Camera;
        public Material LeftMaterial;
        public Material GroundMaterial;
    }

    /// <summary>
    /// 「単純に拡大した平面」と「VG の平面」を同じ高さで隣り合わせるデモ scene を作る(<see cref="PlaneSeamDemo"/>)。
    /// 左の面は素の MeshRenderer(Omni の Logical.Transform で拡大)、右の面は <see cref="PlanarTerrainSource"/>。
    /// 左の面の mesh は実行時に <see cref="PlaneSeamDemo"/> が作る(巨大な mesh asset を保存しない)。
    /// </summary>
    public static class PlaneSeamSceneBuilder{
        public const string Root="Assets/VirtualizedGeometrySample/PlaneSeam";
        public const string MaterialsFolder=Root+"/Materials";
        public const string ScenePath=Root+"/PlaneSeam.unity";
        public const string LeftMaterialPath=MaterialsFolder+"/LeftPlane.mat";
        public const string GroundMaterialPath=MaterialsFolder+"/RightPlane.mat";
        private const string ShaderFolder="Packages/com.virtualgeometry.omni/Shaders";
        // 左の面は素の MeshRenderer なので .shader、右の面は VG の pass が要るので .vgshader。
        private const string LeftShaderPath=ShaderFolder+"/RadialScaledPlane.shader";
        private const string GroundShaderPath=ShaderFolder+"/RadialMoonGrid.vgshader";

        [MenuItem("Tools/Virtualized Geometry/Plane Seam/Build Scene")]
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
            Directory.CreateDirectory(Root);
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

        /// <summary>開いている scene に、左の面・右の面・camera・太陽を置く。</summary>
        public static PlaneSeamObjects Populate(){
            var result=new PlaneSeamObjects();
            result.LeftMaterial=EnsureMaterial(LeftMaterialPath,LeftShaderPath,material=>{
                material.SetColor("_BaseColor",new Color(0.30f,0.40f,0.55f,1));material.SetColor("_GridColor",new Color(0.92f,0.9f,0.82f,1));
                material.SetFloat("_Smoothness",0.05f);material.SetFloat("_FlatColor",0);
                // Omni は Play の準備で、この asset の値から Radial 版の材質(sidecar)を作る。格子の値(左の面の試験点の物体空間の位置と倍率)が
                // 初期値のままだと、Play 中に PlaneSeamDemo が書いても描画に届かず、格子が潰れる(面が一色になる)ので、既定の配置の値を保存しておく。
                PlaneSeamLayout layout=PlaneSeamLayout.Default;
                material.SetVector("_GridOriginOS",new Vector4((float)((layout.LeftSpotX-layout.LeftOriginX)/layout.PlaneScale),0,0,0));
                material.SetFloat("_ObjectScale",(float)layout.PlaneScale);
            });
            result.GroundMaterial=EnsureMaterial(GroundMaterialPath,GroundShaderPath,material=>{
                material.SetColor("_BaseColor",new Color(0.30f,0.50f,0.30f,1));material.SetColor("_GridColor",new Color(0.92f,0.9f,0.82f,1));
                material.SetFloat("_Smoothness",0.05f);material.SetFloat("_FlatColor",0);
            });

            // 左: 素の mesh を拡大した面
            result.ScaledPlane=new GameObject("Scaled Plane (plain scale-up)");
            result.ScaledPlane.AddComponent<global::Omni.Logical.Transform>();
            result.ScaledPlane.AddComponent<MeshFilter>();
            MeshRenderer renderer=result.ScaledPlane.AddComponent<MeshRenderer>();
            renderer.sharedMaterial=result.LeftMaterial;
            renderer.shadowCastingMode=ShadowCastingMode.Off;
            renderer.receiveShadows=false;
            renderer.lightProbeUsage=LightProbeUsage.Off;
            renderer.reflectionProbeUsage=ReflectionProbeUsage.Off;

            // 右: VG の平面(高さ 0)
            result.Ground=new GameObject("VG Plane");result.Ground.SetActive(false);
            result.Ground.AddComponent<global::Omni.Logical.Transform>();
            result.GroundSource=result.Ground.AddComponent<PlanarTerrainSource>();
            result.Ground.AddComponent<OmniPlanetPlacement>();
            Configure(result.GroundSource,(float)PlaneSeamLayout.Default.RightExtent,32,4096,64);
            result.GroundSource.Material=result.GroundMaterial;
            result.Ground.SetActive(true);

            // camera(自由。位置と向きは PlaneSeamDemo が Logical で決める)
            result.CameraObject=new GameObject("Main Camera");result.CameraObject.tag="MainCamera";
            result.Camera=result.CameraObject.AddComponent<Camera>();
            result.Camera.fieldOfView=50;result.Camera.nearClipPlane=0.3f;result.Camera.farClipPlane=100000;
            result.Camera.clearFlags=CameraClearFlags.SolidColor;result.Camera.backgroundColor=new Color(0.005f,0.005f,0.012f,1);
            result.Camera.allowHDR=false;result.Camera.allowMSAA=false;
            result.CameraObject.AddComponent<global::Omni.Logical.Transform>();
            // 論理 near = Camera の Near ÷ 10 = 0.03 m(MoonAndBall と同じ)。
            global::Omni.RadialCamera radial=result.CameraObject.AddComponent<global::Omni.RadialCamera>();
            radial.SetViewScale(10f);
            result.Demo=result.CameraObject.AddComponent<PlaneSeamDemo>();
            result.Demo.ScaledPlane=result.ScaledPlane;result.Demo.Ground=result.GroundSource;

            // 太陽: camera の後ろの上から。
            result.Sun=new GameObject("Sun");
            Light sun=result.Sun.AddComponent<Light>();sun.type=LightType.Directional;sun.intensity=1.3f;sun.color=new Color(1f,0.96f,0.9f);sun.shadows=LightShadows.None;
            result.Sun.transform.rotation=Quaternion.LookRotation(new Vector3(0.35f,-0.8f,0.5f).normalized,Vector3.up);
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(0.08f,0.08f,0.1f);
            RenderSettings.skybox=null;
            return result;
        }

        internal static void Configure(PlanarTerrainSource source,float extent,int maximumLevel,int pool,int budget){
            var serialized=new SerializedObject(source);
            serialized.FindProperty("extent").floatValue=extent;
            serialized.FindProperty("maximumLevel").intValue=maximumLevel;
            serialized.FindProperty("targetPixels").floatValue=2f;
            serialized.FindProperty("poolCapacity").intValue=pool;
            serialized.FindProperty("maximumDrawSlots").intValue=pool;
            serialized.FindProperty("generationBudget").intValue=budget;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
