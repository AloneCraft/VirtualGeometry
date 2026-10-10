using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Omni;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using VirtualGeometry.World;
using VirtualizedGeometry;
using VirtualizedGeometry.Editor;
using LogicalPosition3=Omni.Logical.Position3;
using LogicalQuaternion=Omni.Logical.Quaternion;
using LogicalTransform=Omni.Logical.Transform;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// P2: 惑星(半径 10km、Hmax 300m)を Omni の Logical 座標に置き、Radial 対数描画で撮って次を確かめる。
    /// - 穴・継ぎ目 0(高度 10km/100m/1.7m と宇宙から地上までの経路。高度は Hmax の球からの距離)
    /// - Standard と同じ輪郭: Radial は warp 中心(eye)から見た方向を保つ写像なので、頂点の画面位置と三角形の被覆は変わらない。
    ///   基準は Omni の部品を付けない別の camera で撮る(Logical の camera は安全域外で Unity の位置が原点に固定されるため)。
    ///   輪郭の差は、基準の輪郭から 1px より離れたものを不合格にする。色の差(照明の物理位置を Omni が画素ごとに作り直す丸め)は記録だけする
    /// - 平行移動で不変: 惑星と camera を Logical で 1e9 m 規模ずらしても画像が同じ(float の world 座標に依存しない)
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.P2OmniPlanetValidation.Run
    /// </summary>
    public static class P2OmniPlanetValidation{
        private const string Root="Assets/Development/PlanetWorld/P2";
        private static string Results="TestResults/Planet/P2";
        private const string ShaderPath="Packages/com.virtualgeometry.omni/Shaders/RadialGlobalLit.vgshader";
        private static float Radius=10000;
        private static float Height=300;
        private static int MaximumLevel=16;
        private static float BaseWavelength=4000;
        private static int Octaves=13;
        private const float Target=2;
        private const int Width=640;
        private const int ImageHeight=480;
        private const int ConvergeLimit=600;
        private static readonly Color32 Background=new Color32(255,0,255,255);
        private static readonly double3 FarOffset=new double3(1.0e9,-2.0e9,3.0e9);

        private static Camera camera;
        private static Camera reference;
        private static Camera capturing;
        private static RadialCamera radial;
        private static LogicalTransform cameraLogical;
        private static LogicalTransform planetLogical;
        private static PlanetTerrainSource planet;
        private static OmniPlanetPlacement placement;
        private static LogicalVector3 offset;
        private static IEnumerator steps;
        private static double started;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();

        public static void Run(){
            Results="TestResults/Planet/P2";Radius=10000;Height=300;MaximumLevel=16;BaseWavelength=4000;Octaves=13;
            Start();
        }

        /// <summary>半径 600km、Hmax 3000m。最小の波長は約 0.9m(18 octave)。</summary>
        public static void RunLarge(){
            Results="TestResults/Planet/P2Large";Radius=600000;Height=3000;MaximumLevel=PlanetTerrainSource.GpuMaximumLevel;BaseWavelength=240000;Octaves=18;
            Start();
        }

        private static void Start(){
            report.Clear();failures.Clear();errors.Clear();started=EditorApplication.timeSinceStartup;
            Directory.CreateDirectory(Results);
            Application.logMessageReceived+=Record;
            try{
                Setup();
                steps=Steps();
                EditorApplication.update+=Tick;
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void Record(string condition,string stack,LogType type){
            if(type==LogType.Error||type==LogType.Exception||type==LogType.Assert){
                // Omni が Unity の版の違いで出す診断は、この検証の対象外(Requirements/PlanetWorld.md §8.4)。
                if(condition.Contains("[OmniConversion] ValidatedOnly")||condition.Contains("Localization/ja.po will not be compiled")){return;}
                errors.Add(condition);
            }
        }

        private static void Tick(){
            try{
                if(EditorApplication.timeSinceStartup-started>1800){throw new TimeoutException("P2 validation timeout.");}
                EditorApplication.QueuePlayerLoopUpdate();
                if(!steps.MoveNext()){Finish(null);}
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void Setup(){
            Directory.CreateDirectory(Root);AssetDatabase.Refresh();
            UniversalRendererData renderer=AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Root+"/P2Renderer.asset");
            if(renderer==null){
                renderer=ScriptableObject.CreateInstance<UniversalRendererData>();renderer.renderingMode=RenderingMode.ForwardPlus;
                renderer.depthPrimingMode=DepthPrimingMode.Disabled;renderer.copyDepthMode=CopyDepthMode.AfterTransparents;
                AssetDatabase.CreateAsset(renderer,Root+"/P2Renderer.asset");
            }
            VirtualGeometryRenderFeature feature=null;
            foreach(ScriptableRendererFeature existing in renderer.rendererFeatures){if(existing is VirtualGeometryRenderFeature found){feature=found;}}
            if(feature==null){
                feature=ScriptableObject.CreateInstance<VirtualGeometryRenderFeature>();feature.name="Virtual Geometry P2";
                renderer.rendererFeatures.Add(feature);AssetDatabase.AddObjectToAsset(feature,renderer);
            }
            var serialized=new SerializedObject(feature);
            serialized.FindProperty("selectionShader").objectReferenceValue=AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.virtualgeometry.core/Shaders/VirtualGeometrySelect.compute");
            serialized.FindProperty("surfaceShader").objectReferenceValue=Shader.Find("VirtualizedGeometry/Unlit");
            serialized.ApplyModifiedPropertiesWithoutUndo();feature.Create();
            EditorUtility.SetDirty(feature);EditorUtility.SetDirty(renderer);
            UniversalRenderPipelineAsset pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Root+"/P2Pipeline.asset");
            if(pipeline==null){pipeline=UniversalRenderPipelineAsset.Create(renderer);AssetDatabase.CreateAsset(pipeline,Root+"/P2Pipeline.asset");}
            pipeline.msaaSampleCount=1;pipeline.supportsHDR=false;pipeline.supportsCameraDepthTexture=true;pipeline.renderScale=1;
            GraphicsSettings.defaultRenderPipeline=pipeline;QualitySettings.renderPipeline=pipeline;
            AssetDatabase.SaveAssets();

            Shader shader=AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if(shader==null){throw new InvalidOperationException("Shader not found: "+ShaderPath);}
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            camera=new GameObject("P2 Camera").AddComponent<Camera>();
            camera.nearClipPlane=0.1f;camera.farClipPlane=200000;camera.fieldOfView=60;camera.clearFlags=CameraClearFlags.SolidColor;
            camera.backgroundColor=new Color(1,0,1,1);camera.allowHDR=false;camera.allowMSAA=false;
            cameraLogical=camera.gameObject.AddComponent<LogicalTransform>();
            radial=camera.gameObject.AddComponent<RadialCamera>();
            radial.SetIdentityViewScale();
            reference=new GameObject("P2 Reference Camera").AddComponent<Camera>();
            reference.nearClipPlane=0.1f;reference.farClipPlane=200000;reference.fieldOfView=60;reference.clearFlags=CameraClearFlags.SolidColor;
            reference.backgroundColor=new Color(1,0,1,1);reference.allowHDR=false;reference.allowMSAA=false;
            Use(camera);
            Light sun=new GameObject("P2 Sun").AddComponent<Light>();sun.type=LightType.Directional;sun.transform.rotation=Quaternion.Euler(40,-30,0);
            Material material=new Material(shader);material.SetColor("_BaseColor",new Color(0.35f,0.55f,0.3f,1));
            GameObject holder=new GameObject("P2 Planet");holder.SetActive(false);
            planetLogical=holder.AddComponent<LogicalTransform>();
            planet=holder.AddComponent<PlanetTerrainSource>();
            placement=holder.AddComponent<OmniPlanetPlacement>();
            var settings=new SerializedObject(planet);
            settings.FindProperty("poolCapacity").intValue=2048;
            settings.FindProperty("maximumDrawSlots").intValue=2048;
            settings.FindProperty("generationBudget").intValue=32;
            settings.FindProperty("baseWavelength").floatValue=BaseWavelength;
            settings.FindProperty("octaves").intValue=Octaves;
            settings.ApplyModifiedPropertiesWithoutUndo();
            planet.Material=material;
            planet.Configure(Radius,Height,MaximumLevel,Target);
            offset=LogicalVector3.Zero;
            SetLogical(planetLogical,Vector3.zero,Quaternion.identity);
            holder.SetActive(true);
        }

        private static IEnumerator Steps(){
            report.Add("planet: radius="+Radius+" height="+Height+" maximumLevel="+MaximumLevel+" wavelength="+BaseWavelength+" octaves="+Octaves+" target="+Target+"px pool="+planet.PoolCapacity+" shader="+planet.Material.shader.name);
            var views=new (string Name,float Altitude,float Pitch)[]{("High10km",10000,35),("Low100m",100,15),("Eye1.7m",1.7f,8)};
            var standard=new Texture2D[views.Length];
            var omni=new Texture2D[views.Length];
            // 1. 標準の配置(Unity の world 空間)。比較の基準。
            placement.enabled=false;Use(reference);
            for(int v=0;v<views.Length;v++){
                Place(views[v].Altitude,views[v].Pitch);
                foreach(object step in Converge("Standard"+views[v].Name)){yield return step;}
                standard[v]=Capture();
                Save("Standard"+views[v].Name,standard[v]);
                Measure("Standard"+views[v].Name,standard[v],false);
            }
            // 2. Omni の配置と Radial。原点の近く。
            placement.enabled=true;Use(camera);
            for(int v=0;v<views.Length;v++){
                Place(views[v].Altitude,views[v].Pitch);
                foreach(object step in Converge("Omni"+views[v].Name)){yield return step;}
                omni[v]=Capture();
                Save("Omni"+views[v].Name,omni[v]);
                ReportView("Omni"+views[v].Name);
                Measure("Omni"+views[v].Name,omni[v],true);
                CompareImages("Omni"+views[v].Name+"VsStandard",omni[v],standard[v],true,false,true);
                ProbePrecision("Omni"+views[v].Name);
            }
            // 3. 惑星と camera を Logical で遠くへずらす(Unity の安全域 ±10km の外)。画像は変わらないはず。
            // 選択はヒステリシスで履歴に依存するので、惑星を作り直して 2 と同じ「履歴なし」から始める。
            if(!LogicalVector3.TryFromFloat3((float3)FarOffset,out offset)){throw new InvalidOperationException("Far offset is not representable.");}
            planet.gameObject.SetActive(false);
            SetLogical(planetLogical,Vector3.zero,Quaternion.identity);
            planet.gameObject.SetActive(true);
            for(int v=0;v<views.Length;v++){
                Place(views[v].Altitude,views[v].Pitch);
                foreach(object step in Converge("Far"+views[v].Name)){yield return step;}
                Texture2D far=Capture();
                Save("Far"+views[v].Name,far);
                ReportView("Far"+views[v].Name);
                Measure("Far"+views[v].Name,far,true);
                CompareImages("Far"+views[v].Name+"VsOmni",far,omni[v],true,true,false);
            }
            // 4. 遠くに置いたまま、宇宙から目の高さまでの経路。毎 frame 穴が無いこと。
            int pathHoles=0;int saved=0;
            for(int frame=0;frame<=120;frame++){
                float altitude=Mathf.Exp(Mathf.Lerp(Mathf.Log(30000),Mathf.Log(1.7f),frame/120f));
                Place(altitude,Mathf.Lerp(90,8,frame/120f));
                Texture2D image=Capture(320,240);
                int holes=CountHoles(image);
                pathHoles+=holes;
                if(holes>0&&saved<6){
                    saved++;Save("PathHole"+frame,image);
                    report.Add("path hole frame="+frame+" altitude="+altitude.ToString("0.00",CultureInfo.InvariantCulture)+" holes="+holes+" cells="+planet.LastSelected.Count+" failure="+(placement.LastFailure??"(none)"));
                }
                yield return null;
            }
            report.Add("path frames=121 holes="+pathHoles+" evictions="+planet.Evictions+" starved="+planet.StarvedRequests);
            if(pathHoles!=0){failures.Add("経路で穴が "+pathHoles+" 画素ありました。");}
            // 5. 有効・無効の切り替えを繰り返しても、エラーが出ず描き直せること。
            for(int cycle=0;cycle<10;cycle++){
                planet.gameObject.SetActive(false);
                yield return null;
                planet.gameObject.SetActive(true);
                yield return null;
            }
            Place(views[0].Altitude,views[0].Pitch);
            foreach(object step in Converge("AfterCycles")){yield return step;}
            Measure("AfterCycles",Capture(),true);
            // 6. 高さの振幅をほぼ 0 にした球で、地上 1.7m の頂点の精度を測る(地形の曲率に隠れない)。
            float height=Height;
            Height=0.01f;
            planet.Configure(Radius,Height,MaximumLevel,Target);
            Place(1.7f,8);
            foreach(object step in Converge("SmoothEye1.7m")){yield return step;}
            Texture2D smooth=Capture();
            Save("SmoothEye1.7m",smooth);
            Measure("SmoothEye1.7m",smooth,false);
            ProbePrecision("SmoothEye1.7m");
            Height=height;
            planet.Configure(Radius,Height,MaximumLevel,Target);
            // 7. 実際の地形で、地面から 1.7m(地面の高さは生成した頂点から読み戻す)。
            foreach(object step in PlaceOnGround("TerrainEye1.7m",1.7f,8)){yield return step;}
            Texture2D terrain=Capture();
            Save("TerrainEye1.7m",terrain);
            Measure("TerrainEye1.7m",terrain,true);
            ProbePrecision("TerrainEye1.7m");
            report.Add("pool: used="+planet.UsedSlots+" capacity="+planet.PoolCapacity+" lastError="+(planet.LastError??"(none)"));
            if(planet.LastError!=null){failures.Add("設定エラー: "+planet.LastError);}
        }

        // 撮影と同じ解像度で、生成待ちが無くなり選択が2 frame 続けて変わらなくなるまで描き続ける(解像度で画面誤差が変わるため)。
        private static IEnumerable<object> Converge(string name){
            int stable=0;int previous=-1;int frames=0;
            while(frames<ConvergeLimit){
                Capture();
                int signature=Signature();
                if(planet.PendingGeneration==0&&signature==previous){stable++;}else{stable=0;}
                previous=signature;
                frames++;
                if(stable>=2){break;}
                yield return null;
            }
            if(frames>=ConvergeLimit){failures.Add(name+": "+ConvergeLimit+" frame で選択が収束しませんでした。");}
            report.Add(name+": converged frames="+frames+" cells="+planet.LastSelected.Count+" used="+planet.UsedSlots);
        }

        // 撮る camera だけを有効にする(惑星の LastSelected と統計は最後に描いた camera の値なので、混ざらないようにする)。
        private static void Use(Camera target){
            capturing=target;
            camera.enabled=target==camera;reference.enabled=target==reference;
        }

        private static int Signature(){
            int hash=planet.LastSelected.Count;
            foreach(PlanetCellKey key in planet.LastSelected){hash^=key.GetHashCode();}
            return hash;
        }

        private static void ReportView(string name){
            string flags="(no snapshot)";
            if(RadialViewRegistry.TryGetCurrent(camera.GetEntityId(),out global::Omni.Rendering.Radial.ViewSnapshot snapshot)){
                flags="radialActive="+snapshot.IsRadialActive+" eyeAtWarpCenter="+snapshot.IsEyeAtWarpCenter+" coneCulling="+snapshot.CanUseConeCulling+" failOpen="+snapshot.IsFailOpen;
            }
            report.Add(name+": "+flags+" placementFailure="+(placement.LastFailure??"(none)")+" unityCamera="+camera.transform.position.ToString("F1"));
        }

        // Logical の位置 = base + offset。回転は Unity の値から作る。
        private static void SetLogical(LogicalTransform target,Vector3 position,Quaternion rotation){
            if(!LogicalVector3.TryFromFloat3(position,out LogicalVector3 local)||!LogicalVector3.TryAdd(local,offset,out LogicalVector3 moved)){
                throw new InvalidOperationException("Logical position is not representable.");
            }
            if(!LogicalQuaternion.TryFromFloat4(rotation.x,rotation.y,rotation.z,rotation.w,out LogicalQuaternion logicalRotation)){
                throw new InvalidOperationException("Logical rotation is not representable.");
            }
            target.LocalPosition=moved;target.LocalRotation=logicalRotation;
            target.SyncUnityTransform();
        }

        // 方向 up の地表の真上(R+Hmax+altitude)に置き、地平線の方向から pitch 度下を見る。標準の配置では Unity の Transform も同じ値になる。
        private static void Place(float altitude,float pitch){
            Vector3 up=Up();
            Vector3 forward=Vector3.ProjectOnPlane(Vector3.forward,up).normalized;
            Vector3 right=Vector3.Cross(up,forward);
            Vector3 look=Quaternion.AngleAxis(pitch,right)*forward;
            Quaternion rotation=Quaternion.LookRotation(look,up);
            SetLogical(cameraLogical,up*(Radius+Height+altitude),rotation);
            reference.transform.SetPositionAndRotation(up*(Radius+Height+altitude),rotation);
        }

        // 地面の高さを読み戻しながら、地面から eye の高さへ近づける(選択が細かくなるほど地面の値も正確になる)。
        private static IEnumerable<object> PlaceOnGround(string name,float eye,float pitch){
            float ground=Radius+Height;
            for(int iteration=0;iteration<4;iteration++){
                Place(ground+eye-Radius-Height,pitch);
                foreach(object step in Converge(name+"Iteration"+iteration)){yield return step;}
                if(TrySampleGround(out float sampled)){ground=sampled;}
            }
            Place(ground+eye-Radius-Height,pitch);
            foreach(object step in Converge(name)){yield return step;}
            report.Add(name+": ground="+(ground-Radius).ToString("0.000",CultureInfo.InvariantCulture)+"m above R");
        }

        // camera の真下(方向 Up)に最も近い選択中の Cell の頂点から、Up の線に最も近い頂点の Up 方向の距離を返す。
        private static bool TrySampleGround(out float ground){
            ground=0;
            Vector3 up=Up();
            PlanetCellKey best=default;float closest=float.MaxValue;bool found=false;
            foreach(PlanetCellKey key in planet.LastSelected){
                if(!planet.TryReadbackCellForDiagnostics(in key,out VirtualGeometryPackedVertex[] _,out PlanetCellFrame frame)){continue;}
                float angle=Vector3.Angle(frame.ApproximateOrigin,up);
                if(angle<closest||(angle==closest&&key.Level>best.Level)){closest=angle;best=key;found=true;}
            }
            if(!found||!planet.TryReadbackCellForDiagnostics(in best,out VirtualGeometryPackedVertex[] vertices,out PlanetCellFrame cell)){return false;}
            int grid=(PlanetTerrainSource.CellQuads+1)*(PlanetTerrainSource.CellQuads+1);
            float distance=float.MaxValue;
            for(int i=0;i<grid;i++){
                Vector3 point=cell.ApproximateOrigin+vertices[i].Position;
                float along=Vector3.Dot(point,up);
                float off=(point-up*along).magnitude;
                if(off<distance){distance=off;ground=along;}
            }
            return true;
        }

        private static Vector3 Up(){
            return new Vector3(0.3f,1f,0.2f).normalized;
        }

        private static Texture2D Capture(int width=Width,int height=ImageHeight){
            return M0Validation.Capture(capturing,width,height);
        }

        private static void Save(string name,Texture2D image){
            File.WriteAllBytes(Results+"/"+name+".png",image.EncodeToPNG());
        }

        private static bool IsBackground(Color32 color){
            return color.r==Background.r&&color.g==Background.g&&color.b==Background.b;
        }

        // 内側の球(半径 R−Hmax から弦の沈み込みを引いたもの)に当たる光線の画素が背景色なら穴(P3b と同じ判定)。
        // 光線と球の中心は eye を原点とする world 軸の空間で作る(camera の Unity の位置は安全域外では原点に固定されるため使わない)。
        private static int CountHoles(Texture2D image){
            Color32[] pixels=image.GetPixels32();
            int width=image.width;int height=image.height;
            if(!LogicalPosition3.TryDifference(new LogicalPosition3(planetLogical.LocalPosition),new LogicalPosition3(cameraLogical.LocalPosition),out LogicalVector3 difference)||
                !difference.TryToFloat3(out float3 center)){
                throw new InvalidOperationException("Planet center is not representable.");
            }
            int coarsest=planet.MaximumLevel;
            foreach(PlanetCellKey key in planet.LastSelected){coarsest=Mathf.Min(coarsest,key.Level);}
            float edge=planet.Selector.QuadArc(coarsest)*2f;
            float inner=Radius-Height-edge*edge/(8f*(Radius-Height));
            float pixelAngle=capturing.fieldOfView*Mathf.Deg2Rad/height;
            float tangent=Mathf.Tan(capturing.fieldOfView*0.5f*Mathf.Deg2Rad);
            float aspect=(float)width/height;
            Quaternion rotation=capturing.transform.rotation;
            int holes=0;
            for(int y=0;y<height;y++){
                for(int x=0;x<width;x++){
                    if(!IsBackground(pixels[y*width+x])){continue;}
                    Vector3 local=new Vector3(((x+0.5f)/width*2-1)*tangent*aspect,((y+0.5f)/height*2-1)*tangent,1);
                    Vector3 direction=(rotation*local).normalized;
                    if(HitsSphere(direction,center,inner,capturing.nearClipPlane,pixelAngle)){holes++;}
                }
            }
            return holes;
        }

        private static bool HitsSphere(Vector3 direction,Vector3 center,float radius,float near,float pixelAngle){
            Vector3 offsetFromCenter=-center;
            float b=Vector3.Dot(offsetFromCenter,direction);
            float distance=offsetFromCenter.magnitude;
            float c=(distance-radius)*(distance+radius);
            float discriminant=b*b-c;
            if(discriminant<0){return false;}
            float t=-b-Mathf.Sqrt(discriminant);
            if(t<=near){return false;}
            if(c<=0){return true;}
            float closest=(offsetFromCenter+direction*(-b)).magnitude;
            return radius-closest>Mathf.Sqrt(c)*pixelAngle;
        }

        private static void Measure(string name,Texture2D image,bool fail){
            int holes=CountHoles(image);
            int covered=0;
            foreach(Color32 pixel in image.GetPixels32()){if(!IsBackground(pixel)){covered++;}}
            uint[] statistics=planet.ReadbackStatisticsForDiagnostics(capturing);
            uint[] arguments=planet.ReadbackArgumentsForDiagnostics(capturing);
            report.Add(name+": holes="+holes+" covered="+covered+" cells="+planet.LastSelected.Count+" draws="+arguments[1]+" cull(considered,frustum,hzb,visible)="+string.Join(",",statistics));
            if(fail&&holes!=0){failures.Add(name+": 穴が "+holes+" 画素ありました。");}
            if(fail&&covered==0){failures.Add(name+": 惑星が描かれていません(placement="+(placement.LastFailure??"(none)")+")。");}
        }

        // 描いている最も細かい階層の Cell の頂点を読み戻し、格子に沿った 2 階差分 |p[i−1]−2p[i]+p[i+1]| の最大を測る。
        // なめらかな地表なら quad の長さよりずっと小さい。頂点の位置が量子化されていると、その刻み程度の値になる。
        private static void ProbePrecision(string name){
            int finest=0;
            foreach(PlanetCellKey key in planet.LastSelected){finest=Mathf.Max(finest,key.Level);}
            int side=PlanetTerrainSource.CellQuads+1;
            float maximum=0;float quad=0;int cells=0;
            foreach(PlanetCellKey key in planet.LastSelected){
                if(key.Level!=finest||cells>=4){continue;}
                if(!planet.TryReadbackCellForDiagnostics(in key,out VirtualGeometryPackedVertex[] vertices,out PlanetCellFrame _)){continue;}
                cells++;
                for(int j=0;j<side;j++){
                    for(int i=1;i<side-1;i++){
                        Vector3 a=vertices[j*side+i-1].Position;Vector3 b=vertices[j*side+i].Position;Vector3 c=vertices[j*side+i+1].Position;
                        maximum=Mathf.Max(maximum,(a-2*b+c).magnitude);quad=Mathf.Max(quad,(c-b).magnitude);
                        Vector3 e=vertices[(i-1)*side+j].Position;Vector3 f=vertices[i*side+j].Position;Vector3 g=vertices[(i+1)*side+j].Position;
                        maximum=Mathf.Max(maximum,(e-2*f+g).magnitude);
                    }
                }
            }
            report.Add(name+": precision finestLevel="+finest+" cells="+cells+" quad="+quad.ToString("G4",CultureInfo.InvariantCulture)+"m maxSecondDifference="+maximum.ToString("G4",CultureInfo.InvariantCulture)+"m");
        }

        // 輪郭(背景かどうか)の差と色の差を数える。band なら、基準の輪郭から 1px の帯に入る輪郭の差を許す。
        private static void CompareImages(string name,Texture2D image,Texture2D baseline,bool silhouetteFail,bool colorFail,bool band){
            Color32[] a=image.GetPixels32();Color32[] b=baseline.GetPixels32();
            int width=image.width;int height=image.height;
            bool[] near=null;
            if(band){near=SilhouetteBand(b,width,height);}
            int silhouette=0;int outside=0;int color=0;int maximum=0;
            var difference=new Color32[a.Length];
            for(int i=0;i<a.Length;i++){
                bool terrainA=!IsBackground(a[i]);bool terrainB=!IsBackground(b[i]);
                difference[i]=new Color32(0,0,0,255);
                if(terrainA!=terrainB){
                    silhouette++;
                    if(near!=null&&near[i]){difference[i]=new Color32(0,255,255,255);continue;}
                    outside++;difference[i]=new Color32(255,0,0,255);continue;
                }
                int channel=Mathf.Max(Mathf.Abs(a[i].r-b[i].r),Mathf.Max(Mathf.Abs(a[i].g-b[i].g),Mathf.Abs(a[i].b-b[i].b)));
                if(channel>0){color++;maximum=Mathf.Max(maximum,channel);difference[i]=new Color32(255,255,0,255);}
            }
            var texture=new Texture2D(width,height,TextureFormat.RGBA32,false);texture.SetPixels32(difference);texture.Apply();
            Save(name+"Difference",texture);
            UnityEngine.Object.DestroyImmediate(texture);
            report.Add(name+": silhouetteDiffering="+silhouette+" outside1px="+outside+" colorDiffering="+color+" maxChannelDelta="+maximum);
            if(silhouetteFail&&outside!=0){failures.Add(name+": 輪郭の異なる画素が "+outside+" ありました(基準の輪郭から 1px の帯の外)。");}
            if(colorFail&&color!=0){failures.Add(name+": 色の異なる画素が "+color+" ありました(最大 "+maximum+")。");}
        }

        // 基準の輪郭(背景と地形の境)から 1px 以内の画素。
        private static bool[] SilhouetteBand(Color32[] pixels,int width,int height){
            var mask=new bool[pixels.Length];
            for(int i=0;i<pixels.Length;i++){mask[i]=!IsBackground(pixels[i]);}
            var band=new bool[pixels.Length];
            for(int y=0;y<height;y++){
                for(int x=0;x<width;x++){
                    bool value=mask[y*width+x];
                    bool edge=false;
                    if(x>0&&mask[y*width+x-1]!=value){edge=true;}
                    if(x<width-1&&mask[y*width+x+1]!=value){edge=true;}
                    if(y>0&&mask[(y-1)*width+x]!=value){edge=true;}
                    if(y<height-1&&mask[(y+1)*width+x]!=value){edge=true;}
                    if(!edge){continue;}
                    for(int dy=-1;dy<=1;dy++){
                        for(int dx=-1;dx<=1;dx++){
                            int px=x+dx;int py=y+dy;
                            if(px>=0&&px<width&&py>=0&&py<height){band[py*width+px]=true;}
                        }
                    }
                }
            }
            return band;
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Record;
            if(error!=null){failures.Add(error.ToString());}
            foreach(string message in errors){failures.Add("Unity logged error: "+message);}
            report.Add("UTC="+DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+" Unity="+Application.unityVersion+" GPU="+SystemInfo.graphicsDeviceName+" API="+SystemInfo.graphicsDeviceType);
            if(failures.Count==0){report.Add("PASSED");}else{report.Add("FAILED");report.AddRange(failures);}
            File.WriteAllLines(Results+"/Report.txt",report);
            int code=1;
            if(failures.Count==0){code=0;}
            EditorApplication.Exit(code);
        }
    }
}
