using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using VirtualGeometry.World;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>build した scene の部品。</summary>
    public sealed class MoonTripleObjects{
        public GameObject PlainMoon;
        public GameObject MoonAObject;
        public GameObject MoonBObject;
        /// <summary>原子サイズ(半径 1e-10 m)の VG の球。VG の月 A と同じ <see cref="PlanetTerrainSource"/> の処理で描く。</summary>
        public GameObject AtomObject;
        public PlanetTerrainSource Atom;
        public Material AtomMaterial;
        public GameObject Ball;
        public GameObject CameraObject;
        public GameObject Sun;
        public PlanetTerrainSource MoonA;
        /// <summary>方式 3(PlanetSurface。Omni の分割した地面)の月。</summary>
        public global::Omni.CosmicScale.CosmicPlanetSurface MoonB;
        public MoonTripleDemo Demo;
        public Camera Camera;
        public Material PlainMaterial;
        public Material MoonAMaterial;
        public Material PatchMaterial;
        public Material BallMaterial;
    }

    /// <summary>
    /// 3 つの月とサッカーボールのデモ scene を作る(<see cref="MoonTripleDemo"/>)。3 つの月は描き方(方式)が違う。
    /// 方式 1(単純な巨大化): 普通の月。CosmicScale(Omni のデモ)の単位球のメッシュ(<see cref="MeshPath"/>、Omni の Demos/CosmicScale/Meshes/CosmicSphere.asset のコピー)を、実行時に直径 2R へ拡大した 1 つの物体。
    /// 方式 2(VG): VG の月 A。<see cref="PlanetTerrainSource"/>(半径 R、高さ 0)。
    /// 方式 3(PlanetSurface): 月 B。Omni の分割した地面(PlanetSurfaceLod)を、CosmicScale の月と同じ <c>CosmicPlanetSurface</c> で描く。
    /// ボールは Omni のサッカーボール(通常の MeshRenderer、CosmicSphere のメッシュ)。
    /// </summary>
    public static class MoonTripleSceneBuilder{
        public const string Root="Assets/VirtualizedGeometrySample/MoonTriple";
        public const string MaterialsFolder=Root+"/Materials";
        public const string ScenePath=Root+"/MoonTriple.unity";
        /// <summary>CosmicScale の月とボールが使う、直径 1 の単位球のメッシュ(131,841 頂点)。</summary>
        public const string MeshPath=Root+"/Meshes/CosmicSphere.asset";
        /// <summary>probe 用: 0 なら資産のメッシュ。正なら、普通の月だけを、この経度×緯度の UV 球(実行時に作る。極は +y)にする。</summary>
        public static int PlainLongitude;
        public static int PlainLatitude;
        public const string PlainMaterialPath=MaterialsFolder+"/PlainMoon.mat";
        public const string MoonAMaterialPath=MaterialsFolder+"/VgMoonA.mat";
        public const string AtomMaterialPath=MaterialsFolder+"/AtomVg.mat";
        public const string PatchMaterialPath=MaterialsFolder+"/PatchMoon.mat";
        public const string PatchShaderPath=Root+"/OmniCosmic/CosmicPlanetPatch.shader";
        // ボールは Omni の SoccerBall(CosmicScale)と同じ。CosmicPlanetLit の模様のパラメーターは Omni の SoccerBall.mat の値。
        public const string BallMaterialPath=MaterialsFolder+"/SoccerBall.mat";
        public const string CosmicShaderPath=Root+"/OmniCosmic/CosmicPlanetLit.shader";
        // 月の表面の画像(Omni の PlanetSurface の LROC 全球)。Omni の Moon.mat と同じ。
        public const string MoonTexturePath=Root+"/Textures/LrocGlobal4k.png";
        private const string ShaderFolder="Packages/com.virtualgeometry.omni/Shaders";
        // 普通の月は素の MeshRenderer なので .shader、VG の月とボールは VG の pass が要るので .vgshader。
        // 普通の月は Omni の Moon.mat と同じ CosmicPlanetLit(月の種類 0)。単色の検証は RadialPlainSphere(MoonTripleValidation が使う)。
        private const string MoonShaderPath=ShaderFolder+"/RadialMoonGrid.vgshader";

        // 選べる最大の階層(Cell の quad が約 4×10^-5 m)。
        private const int MaximumLevel=30;

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

        private static PlanetTerrainSource AddSphere(string name,float radius,Material material,int pool,int budget,out GameObject owner){
            owner=new GameObject(name);owner.SetActive(false);
            owner.AddComponent<global::Omni.Logical.Transform>();
            PlanetTerrainSource source=owner.AddComponent<PlanetTerrainSource>();
            owner.AddComponent<OmniPlanetPlacement>();
            MoonBallSceneBuilder.Configure(source,radius,0f,1000f,1,radius<1f?10:MaximumLevel,pool,budget,false);
            source.Material=material;
            owner.SetActive(true);
            return source;
        }

        // 直径 1 の UV 球。極は +y(経度 longitude 本、緯度 latitude 段)。巻き順は、外向きが表になるように、(a,b,c)(b,d,c)。
        internal static Mesh BuildUvSphere(int longitude,int latitude){
            var vertices=new Vector3[(longitude+1)*(latitude+1)];
            for(int j=0;j<=latitude;j++){
                double theta=Math.PI*j/latitude;
                for(int i=0;i<=longitude;i++){
                    double phi=2*Math.PI*i/longitude;
                    vertices[j*(longitude+1)+i]=new Vector3((float)(0.5*Math.Sin(theta)*Math.Cos(phi)),(float)(0.5*Math.Cos(theta)),(float)(0.5*Math.Sin(theta)*Math.Sin(phi)));
                }
            }
            var triangles=new List<int>(longitude*latitude*6);
            for(int j=0;j<latitude;j++){
                for(int i=0;i<longitude;i++){
                    int a=j*(longitude+1)+i;int b=a+1;int c=a+(longitude+1);int d=c+1;
                    triangles.Add(a);triangles.Add(b);triangles.Add(c);
                    triangles.Add(b);triangles.Add(d);triangles.Add(c);
                }
            }
            var mesh=new Mesh{name="PlainSphereProbe",indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles,0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>開いている scene に、3 つの月・ボール・camera・太陽を置く。</summary>
        public static MoonTripleObjects Populate(){
            var result=new MoonTripleObjects();
            Mesh mesh=AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
            if(mesh==null){throw new InvalidOperationException("単位球のメッシュがありません: "+MeshPath);}
            Mesh plainMesh=mesh;
            if(PlainLongitude>0&&PlainLatitude>0){plainMesh=BuildUvSphere(PlainLongitude,PlainLatitude);}
            // 普通の月: Omni の Moon.mat と同じ(CosmicPlanetLit、_CosmicBody の種類 0 = 月、画像は LROC 全球)。
            var moonTexture=AssetDatabase.LoadAssetAtPath<Texture2D>(MoonTexturePath);
            if(moonTexture==null){throw new InvalidOperationException("月の画像がありません: "+MoonTexturePath);}
            result.PlainMaterial=EnsureMaterial(PlainMaterialPath,CosmicShaderPath,material=>{
                material.SetTexture("_BaseMap",moonTexture);
                material.SetColor("_BaseColor",Color.white);
                material.SetVector("_CosmicBody",new Vector4(10,0,0,0));
                material.SetVector("_CosmicP0",Vector4.zero);
                material.SetVector("_CosmicP1",Vector4.zero);
                material.SetVector("_CosmicP2",Vector4.zero);
                material.SetVector("_CosmicP3",Vector4.zero);
            });
            result.MoonAMaterial=EnsureMaterial(MoonAMaterialPath,MoonShaderPath,material=>{
                material.SetColor("_BaseColor",new Color(0.30f,0.50f,0.30f,1));material.SetColor("_GridColor",new Color(0.92f,0.9f,0.82f,1));
                material.SetFloat("_Smoothness",0.05f);material.SetFloat("_FlatColor",0);
            });
            // 原子サイズの VG の球: 月 A と同じ材質の種類(RadialMoonGrid)。基準点は設定しない(格子の周期は 1 m / 10 cm / 1 cm 固定で、原子では全面が格子の色になるため)ので、
            // 格子は描かれず、URP の光による陰影だけで見える。
            result.AtomMaterial=EnsureMaterial(AtomMaterialPath,MoonShaderPath,material=>{
                material.SetColor("_BaseColor",new Color(0.95f,0.72f,0.30f,1));material.SetColor("_GridColor",new Color(0.95f,0.72f,0.30f,1));
                material.SetFloat("_Smoothness",0.35f);material.SetFloat("_FlatColor",0);
            });
            // 方式 3 の月: 普通の月と同じ見た目の式(CosmicPlanetPatch。物体空間がパッチの中心からの差)。
            result.PatchMaterial=EnsureMaterial(PatchMaterialPath,PatchShaderPath,material=>{
                material.SetTexture("_BaseMap",moonTexture);
                material.SetColor("_BaseColor",Color.white);
                material.SetVector("_CosmicBody",new Vector4(10,0,0,0));
                material.SetVector("_CosmicP0",Vector4.zero);
                material.SetVector("_CosmicP1",Vector4.zero);
                material.SetVector("_CosmicP2",Vector4.zero);
                material.SetVector("_CosmicP3",Vector4.zero);
            });
            result.BallMaterial=EnsureMaterial(BallMaterialPath,CosmicShaderPath,material=>{
                // Omni の SoccerBall.mat と同じ値(CosmicBody: 階層の段、種類 2 = サッカーボール、シード)。
                material.SetColor("_BaseColor",Color.white);
                material.SetVector("_CosmicBody",new Vector4(7,2,5,0));
                material.SetVector("_CosmicP0",new Vector4(0.41f,0.67f,0.29f,0.11f));
                material.SetVector("_CosmicP1",new Vector4(1,0,0,0.4f));
                // 赤道の線(MoonTriple が足したシェーダーの追加): x = 帯の半幅 3.5 mm、yzw = 赤。半分を面に埋めたとき、面の境界との差が誤差になる。
                material.SetVector("_CosmicP2",new Vector4(0.0035f,1f,0.08f,0.08f));
                material.SetVector("_CosmicP3",Vector4.zero);
            });

            // 普通の月: 単位球のメッシュ(直径 1)。倍率は MoonTripleDemo が Logical で 2R にする。
            result.PlainMoon=new GameObject("Plain Moon (unit sphere scaled)");
            result.PlainMoon.AddComponent<global::Omni.Logical.Transform>();
            result.PlainMoon.AddComponent<MeshFilter>().sharedMesh=plainMesh;
            MeshRenderer renderer=result.PlainMoon.AddComponent<MeshRenderer>();
            renderer.sharedMaterial=result.PlainMaterial;
            renderer.shadowCastingMode=ShadowCastingMode.Off;
            renderer.receiveShadows=false;
            renderer.lightProbeUsage=LightProbeUsage.Off;
            renderer.reflectionProbeUsage=ReflectionProbeUsage.Off;

            float radius=(float)MoonTripleLayout.Default.Radius;
            result.MoonA=AddSphere("VG Moon A",radius,result.MoonAMaterial,4096,64,out result.MoonAObject);
            // 原子サイズの VG の球(半径 1e-10 m。AddSphere は半径 1 m 未満を階層 10 にする。原子の頂点間隔(~1e-12 m)には 7 階層ほどで足りる)。
            result.Atom=AddSphere("VG Atom (radius 1e-10 m)",(float)MoonTripleLayout.AtomRadius,result.AtomMaterial,1024,32,out result.AtomObject);
            // 方式 3 の月 B(PlanetSurface): Omni の分割した地面。視点(camera の Logical.Transform)を設定してから有効にする(有効にした時点で分割が始まる)。
            result.MoonBObject=new GameObject("PlanetSurface Moon B (method 3)");result.MoonBObject.SetActive(false);
            result.MoonBObject.AddComponent<global::Omni.Logical.Transform>();
            result.MoonB=result.MoonBObject.AddComponent<global::Omni.CosmicScale.CosmicPlanetSurface>();
            // ボール: CosmicSphere(直径 1)を Logical で直径 2R に拡大する(MoonTripleDemo が設定する)。VG ではなく通常の MeshRenderer。
            result.Ball=new GameObject("Soccer Ball (Omni CosmicScale)");
            result.Ball.AddComponent<global::Omni.Logical.Transform>();
            result.Ball.AddComponent<MeshFilter>().sharedMesh=mesh;
            MeshRenderer ballRenderer=result.Ball.AddComponent<MeshRenderer>();
            ballRenderer.sharedMaterial=result.BallMaterial;
            ballRenderer.shadowCastingMode=ShadowCastingMode.Off;
            ballRenderer.receiveShadows=false;
            ballRenderer.lightProbeUsage=LightProbeUsage.Off;
            ballRenderer.reflectionProbeUsage=ReflectionProbeUsage.Off;

            // camera(ボールの周回。位置と向きは MoonTripleDemo が Logical で決める)
            result.CameraObject=new GameObject("Main Camera");result.CameraObject.tag="MainCamera";
            result.Camera=result.CameraObject.AddComponent<Camera>();
            result.Camera.fieldOfView=50;result.Camera.nearClipPlane=0.3f;result.Camera.farClipPlane=100000;
            result.Camera.clearFlags=CameraClearFlags.SolidColor;result.Camera.backgroundColor=new Color(0.005f,0.005f,0.012f,1);
            result.Camera.allowHDR=false;result.Camera.allowMSAA=false;
            result.CameraObject.AddComponent<global::Omni.Logical.Transform>();
            // 論理 near = Camera の Near ÷ 10 = 0.03 m(MoonAndBall と同じ)。
            global::Omni.RadialCamera radial=result.CameraObject.AddComponent<global::Omni.RadialCamera>();
            radial.SetViewScale(10f);
            result.MoonB.Configure("MoonTripleB",radius,result.PatchMaterial,result.CameraObject.GetComponent<global::Omni.Logical.Transform>(),Array.Empty<MeshRenderer>());
            result.MoonBObject.SetActive(true);
            result.Demo=result.CameraObject.AddComponent<MoonTripleDemo>();
            result.Demo.PlainMoon=result.PlainMoon;result.Demo.MoonA=result.MoonA;result.Demo.MoonB=result.MoonB;result.Demo.Ball=result.Ball;result.Demo.Atom=result.Atom;

            // 太陽: 接触点に斜めから(2 つの面の間に光が入る向き)。
            result.Sun=new GameObject("Sun");
            Light sun=result.Sun.AddComponent<Light>();sun.type=LightType.Directional;sun.intensity=1.3f;sun.color=new Color(1f,0.96f,0.9f);sun.shadows=LightShadows.None;
            result.Sun.transform.rotation=Quaternion.LookRotation(new Vector3(0.2f,-0.8f,-0.55f).normalized,Vector3.up);
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(0.08f,0.08f,0.1f);
            RenderSettings.skybox=null;
            return result;
        }
    }
}
