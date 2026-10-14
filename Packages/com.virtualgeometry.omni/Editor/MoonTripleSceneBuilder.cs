using System;
using System.IO;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using VirtualGeometry.World;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>build した scene の部品。</summary>
    public sealed class MoonTripleObjects{
        /// <summary>4 枚の面の根(<see cref="MoonTripleFace"/> の順)。</summary>
        public GameObject[] Faces=new GameObject[4];
        public PlanarTerrainSource Vg;
        public GameObject Ball;
        public GameObject CameraObject;
        public GameObject Sun;
        public MoonTripleDemo Demo;
        public Camera Camera;
        public Material[] FaceMaterials=new Material[4];
        public Material TileMaterial;
        public Material BallMaterial;
    }

    /// <summary>
    /// 4 枚の平面とサッカーボールのデモ scene を作る(<see cref="MoonTripleDemo"/>)。角が原点で接し、すべて y=0(配置は <see cref="MoonTripleLayout"/>)。
    /// VG の面: <see cref="PlanarTerrainSource"/>(一辺 1000 km、最大階層は Cell が 10 m 以下になる最小の値)。
    /// Standard100Km・Standard1000Km: 10 m の標準 Plane の mesh を、Logical で N 倍に拡大した 1 つの物体(素の MeshRenderer)。
    /// 手動分割 1000m: 10 m の標準 Plane の mesh を 100×100 枚、Logical で並べた面(素の MeshRenderer)。
    /// 素の面の材質は <c>Omni/RadialScaledPlane</c>(PlaneSeam と同じ)。格子の原点(_GridOriginOS)と倍率(_ObjectScale)は、材質に書く(Play で書き換えない)。
    /// </summary>
    public static class MoonTripleSceneBuilder{
        public const string Root="Assets/VirtualizedGeometrySample/MoonTriple";
        public const string MaterialsFolder=Root+"/Materials";
        public const string ScenePath=Root+"/MoonTriple.unity";
        private const string ShaderFolder="Packages/com.virtualgeometry.omni/Shaders";
        // 素の面は素の MeshRenderer なので .shader、VG の面は VG の pass が要るので .vgshader。
        private const string PlainShaderPath=ShaderFolder+"/RadialScaledPlane.shader";
        private const string VgShaderPath=ShaderFolder+"/RadialMoonGrid.vgshader";
        // ボールは Omni のサッカーボール(CosmicPlanetLit)。
        public const string BallMaterialPath=MaterialsFolder+"/SoccerBall.mat";
        public const string CosmicShaderPath=Root+"/OmniCosmic/CosmicPlanetLit.shader";
        // 4 枚の面の材質(MoonTripleFace の順)と、手動分割の 1 枚の材質。
        private static readonly string[] FaceMaterialPaths={
            MaterialsFolder+"/FaceVg.mat",
            MaterialsFolder+"/FaceStandard100Km.mat",
            MaterialsFolder+"/FaceStandard1000Km.mat",
            MaterialsFolder+"/FaceManualTile.mat",
        };
        // 1 枚の一辺(m)。Unity の標準 Plane の mesh は一辺 10 m(±5)。
        private const float PlaneMeters=10f;
        // VG の面の材質の値。
        private const float VgTargetPixels=MoonTripleDemo.VgTargetPixels;
        private const int VgPool=4096;
        private const int VgBudget=64;

        [MenuItem("Tools/Virtualized Geometry/Moon Triple/Build Scene")]
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
            MoonTripleObjects objects=Populate(MoonTripleLayout.Default);
            // Play 開始と同じ配置(4 枚の面・ボール・camera)を、scene に焼き込む。焼き込まないと、Play を止めた Edit Mode で全部が原点に重なって、何も表示されない。
            // Initialize は、VG の面の Selector が使えるとき(MoonsReady)に呼ぶ。
            if(!objects.Demo.MoonsReady()){throw new InvalidOperationException("VG の面の準備ができていません(Selector が無い)。配置を焼き込めません。");}
            if(!objects.Demo.Initialize()){throw new InvalidOperationException("MoonTripleDemo.Initialize が失敗しました: "+objects.Demo.Failure);}
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

        // 素の面の材質の値: 色、格子(物体空間の原点と倍率)。格子の値は、Omni が Play の準備で材質から Radial 版を作るので、材質に書いておく(PlaneSeam と同じ)。
        private static void ConfigurePlainMaterial(Material material,Color color,MoonTripleLayout layout,MoonTripleFace face,double scale){
            material.SetColor("_BaseColor",color);material.SetColor("_GridColor",new Color(0.92f,0.9f,0.82f,1));
            material.SetFloat("_Smoothness",0.05f);material.SetFloat("_FlatColor",0);
            double3 corner=layout.CornerInObject(face);
            material.SetVector("_GridOriginOS",new Vector4((float)corner.x,(float)corner.y,(float)corner.z,0));
            material.SetFloat("_ObjectScale",(float)scale);
        }

        // Unity の標準 Plane の mesh(一辺 10 m)。素の面に使う。シーンに置いた後に消すので、一時の物体で取り出す。
        private static Mesh PlaneMesh(){
            GameObject temp=GameObject.CreatePrimitive(PrimitiveType.Plane);
            try{
                Mesh mesh=temp.GetComponent<MeshFilter>().sharedMesh;
                Vector3 size=mesh.bounds.size;
                if(Mathf.Abs(size.x-PlaneMeters)>1e-3f||Mathf.Abs(size.z-PlaneMeters)>1e-3f){
                    throw new InvalidOperationException("標準 Plane の一辺が "+PlaneMeters+" m ではありません: "+size);
                }
                return mesh;
            }finally{
                UnityEngine.Object.DestroyImmediate(temp);
            }
        }

        // 素の面(Logical の物体。標準 Plane の mesh、材質は RadialScaledPlane)。
        private static GameObject AddPlainPiece(string name,Mesh mesh,Material material,Transform parent){
            var owner=new GameObject(name);
            if(parent!=null){owner.transform.SetParent(parent,false);}
            owner.AddComponent<global::Omni.Logical.Transform>();
            owner.AddComponent<MeshFilter>().sharedMesh=mesh;
            MeshRenderer renderer=owner.AddComponent<MeshRenderer>();
            renderer.sharedMaterial=material;
            renderer.shadowCastingMode=ShadowCastingMode.Off;
            renderer.receiveShadows=false;
            renderer.lightProbeUsage=LightProbeUsage.Off;
            renderer.reflectionProbeUsage=ReflectionProbeUsage.Off;
            return owner;
        }

        /// <summary>開いている scene に、4 枚の面・ボール・camera・太陽を置く。</summary>
        public static MoonTripleObjects Populate(MoonTripleLayout layout){
            var result=new MoonTripleObjects();
            Mesh plane=PlaneMesh();
            Mesh ballMesh=AssetDatabase.LoadAssetAtPath<Mesh>(Root+"/Meshes/CosmicSphere.asset");
            if(ballMesh==null){throw new InvalidOperationException("単位球のメッシュがありません: "+Root+"/Meshes/CosmicSphere.asset");}

            // 材質: 4 枚の面(VG は格子の VG 材質、Standard と手動分割は RadialScaledPlane)。
            for(int f=0;f<4;f++){
                var face=(MoonTripleFace)f;
                Color color=MoonTripleDemo.FaceColors[f];
                if(face==MoonTripleFace.Vg){
                    result.FaceMaterials[f]=EnsureMaterial(FaceMaterialPaths[f],VgShaderPath,material=>{
                        material.SetColor("_BaseColor",color);material.SetColor("_GridColor",new Color(0.92f,0.9f,0.82f,1));
                        material.SetFloat("_Smoothness",0.05f);material.SetFloat("_FlatColor",0);
                        material.SetVector("_GridAxisU",new Vector4(1,0,0,0));material.SetVector("_GridAxisV",new Vector4(0,0,1,0));
                    });
                }else{
                    double scale=face==MoonTripleFace.Manual?1:layout.StandardScale(face);
                    result.FaceMaterials[f]=EnsureMaterial(FaceMaterialPaths[f],PlainShaderPath,material=>{
                        ConfigurePlainMaterial(material,color,layout,face,scale);
                    });
                }
            }
            result.TileMaterial=result.FaceMaterials[(int)MoonTripleFace.Manual];
            result.BallMaterial=EnsureMaterial(BallMaterialPath,CosmicShaderPath,material=>{
                // Omni の SoccerBall.mat と同じ値(CosmicBody: 階層の段、種類 2 = サッカーボール、シード)。
                material.SetColor("_BaseColor",Color.white);
                material.SetVector("_CosmicBody",new Vector4(7,2,5,0));
                material.SetVector("_CosmicP0",new Vector4(0.41f,0.67f,0.29f,0.11f));
                material.SetVector("_CosmicP1",new Vector4(0,-1,0,0.4f));
                // 赤道の線(MoonTriple が足したシェーダーの追加): x = 帯の半幅 3.5 mm、yzw = 赤。
                material.SetVector("_CosmicP2",new Vector4(0.0035f,1f,0.08f,0.08f));
                material.SetVector("_CosmicP3",Vector4.zero);
            });

            // VG の面: 一辺 = BaseMeters × ScaleVg(1000 km)。最大階層は、Cell が 10 m 以下になる最小の値。
            result.Faces[(int)MoonTripleFace.Vg]=new GameObject("VG の面(一辺 1000 km。VG の PlanarTerrainSource。Cell ごとに頂点を持つ)");
            result.Faces[(int)MoonTripleFace.Vg].SetActive(false);
            result.Faces[(int)MoonTripleFace.Vg].AddComponent<global::Omni.Logical.Transform>();
            result.Vg=result.Faces[(int)MoonTripleFace.Vg].AddComponent<PlanarTerrainSource>();
            result.Faces[(int)MoonTripleFace.Vg].AddComponent<OmniPlanetPlacement>();
            ConfigurePlane(result.Vg,(float)layout.FaceSize(MoonTripleFace.Vg),layout.VgMaximumLevel);
            result.Vg.Material=result.FaceMaterials[(int)MoonTripleFace.Vg];
            result.Faces[(int)MoonTripleFace.Vg].SetActive(true);

            // Standard100Km・Standard1000Km: 標準 Plane を N 倍(1 物体)。
            result.Faces[(int)MoonTripleFace.Standard100Km]=AddPlainPiece("Standard100Km(10 m の Plane を 10^4 倍。素の拡大)",plane,result.FaceMaterials[(int)MoonTripleFace.Standard100Km],null);
            result.Faces[(int)MoonTripleFace.Standard1000Km]=AddPlainPiece("Standard1000Km(10 m の Plane を 10^5 倍。素の拡大)",plane,result.FaceMaterials[(int)MoonTripleFace.Standard1000Km],null);

            // 手動分割 1000m: 10 m の Plane を TilesPerSide × TilesPerSide 枚。子の順が (i, j)、k = j × TilesPerSide + i(MoonTripleDemo が読む)。
            GameObject manual=new GameObject("手動分割 1000m(10 m の Plane を 100×100 枚並べた面)");
            result.Faces[(int)MoonTripleFace.Manual]=manual;
            // 親にも Logical.Transform を先に付ける。無いと、子に付けたとき Omni の確認ダイアログ(EditorUtility.DisplayDialog)が出て、押すまで Unity の処理が止まる(Omni の Transform.Reset)。
            manual.AddComponent<global::Omni.Logical.Transform>();
            // 位置は MoonTripleDemo.Initialize(ApplyFaces)が Logical で置く。Unity の Transform に直接書くと、Omni の「Logical.Transform が唯一の書き手」の検査に引っかかる。
            int tiles=layout.TilesPerSide;
            for(int j=0;j<tiles;j++){
                for(int i=0;i<tiles;i++){
                    AddPlainPiece("タイル "+i+","+j,plane,result.TileMaterial,manual.transform);
                }
            }

            // ボール: CosmicSphere(直径 1)を Logical で直径 2·0.11 m に拡大する(MoonTripleDemo が設定する)。VG ではなく通常の MeshRenderer。
            result.Ball=new GameObject("サッカーボール(Omni の CosmicScale、半径 0.11 m。赤い線がボールの中心の高さ)");
            result.Ball.AddComponent<global::Omni.Logical.Transform>();
            result.Ball.AddComponent<MeshFilter>().sharedMesh=ballMesh;
            MeshRenderer ballRenderer=result.Ball.AddComponent<MeshRenderer>();
            ballRenderer.sharedMaterial=result.BallMaterial;
            ballRenderer.shadowCastingMode=ShadowCastingMode.Off;
            ballRenderer.receiveShadows=false;
            ballRenderer.lightProbeUsage=LightProbeUsage.Off;
            ballRenderer.reflectionProbeUsage=ReflectionProbeUsage.Off;

            // camera(自由飛行。位置と向きは MoonTripleDemo が Logical で決める)
            result.CameraObject=new GameObject("メインカメラ(自由飛行。操作と HUD は MoonTripleDemo)");result.CameraObject.tag="MainCamera";
            result.Camera=result.CameraObject.AddComponent<Camera>();
            result.Camera.fieldOfView=50;result.Camera.nearClipPlane=0.3f;result.Camera.farClipPlane=100000;
            result.Camera.clearFlags=CameraClearFlags.SolidColor;result.Camera.backgroundColor=new Color(0.005f,0.005f,0.012f,1);
            result.Camera.allowHDR=false;result.Camera.allowMSAA=false;
            result.CameraObject.AddComponent<global::Omni.Logical.Transform>();
            // 論理 near = Camera の Near ÷ 10 = 0.03 m(MoonAndBall と同じ)。
            global::Omni.RadialCamera radial=result.CameraObject.AddComponent<global::Omni.RadialCamera>();
            radial.SetViewScale(10f);
            result.Demo=result.CameraObject.AddComponent<MoonTripleDemo>();
            result.Demo.Faces=result.Faces;result.Demo.Ball=result.Ball;

            // 太陽: +x 側の斜め上から。
            result.Sun=new GameObject("太陽(URP の平行光。VG の面だけを照らす。他の面とボールは _CosmicKeyLight)");
            Light sun=result.Sun.AddComponent<Light>();sun.type=LightType.Directional;sun.intensity=1.3f;sun.color=new Color(1f,0.96f,0.9f);sun.shadows=LightShadows.None;
            result.Sun.transform.rotation=Quaternion.LookRotation(new Vector3(-0.7f,-0.55f,-0.25f).normalized,Vector3.up);
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(0.08f,0.08f,0.1f);
            RenderSettings.skybox=null;
            return result;
        }

        // 一辺 extent の VG の平面の設定(PlanarTerrainSource の SerializedObject)。PlaneSeamSceneBuilder と同じ項目。
        private static void ConfigurePlane(PlanarTerrainSource source,float extent,int maximumLevel){
            var serialized=new SerializedObject(source);
            serialized.FindProperty("extent").floatValue=extent;
            serialized.FindProperty("maximumLevel").intValue=maximumLevel;
            serialized.FindProperty("targetPixels").floatValue=VgTargetPixels;
            serialized.FindProperty("poolCapacity").intValue=VgPool;
            serialized.FindProperty("maximumDrawSlots").intValue=VgPool;
            serialized.FindProperty("generationBudget").intValue=VgBudget;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
