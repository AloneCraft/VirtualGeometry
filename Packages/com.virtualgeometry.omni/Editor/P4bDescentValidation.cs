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
using LogicalTransform=Omni.Logical.Transform;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// P4b: 手続き生成の高さの惑星へ、降下カメラ(<see cref="PlanetDescentCamera"/>)で宇宙から地上 1.7m まで降り、地上を歩き、瞬間移動して次を確かめる。
    /// - CPU と GPU の高さの一致: GPU が生成した頂点と、同じ式の CPU の評価(<see cref="PlanetTerrainSource.TryEvaluateVertexForDiagnostics"/>)の差
    /// - 地面に潜らない: 毎 frame、CPU の地表からの高さが eyeHeight 以上
    /// - 穴 0: 毎 frame(P2 と同じ判定)
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.P4bDescentValidation.Run(半径 600km)/ RunEarth(半径 6,371km)
    /// </summary>
    public static class P4bDescentValidation{
        private const string Root="Assets/Development/PlanetWorld/P4b";
        private const string ShaderPath="Packages/com.virtualgeometry.omni/Shaders/RadialGlobalLit.vgshader";
        private const float Target=2;
        private const float Eye=1.7f;
        private const int Width=640;
        private const int ImageHeight=480;
        private const int ConvergeLimit=600;
        private static readonly Color32 Background=new Color32(255,0,255,255);

        private static string results;
        private static float radius;
        private static float height;
        private static float wavelength;
        private static int octaves;
        private static Camera camera;
        private static LogicalTransform cameraLogical;
        private static LogicalTransform planetLogical;
        private static PlanetTerrainSource planet;
        private static OmniPlanetPlacement placement;
        private static PlanetDescentCamera descent;
        private static IEnumerator steps;
        private static double started;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();

        public static void Run(){
            results="TestResults/Planet/P4b";radius=600000;height=3000;wavelength=240000;octaves=26;
            Start();
        }

        public static void RunEarth(){
            results="TestResults/Planet/P4bEarth";radius=6371000;height=8000;wavelength=2500000;octaves=29;
            Start();
        }

        private static void Start(){
            report.Clear();failures.Clear();errors.Clear();started=EditorApplication.timeSinceStartup;
            Directory.CreateDirectory(results);
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
                if(EditorApplication.timeSinceStartup-started>2400){throw new TimeoutException("P4b validation timeout.");}
                EditorApplication.QueuePlayerLoopUpdate();
                if(!steps.MoveNext()){Finish(null);}
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void Setup(){
            Directory.CreateDirectory(Root);AssetDatabase.Refresh();
            UniversalRendererData renderer=AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Root+"/P4bRenderer.asset");
            if(renderer==null){
                renderer=ScriptableObject.CreateInstance<UniversalRendererData>();renderer.renderingMode=RenderingMode.ForwardPlus;
                renderer.depthPrimingMode=DepthPrimingMode.Disabled;renderer.copyDepthMode=CopyDepthMode.AfterTransparents;
                AssetDatabase.CreateAsset(renderer,Root+"/P4bRenderer.asset");
            }
            VirtualGeometryRenderFeature feature=null;
            foreach(ScriptableRendererFeature existing in renderer.rendererFeatures){if(existing is VirtualGeometryRenderFeature found){feature=found;}}
            if(feature==null){
                feature=ScriptableObject.CreateInstance<VirtualGeometryRenderFeature>();feature.name="Virtual Geometry P4b";
                renderer.rendererFeatures.Add(feature);AssetDatabase.AddObjectToAsset(feature,renderer);
            }
            var serialized=new SerializedObject(feature);
            serialized.FindProperty("selectionShader").objectReferenceValue=AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.virtualgeometry.core/Shaders/VirtualGeometrySelect.compute");
            serialized.FindProperty("surfaceShader").objectReferenceValue=Shader.Find("VirtualizedGeometry/Unlit");
            serialized.ApplyModifiedPropertiesWithoutUndo();feature.Create();
            EditorUtility.SetDirty(feature);EditorUtility.SetDirty(renderer);
            UniversalRenderPipelineAsset pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Root+"/P4bPipeline.asset");
            if(pipeline==null){pipeline=UniversalRenderPipelineAsset.Create(renderer);AssetDatabase.CreateAsset(pipeline,Root+"/P4bPipeline.asset");}
            pipeline.msaaSampleCount=1;pipeline.supportsHDR=false;pipeline.supportsCameraDepthTexture=true;pipeline.renderScale=1;
            GraphicsSettings.defaultRenderPipeline=pipeline;QualitySettings.renderPipeline=pipeline;
            AssetDatabase.SaveAssets();

            Shader shader=AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if(shader==null){throw new InvalidOperationException("Shader not found: "+ShaderPath);}
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            camera=new GameObject("P4b Camera").AddComponent<Camera>();
            camera.nearClipPlane=0.1f;camera.farClipPlane=200000;camera.fieldOfView=60;camera.clearFlags=CameraClearFlags.SolidColor;
            camera.backgroundColor=new Color(1,0,1,1);camera.allowHDR=false;camera.allowMSAA=false;
            cameraLogical=camera.gameObject.AddComponent<LogicalTransform>();
            camera.gameObject.AddComponent<RadialCamera>().SetIdentityViewScale();
            Light sun=new GameObject("P4b Sun").AddComponent<Light>();sun.type=LightType.Directional;sun.transform.rotation=Quaternion.Euler(40,-30,0);
            Material material=new Material(shader);material.SetColor("_BaseColor",new Color(0.35f,0.55f,0.3f,1));
            GameObject holder=new GameObject("P4b Planet");holder.SetActive(false);
            planetLogical=holder.AddComponent<LogicalTransform>();
            planet=holder.AddComponent<PlanetTerrainSource>();
            placement=holder.AddComponent<OmniPlanetPlacement>();
            var settings=new SerializedObject(planet);
            settings.FindProperty("poolCapacity").intValue=2048;
            settings.FindProperty("maximumDrawSlots").intValue=2048;
            settings.FindProperty("generationBudget").intValue=32;
            settings.FindProperty("baseWavelength").floatValue=wavelength;
            settings.FindProperty("octaves").intValue=octaves;
            settings.ApplyModifiedPropertiesWithoutUndo();
            planet.Material=material;
            planet.Configure(radius,height,PlanetTerrainSource.GpuMaximumLevel,Target);
            HeightShiftOverride.Apply(planet);
            holder.SetActive(true);
            // 惑星の中心に置くと上方向が決まらないので、宇宙に置いてから Teleport する。
            if(!LogicalVector3.TryFromFloat3(new float3(0,radius*3f,0),out LogicalVector3 far)){throw new InvalidOperationException("far");}
            cameraLogical.LocalPosition=far;cameraLogical.SyncUnityTransform();
            descent=camera.gameObject.AddComponent<PlanetDescentCamera>();
            descent.Planet=planet;descent.EyeHeight=Eye;descent.ReadInput=false;
            var cameraSettings=new SerializedObject(descent);
            cameraSettings.FindProperty("speedPerAltitude").floatValue=2f;
            cameraSettings.ApplyModifiedPropertiesWithoutUndo();
        }

        private static IEnumerator Steps(){
            report.Add("planet: radius="+radius+" height="+height+" wavelength="+wavelength+" octaves="+octaves+" target="+Target+"px pool="+planet.PoolCapacity);
            Vector3 start=new Vector3(0.3f,1f,0.2f).normalized;
            // 1. 宇宙(高さ 1.5R)から惑星の中心を見る。
            LookAtCenter(start,0);
            if(!descent.Teleport(start,radius*1.5f)){throw new InvalidOperationException("Teleport: "+descent.LastFailure);}
            foreach(object step in Converge("Orbit")){yield return step;}
            Measure("Orbit",Capture());
            CompareCpuGpu("Orbit");
            // 2. 降下。毎 frame、下へ進みながら少しずつ地平線へ向きを起こす。
            int frames=0;int holes=0;int below=0;int clamped=0;int saved=0;int under=0;
            while(frames<400&&descent.Altitude>Eye*1.01f){
                float pitch=0;
                if(descent.Altitude<radius*0.05f){pitch=0.4f;}
                if(!descent.Move(new Vector3(0,-1,0),new Vector2(0,pitch),1f/30f)){throw new InvalidOperationException("Move: "+descent.LastFailure);}
                Texture2D image=Capture(320,240);
                int frameHoles=CountHoles(image);
                holes+=frameHoles;
                if(frameHoles>0&&saved<4){saved++;Save("DescentHole"+frames,image);}
                if(descent.Altitude<Eye-1e-3f){below++;}
                if(UnderRendered()){under++;}
                if(descent.Clamped){clamped++;}
                if(frames%40==0){report.Add("descent frame="+frames+" altitude="+descent.Altitude.ToString("G5",CultureInfo.InvariantCulture)+" cells="+planet.LastSelected.Count+" pending="+planet.PendingGeneration);}
                frames++;
                yield return null;
            }
            report.Add("descent frames="+frames+" altitude="+descent.Altitude.ToString("G5",CultureInfo.InvariantCulture)+" holes="+holes+" below="+below+" underRendered="+under+" clamped="+clamped+" starved="+planet.StarvedRequests);
            if(under!=0){failures.Add("降下で、描いた面が camera より上の frame が "+under+" ありました。");}
            if(holes!=0){failures.Add("降下で穴が "+holes+" 画素ありました。");}
            if(below!=0){failures.Add("降下で地表から "+Eye+"m より下の frame が "+below+" ありました。");}
            if(descent.Altitude>Eye*1.5f){failures.Add("降下が地上まで届きませんでした(高さ "+descent.Altitude+"m)。");}
            // 3. 地上に着いた状態を細かく撮る。
            foreach(object step in Converge("Ground")){yield return step;}
            Texture2D ground=Capture();
            Save("Ground",ground);
            Measure("Ground",ground);
            CompareCpuGpu("Ground");
            // 4. 地上を前へ歩く(地表に沿って押し戻されながら進む)。
            holes=0;below=0;clamped=0;under=0;
            for(int frame=0;frame<120;frame++){
                if(!descent.Move(new Vector3(0,0,1),Vector2.zero,1f/30f)){throw new InvalidOperationException("Move: "+descent.LastFailure);}
                Texture2D image=Capture(320,240);
                holes+=CountHoles(image);
                if(descent.Altitude<Eye-1e-3f){below++;}
                if(UnderRendered()){under++;}
                if(descent.Clamped){clamped++;}
                yield return null;
            }
            Save("Walk",Capture());
            report.Add("walk frames=120 altitude="+descent.Altitude.ToString("G5",CultureInfo.InvariantCulture)+" holes="+holes+" below="+below+" underRendered="+under+" clamped="+clamped);
            if(under!=0){failures.Add("歩行で、描いた面が camera より上の frame が "+under+" ありました。");}
            if(holes!=0){failures.Add("歩行で穴が "+holes+" 画素ありました。");}
            if(below!=0){failures.Add("歩行で地表から "+Eye+"m より下の frame が "+below+" ありました。");}
            // 5. 地表より下を指定して瞬間移動する(押し戻されて eyeHeight になる)。生成が追いつかない間も穴が無いこと。
            var targets=new[]{new Vector3(1,0.2f,-0.3f),new Vector3(-0.4f,-1,0.5f),new Vector3(0.1f,0.3f,1),new Vector3(-1,0.7f,-0.2f)};
            for(int t=0;t<targets.Length;t++){
                // 移動先の Cell を先に生成させ、揃ってから移す。
                int waited=0;
                while(!descent.PrepareTeleport(targets[t],-50f)){
                    if(descent.LastFailure!=null){throw new InvalidOperationException("PrepareTeleport: "+descent.LastFailure);}
                    if(waited>=300){failures.Add("瞬間移動 "+t+" の準備が 300 frame で終わりませんでした。");break;}
                    Capture(320,240);
                    waited++;
                    yield return null;
                }
                if(!descent.Teleport(targets[t],-50f)){throw new InvalidOperationException("Teleport: "+descent.LastFailure);}
                // 地面が写るよう、その場の地平線から 10° 下を向ける(向きは移動前のままだと空を向くことがある)。
                LookAlongHorizon(10f);
                holes=0;below=0;under=0;
                if(descent.Altitude<Eye-1e-3f){below++;}
                var perFrame=new List<string>();
                for(int frame=0;frame<40;frame++){
                    Texture2D image=Capture(320,240);
                    int frameHoles=CountHoles(image);
                    holes+=frameHoles;
                    float above=RenderedSurfaceAboveCamera(out int level);
                    if(!float.IsNaN(above)&&above>0.01f){under++;}
                    if(frameHoles>0){perFrame.Add(frame+":"+frameHoles+"/p"+planet.PendingGeneration+"/c"+planet.LastSelected.Count+"/L"+level+":"+above.ToString("0.00",CultureInfo.InvariantCulture));}
                    if(descent.Altitude<Eye-1e-3f){below++;}
                    yield return null;
                }
                if(perFrame.Count>0){report.Add("teleport "+t+" holes per frame(frame:holes/pending/cells)="+string.Join(" ",perFrame));}
                Texture2D last=Capture();
                Save("Teleport"+t,last);
                var mask=new Color32[last.width*last.height];
                int lastHoles=CountHoles(last,mask);
                if(lastHoles>0){
                    var texture=new Texture2D(last.width,last.height,TextureFormat.RGBA32,false);texture.SetPixels32(mask);texture.Apply();
                    Save("Teleport"+t+"HoleMask",texture);
                    UnityEngine.Object.DestroyImmediate(texture);
                }
                report.Add("teleport "+t+": last frame holes="+lastHoles+" finestLevel="+Finest()+" up="+((Vector3)TryUp()).ToString("F3"));
                report.Add("teleport "+t+": waited="+waited+" altitude="+descent.Altitude.ToString("G5",CultureInfo.InvariantCulture)+" clamped="+descent.Clamped+" holes="+holes+" below="+below+" underRendered="+under);
                if(under!=0){failures.Add("瞬間移動 "+t+" で、描いた面が camera より上の frame が "+under+" ありました。");}
                if(holes!=0){failures.Add("瞬間移動 "+t+" で穴が "+holes+" 画素ありました。");}
                if(below!=0){failures.Add("瞬間移動 "+t+" で地表より下の frame がありました。");}
                if(!descent.Clamped){failures.Add("瞬間移動 "+t+": 地表より下の指定が押し戻されていません。");}
            }
            report.Add("pool: used="+planet.UsedSlots+" capacity="+planet.PoolCapacity+" evictions="+planet.Evictions+" starved="+planet.StarvedRequests+" lastError="+(planet.LastError??"(none)"));
            if(planet.LastError!=null){failures.Add("設定エラー: "+planet.LastError);}
        }

        // カメラを、方向 direction の上から惑星の中心へ向ける(pitch は中心から地平線側へ起こす角度)。
        private static void LookAtCenter(Vector3 direction,float pitch){
            Vector3 down=-direction.normalized;
            Vector3 side=Vector3.Cross(down,Vector3.forward).normalized;
            Quaternion rotation=Quaternion.AngleAxis(pitch,side)*Quaternion.LookRotation(down,Vector3.forward);
            if(!global::Omni.Logical.Quaternion.TryFromFloat4(rotation.x,rotation.y,rotation.z,rotation.w,out global::Omni.Logical.Quaternion value)){throw new InvalidOperationException("rotation");}
            cameraLogical.LocalRotation=value;
            cameraLogical.SyncUnityTransform();
        }

        // camera を、その場の地平線(惑星の中心から外向きの方向に垂直)の方向へ向け、pitch 度下を見る。
        private static void LookAlongHorizon(float pitch){
            Vector3 up=(Vector3)TryUp();
            Vector3 forward=Vector3.ProjectOnPlane(Vector3.forward,up);
            if(forward.sqrMagnitude<1e-6f){forward=Vector3.ProjectOnPlane(Vector3.right,up);}
            forward.Normalize();
            Vector3 right=Vector3.Cross(up,forward);
            Quaternion rotation=Quaternion.LookRotation(Quaternion.AngleAxis(pitch,right)*forward,up);
            if(!global::Omni.Logical.Quaternion.TryFromFloat4(rotation.x,rotation.y,rotation.z,rotation.w,out global::Omni.Logical.Quaternion value)){throw new InvalidOperationException("rotation");}
            cameraLogical.LocalRotation=value;
            cameraLogical.SyncUnityTransform();
        }

        private static IEnumerable<object> Converge(string name){
            int stable=0;int previous=-1;int frames=0;
            while(frames<ConvergeLimit){
                Capture();
                int signature=planet.LastSelected.Count;
                foreach(PlanetCellKey key in planet.LastSelected){signature^=key.GetHashCode();}
                if(planet.PendingGeneration==0&&signature==previous){stable++;}else{stable=0;}
                previous=signature;
                frames++;
                if(stable>=2){break;}
                yield return null;
            }
            if(frames>=ConvergeLimit){failures.Add(name+": "+ConvergeLimit+" frame で選択が収束しませんでした。");}
            report.Add(name+": converged frames="+frames+" cells="+planet.LastSelected.Count+" used="+planet.UsedSlots+" placementFailure="+(placement.LastFailure??"(none)"));
        }

        private static void Measure(string name,Texture2D image){
            int holes=CountHoles(image);
            int covered=0;
            foreach(Color32 pixel in image.GetPixels32()){if(!IsBackground(pixel)){covered++;}}
            int finest=0;
            foreach(PlanetCellKey key in planet.LastSelected){finest=Mathf.Max(finest,key.Level);}
            report.Add(name+": holes="+holes+" covered="+covered+" finestLevel="+finest+" altitude="+descent.Altitude.ToString("G5",CultureInfo.InvariantCulture));
            if(holes!=0){failures.Add(name+": 穴が "+holes+" 画素ありました。");}
            if(covered==0){failures.Add(name+": 惑星が描かれていません。");}
        }

        // 選択中の Cell(階層ごとに最大 2 個)の格子の 9 点で、GPU の頂点と CPU の評価の差を測る。許容は Cell の大きさの 1e-5 倍 + 1mm。
        private static void CompareCpuGpu(string name){
            var perLevel=new Dictionary<int,int>();
            float worst=0;float worstRatio=0;int points=0;
            int side=PlanetTerrainSource.CellQuads+1;
            foreach(PlanetCellKey key in planet.LastSelected){
                perLevel.TryGetValue(key.Level,out int count);
                if(count>=2){continue;}
                if(!planet.TryReadbackCellForDiagnostics(in key,out VirtualGeometryPackedVertex[] vertices,out PlanetCellFrame _)){continue;}
                perLevel[key.Level]=count+1;
                float cellSize=planet.Selector.QuadArc(key.Level)*PlanetTerrainSource.CellQuads;
                for(int j=0;j<=PlanetTerrainSource.CellQuads;j+=PlanetTerrainSource.CellQuads/2){
                    for(int i=0;i<=PlanetTerrainSource.CellQuads;i+=PlanetTerrainSource.CellQuads/2){
                        if(!planet.TryEvaluateVertexForDiagnostics(in key,i,j,out Vector3 cpu)){continue;}
                        float error=(vertices[j*side+i].Position-cpu).magnitude;
                        float allowed=cellSize*1e-5f+1e-3f;
                        worst=Mathf.Max(worst,error);worstRatio=Mathf.Max(worstRatio,error/allowed);points++;
                    }
                }
            }
            report.Add(name+": cpuGpu points="+points+" levels="+perLevel.Count+" maxError="+worst.ToString("G4",CultureInfo.InvariantCulture)+"m maxErrorPerAllowed="+worstRatio.ToString("G3",CultureInfo.InvariantCulture));
            if(points==0){failures.Add(name+": CPU と GPU を比べる頂点がありません。");}
            if(worstRatio>1){failures.Add(name+": CPU と GPU の高さの差が許容を超えました(最大 "+worst+"m)。");}
        }

        private static Texture2D Capture(int width=Width,int height=ImageHeight){
            return M0Validation.Capture(camera,width,height);
        }

        private static void Save(string name,Texture2D image){
            File.WriteAllBytes(results+"/"+name+".png",image.EncodeToPNG());
        }

        private static bool IsBackground(Color32 color){
            return color.r==Background.r&&color.g==Background.g&&color.b==Background.b;
        }

        // 内側の球(半径 R−Hmax から弦の沈み込みを引いたもの)に当たる光線の画素が背景色なら穴(P2 と同じ判定)。
        // camera の真下に描いている最も細かい Cell の面の高さ − camera の高さ(正なら、camera は描いた面の下にいる = 地面に潜って見える)。
        // 描いている Cell の頂点を読み戻し、camera を通る上方向の直線と、真下に最も近い頂点の周りの三角形(格子と同じ分け方)の交点で測る。
        // 頂点は Cell の原点との差を Logical で取って camera からの相対位置にする。求められなければ NaN。
        private static float RenderedSurfaceAboveCamera(out int level){
            level=-1;
            Vector3 up=(Vector3)TryUp();
            var view=new PlanetSelectionView(Logical(),planet.Selector.LogicalRadius,Matrix4x4.identity,Vector3.zero,1,null,415,0.1f,false,false);
            PlanetCellKey best=default;bool found=false;
            foreach(PlanetCellKey key in planet.LastSelected){
                PlanetCellFrame frame=planet.Selector.Frame(in key);
                float angle=Vector3.Angle(frame.ApproximateOrigin,up);
                float size=planet.Selector.QuadArc(key.Level)*PlanetTerrainSource.CellQuads/radius*Mathf.Rad2Deg;
                if(angle<=size&&(!found||key.Level>=best.Level)){best=key;found=true;}
            }
            if(!found||!planet.TryReadbackCellForDiagnostics(in best,out VirtualGeometryPackedVertex[] vertices,out PlanetCellFrame _)){return float.NaN;}
            level=best.Level;
            int side=PlanetTerrainSource.CellQuads+1;
            var relative=new Vector3[side*side];
            Vector3 originFromCamera=planet.Selector.FromCamera(in view,in best,Vector3.zero);
            int nearest=0;float closest=float.MaxValue;
            for(int i=0;i<relative.Length;i++){
                relative[i]=originFromCamera+vertices[i].Position;
                float off=(relative[i]-up*Vector3.Dot(relative[i],up)).magnitude;
                if(off<closest){closest=off;nearest=i;}
            }
            int ni=nearest%side;int nj=nearest/side;
            for(int y=Mathf.Max(0,nj-1);y<=Mathf.Min(side-2,nj);y++){
                for(int x=Mathf.Max(0,ni-1);x<=Mathf.Min(side-2,ni);x++){
                    Vector3 a=relative[y*side+x];Vector3 b=relative[y*side+x+1];Vector3 c=relative[(y+1)*side+x];Vector3 d=relative[(y+1)*side+x+1];
                    if(IntersectLine(up,a,c,b,out float t)||IntersectLine(up,b,c,d,out t)){return t;}
                }
            }
            return float.NaN;
        }

        // 原点を通る方向 direction の直線と三角形 (p0,p1,p2) の交点の、direction 方向の距離 t(負なら反対側)。
        private static bool IntersectLine(Vector3 direction,Vector3 p0,Vector3 p1,Vector3 p2,out float t){
            t=0;
            Vector3 e1=p1-p0;Vector3 e2=p2-p0;
            Vector3 h=Vector3.Cross(direction,e2);
            float det=Vector3.Dot(e1,h);
            if(Mathf.Abs(det)<1e-12f){return false;}
            float inverse=1f/det;
            Vector3 s0=-p0;
            float u=Vector3.Dot(s0,h)*inverse;
            if(u<0||u>1){return false;}
            Vector3 q=Vector3.Cross(s0,e1);
            float v=Vector3.Dot(direction,q)*inverse;
            if(v<0||u+v>1){return false;}
            t=Vector3.Dot(e2,q)*inverse;
            return true;
        }

        // 描いた面が camera より上か(1cm の余裕)。
        private static bool UnderRendered(){
            float above=RenderedSurfaceAboveCamera(out int _);
            return !float.IsNaN(above)&&above>0.01f;
        }

        // camera の惑星ローカル位置(惑星は回転・拡大していない)。
        private static LogicalVector3 Logical(){
            LogicalPosition3.TryDifference(new LogicalPosition3(cameraLogical.LocalPosition),new LogicalPosition3(planetLogical.LocalPosition),out LogicalVector3 difference);
            return difference;
        }

        private static int Finest(){
            int finest=0;
            foreach(PlanetCellKey key in planet.LastSelected){finest=Mathf.Max(finest,key.Level);}
            return finest;
        }

        // camera の位置の、惑星の中心から外向きの方向(world)。
        private static float3 TryUp(){
            if(!LogicalPosition3.TryDifference(new LogicalPosition3(cameraLogical.LocalPosition),new LogicalPosition3(planetLogical.LocalPosition),out LogicalVector3 difference)||
                !difference.TryToFloat3(out float3 up)){
                return float3.zero;
            }
            return math.normalize(up);
        }

        private static int CountHoles(Texture2D image){
            return CountHoles(image,null);
        }

        // marked が null でなければ、穴の画素を赤、地形を暗い灰色、穴でない背景を白にした画像を書く。
        private static int CountHoles(Texture2D image,Color32[] marked){
            Color32[] pixels=image.GetPixels32();
            int width=image.width;int imageHeight=image.height;
            if(!LogicalPosition3.TryDifference(new LogicalPosition3(planetLogical.LocalPosition),new LogicalPosition3(cameraLogical.LocalPosition),out LogicalVector3 difference)||
                !difference.TryToFloat3(out float3 center)){
                throw new InvalidOperationException("Planet center is not representable.");
            }
            int coarsest=planet.MaximumLevel;
            foreach(PlanetCellKey key in planet.LastSelected){coarsest=Mathf.Min(coarsest,key.Level);}
            float edge=planet.Selector.QuadArc(coarsest)*2f;
            float inner=radius-height-edge*edge/(8f*(radius-height));
            float pixelAngle=camera.fieldOfView*Mathf.Deg2Rad/imageHeight;
            float tangent=Mathf.Tan(camera.fieldOfView*0.5f*Mathf.Deg2Rad);
            float aspect=(float)width/imageHeight;
            Quaternion rotation=camera.transform.rotation;
            int holes=0;
            for(int y=0;y<imageHeight;y++){
                for(int x=0;x<width;x++){
                    if(!IsBackground(pixels[y*width+x])){
                        if(marked!=null){marked[y*width+x]=new Color32(60,60,60,255);}
                        continue;
                    }
                    if(marked!=null){marked[y*width+x]=new Color32(255,255,255,255);}
                    Vector3 local=new Vector3(((x+0.5f)/width*2-1)*tangent*aspect,((y+0.5f)/imageHeight*2-1)*tangent,1);
                    Vector3 direction=(rotation*local).normalized;
                    if(HitsSphere(direction,center,inner,camera.nearClipPlane,pixelAngle)){
                        holes++;
                        if(marked!=null){marked[y*width+x]=new Color32(255,0,0,255);}
                    }
                }
            }
            return holes;
        }

        private static bool HitsSphere(Vector3 direction,Vector3 center,float sphereRadius,float near,float pixelAngle){
            Vector3 offset=-center;
            float b=Vector3.Dot(offset,direction);
            float distance=offset.magnitude;
            float c=(distance-sphereRadius)*(distance+sphereRadius);
            float discriminant=b*b-c;
            if(discriminant<0){return false;}
            float t=-b-Mathf.Sqrt(discriminant);
            if(t<=near){return false;}
            if(c<=0){return true;}
            float closest=(offset+direction*(-b)).magnitude;
            // 縁の許容量は 2 画素。地形の低い所(谷)は内側の球の近くまで下がるので、遠くから見た縁では、本物の谷の切れ込みと内側の球の輪郭が
            // 1 画素ほどしか離れないことがある(2026-10-11、半径 10km・高度 20km で、R−0.56Hmax の谷の切れ込みを 1 画素の許容量で穴と数えた)。
            return sphereRadius-closest>2f*Mathf.Sqrt(c)*pixelAngle;
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Record;
            if(error!=null){failures.Add(error.ToString());}
            foreach(string message in errors){failures.Add("Unity logged error: "+message);}
            report.Add("UTC="+DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+" Unity="+Application.unityVersion+" GPU="+SystemInfo.graphicsDeviceName+" API="+SystemInfo.graphicsDeviceType);
            if(failures.Count==0){report.Add("PASSED");}else{report.Add("FAILED");report.AddRange(failures);}
            Directory.CreateDirectory(results);
            File.WriteAllLines(results+"/Report.txt",report);
            int code=1;
            if(failures.Count==0){code=0;}
            EditorApplication.Exit(code);
        }
    }
}
