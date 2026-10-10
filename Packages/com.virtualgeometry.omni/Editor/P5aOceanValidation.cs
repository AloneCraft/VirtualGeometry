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
    /// P5a: 海の Layer(<see cref="PlanetOceanSource"/>)を地形と重ねて、次を確かめる(半径 600km、Hmax 3000m、風速 10m/s)。
    /// - CPU と GPU の海面の一致(時刻を固定)
    /// - 帯域制限: 波長が 2·quadLength より短い波が変位に入っていない(GPU の頂点が帯域制限した CPU の評価に一致し、全成分の評価とは違う)
    /// - 波が動く: 時刻を変えると海の画素が変わる
    /// - 海岸線の z-fighting: 時刻を固定し、camera を 0.1mm ずらして 2 回撮り、海と陸の境から 1px の帯の外で判定が入れ替わる画素を数える(地上 1.7m、100m、1km、10km)。
    ///   境の線そのものの画素は、0.1mm のずれ(約 0.001 画素)で線が画素の中心を横切ると入れ替わりうるので数えない(帯の中の数は記録する)
    /// - 高度帯の遷移: 目標誤差を 1/4 にした細かい描画と比べ、海と陸の境が 1px の帯の外へ動いた画素を数える
    /// - 穴 0
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.P5aOceanValidation.Run
    /// </summary>
    public static class P5aOceanValidation{
        private const string Root="Assets/Development/PlanetWorld/P5a";
        private const string Results="TestResults/Planet/P5a";
        private const string ShaderPath="Packages/com.virtualgeometry.omni/Shaders/RadialGlobalLit.vgshader";
        private const float Radius=600000;
        private const float Height=3000;
        private const float Target=2;
        private const float Eye=1.7f;
        private const int Width=640;
        private const int ImageHeight=480;
        private const int ConvergeLimit=600;
        private const float FrozenTime=12.3f;
        private static readonly Color32 Background=new Color32(255,0,255,255);

        private static Camera camera;
        private static LogicalTransform cameraLogical;
        private static LogicalTransform planetLogical;
        private static PlanetTerrainSource terrain;
        private static PlanetOceanSource ocean;
        private static PlanetDescentCamera descent;
        private static IEnumerator steps;
        private static double started;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();

        public static void Run(){
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
                if(EditorApplication.timeSinceStartup-started>2400){throw new TimeoutException("P5a validation timeout.");}
                EditorApplication.QueuePlayerLoopUpdate();
                if(!steps.MoveNext()){Finish(null);}
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void Setup(){
            Directory.CreateDirectory(Root);AssetDatabase.Refresh();
            UniversalRendererData renderer=AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Root+"/P5aRenderer.asset");
            if(renderer==null){
                renderer=ScriptableObject.CreateInstance<UniversalRendererData>();renderer.renderingMode=RenderingMode.ForwardPlus;
                renderer.depthPrimingMode=DepthPrimingMode.Disabled;renderer.copyDepthMode=CopyDepthMode.AfterTransparents;
                AssetDatabase.CreateAsset(renderer,Root+"/P5aRenderer.asset");
            }
            VirtualGeometryRenderFeature feature=null;
            foreach(ScriptableRendererFeature existing in renderer.rendererFeatures){if(existing is VirtualGeometryRenderFeature found){feature=found;}}
            if(feature==null){
                feature=ScriptableObject.CreateInstance<VirtualGeometryRenderFeature>();feature.name="Virtual Geometry P5a";
                renderer.rendererFeatures.Add(feature);AssetDatabase.AddObjectToAsset(feature,renderer);
            }
            var serialized=new SerializedObject(feature);
            serialized.FindProperty("selectionShader").objectReferenceValue=AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.virtualgeometry.core/Shaders/VirtualGeometrySelect.compute");
            serialized.FindProperty("surfaceShader").objectReferenceValue=Shader.Find("VirtualizedGeometry/Unlit");
            serialized.ApplyModifiedPropertiesWithoutUndo();feature.Create();
            EditorUtility.SetDirty(feature);EditorUtility.SetDirty(renderer);
            UniversalRenderPipelineAsset pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Root+"/P5aPipeline.asset");
            if(pipeline==null){pipeline=UniversalRenderPipelineAsset.Create(renderer);AssetDatabase.CreateAsset(pipeline,Root+"/P5aPipeline.asset");}
            pipeline.msaaSampleCount=1;pipeline.supportsHDR=false;pipeline.supportsCameraDepthTexture=true;pipeline.renderScale=1;
            GraphicsSettings.defaultRenderPipeline=pipeline;QualitySettings.renderPipeline=pipeline;
            AssetDatabase.SaveAssets();

            Shader shader=AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if(shader==null){throw new InvalidOperationException("Shader not found: "+ShaderPath);}
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            camera=new GameObject("P5a Camera").AddComponent<Camera>();
            camera.nearClipPlane=0.1f;camera.farClipPlane=200000;camera.fieldOfView=60;camera.clearFlags=CameraClearFlags.SolidColor;
            camera.backgroundColor=new Color(1,0,1,1);camera.allowHDR=false;camera.allowMSAA=false;
            cameraLogical=camera.gameObject.AddComponent<LogicalTransform>();
            camera.gameObject.AddComponent<RadialCamera>().SetIdentityViewScale();
            Light sun=new GameObject("P5a Sun").AddComponent<Light>();sun.type=LightType.Directional;sun.transform.rotation=Quaternion.Euler(40,-30,0);
            // 地形は緑、海は青。海と陸の判定は色(青が緑より強いか)で行う。
            Material land=new Material(shader);land.SetColor("_BaseColor",new Color(0.35f,0.55f,0.25f,1));land.SetFloat("_Smoothness",0.2f);
            Material sea=new Material(shader);sea.SetColor("_BaseColor",new Color(0.05f,0.2f,0.6f,1));sea.SetFloat("_Smoothness",0.85f);
            GameObject holder=new GameObject("P5a Planet");holder.SetActive(false);
            planetLogical=holder.AddComponent<LogicalTransform>();
            terrain=holder.AddComponent<PlanetTerrainSource>();
            ocean=holder.AddComponent<PlanetOceanSource>();
            holder.AddComponent<OmniPlanetPlacement>();
            var terrainSettings=new SerializedObject(terrain);
            terrainSettings.FindProperty("poolCapacity").intValue=2048;
            terrainSettings.FindProperty("maximumDrawSlots").intValue=2048;
            terrainSettings.FindProperty("generationBudget").intValue=32;
            terrainSettings.FindProperty("baseWavelength").floatValue=240000;
            terrainSettings.FindProperty("octaves").intValue=26;
            terrainSettings.ApplyModifiedPropertiesWithoutUndo();
            terrain.Material=land;
            terrain.Configure(Radius,Height,PlanetLayerSource.GpuMaximumLevel,Target);
            var oceanSettings=new SerializedObject(ocean);
            oceanSettings.FindProperty("poolCapacity").intValue=2048;
            oceanSettings.FindProperty("maximumDrawSlots").intValue=2048;
            oceanSettings.FindProperty("generationBudget").intValue=512;
            oceanSettings.ApplyModifiedPropertiesWithoutUndo();
            ocean.Terrain=terrain;ocean.Material=sea;ocean.AdvanceTime=false;ocean.WaveTime=FrozenTime;
            ocean.Configure(10f,24,0.5f,PlanetLayerSource.GpuMaximumLevel,Target);
            if(!LogicalVector3.TryFromFloat3(new float3(0,Radius*3f,0),out LogicalVector3 far)){throw new InvalidOperationException("far");}
            cameraLogical.LocalPosition=far;cameraLogical.SyncUnityTransform();
            holder.SetActive(true);
            descent=camera.gameObject.AddComponent<PlanetDescentCamera>();
            descent.Planet=terrain;descent.Ocean=ocean;descent.EyeHeight=Eye;descent.ReadInput=false;
        }

        private static IEnumerator Steps(){
            report.Add("planet: radius="+Radius+" height="+Height+" ocean waves="+ocean.WaveCount+" amplitude="+ocean.WaveAmplitude.ToString("G4",CultureInfo.InvariantCulture)+
                "m longest="+ocean.LongestWavelength.ToString("G4",CultureInfo.InvariantCulture)+"m target="+Target+"px");
            // 海岸を探す: 地表の高さの符号が変わる 2 点の間を二分する。
            if(!FindCoast(out Vector3 coast,out Vector3 seaward)){throw new InvalidOperationException("海岸が見つかりません。");}
            report.Add("coast direction="+coast.ToString("F5")+" seaward="+seaward.ToString("F3"));
            var views=new (string Name,float Altitude,float Inland,float Pitch)[]{("CoastEye1.7m",Eye,20f,4f),("Coast100m",100f,0f,35f),("Coast1km",1000f,0f,45f),("Coast10km",10000f,0f,55f)};
            for(int v=0;v<views.Length;v++){
                var view=views[v];
                // 陸側へ inland m 戻った点の上に置き、海の方を pitch 度下に見る。
                Vector3 direction=(coast-seaward*(view.Inland/Radius)).normalized;
                foreach(object step in Teleport(direction,view.Altitude)){yield return step;}
                LookToward(seaward,view.Pitch);
                foreach(object step in Converge(view.Name)){yield return step;}
                Texture2D image=Capture();
                Save(view.Name,image);
                int holes=CountHoles(image);
                ClassCounts(image,out int seaPixels,out int landPixels);
                report.Add(view.Name+": altitude="+descent.Altitude.ToString("G5",CultureInfo.InvariantCulture)+" holes="+holes+" sea="+seaPixels+" land="+landPixels+
                    " terrainCells="+terrain.LastSelected.Count+" oceanCells="+ocean.LastSelected.Count);
                if(holes!=0){failures.Add(view.Name+": 穴が "+holes+" 画素ありました。");}
                if(seaPixels==0||landPixels==0){failures.Add(view.Name+": 海と陸の両方が写っていません(海 "+seaPixels+"、陸 "+landPixels+")。");}
                // z-fighting: 0.1mm だけ視線方向へずらして撮り直し、海と陸の判定が入れ替わる画素を数える。
                Nudge(1e-4f);
                Capture();
                Texture2D nudged=Capture();
                Nudge(-1e-4f);
                int flipped=CountFlips(image,nudged,view.Name+"Flip",out int onContour);
                report.Add(view.Name+": zFightingFlips="+flipped+" contourFlips="+onContour);
                if(flipped!=0){failures.Add(view.Name+": 海と陸の境から離れた所で、判定が 0.1mm のずれで入れ替わる画素が "+flipped+" ありました(z-fighting)。");}
                CompareCpuGpu(view.Name);
                // 高度帯の遷移: 目標誤差 1/4 の細かい描画と、海と陸の境を比べる。
                terrain.TargetPixels=Target*0.25f;ocean.TargetPixels=Target*0.25f;
                foreach(object step in Converge(view.Name+"Fine")){yield return step;}
                Texture2D fine=Capture();
                Save(view.Name+"Fine",fine);
                CompareClasses(view.Name,image,fine);
                terrain.TargetPixels=Target;ocean.TargetPixels=Target;
                foreach(object step in Converge(view.Name+"Restore")){yield return step;}
            }
            // 波が動く: 時刻を進めると海の画素が変わる。
            var last=views[0];
            Vector3 eyeDirection=(coast-seaward*(last.Inland/Radius)).normalized;
            foreach(object step in Teleport(eyeDirection,last.Altitude)){yield return step;}
            LookToward(seaward,last.Pitch);
            foreach(object step in Converge("Animation")){yield return step;}
            Texture2D before=Capture();
            Save("AnimationBefore",before);
            // 海の作り直しは 1 frame に 1 回(次の frame の描画の前)なので、時刻を変えた後に frame を進めてから撮る。
            ocean.WaveTime=FrozenTime+1.5f;
            for(int frame=0;frame<3;frame++){
                Capture();
                yield return null;
            }
            Texture2D after=Capture();
            Save("AnimationAfter",after);
            int changed=CountChangedSea(before,after);
            report.Add("animation: changedSeaPixels="+changed);
            if(changed==0){failures.Add("時刻を変えても海の画素が変わりませんでした。");}
            ocean.WaveTime=FrozenTime;
            report.Add("pool: terrain used="+terrain.UsedSlots+" ocean used="+ocean.UsedSlots+" lastError="+(terrain.LastError??"(none)")+"/"+(ocean.LastError??"(none)"));
            if(terrain.LastError!=null||ocean.LastError!=null){failures.Add("設定エラー: "+terrain.LastError+" "+ocean.LastError);}
        }

        // 地表の高さの符号が変わる 2 方向を、決まった順の方向で探し、その間を二分して海岸(高さ 0)の方向にする。seaward は海側への接線方向。
        private static bool FindCoast(out Vector3 coast,out Vector3 seaward){
            coast=default;seaward=default;
            var random=new System.Random(7);
            for(int attempt=0;attempt<2000;attempt++){
                Vector3 a=new Vector3((float)random.NextDouble()*2-1,(float)random.NextDouble()*2-1,(float)random.NextDouble()*2-1).normalized;
                Vector3 step=Vector3.Cross(a,Vector3.up).normalized*0.002f;
                Vector3 b=(a+step).normalized;
                if(!SurfaceHeight(a,out float ha)||!SurfaceHeight(b,out float hb)){continue;}
                if(ha>0==hb>0){continue;}
                Vector3 landSide=a;Vector3 seaSide=b;
                if(ha<=0){landSide=b;seaSide=a;}
                // 海側の方向は二分する前の 2 点から求める(二分した後の 2 点は float で区別できないほど近い)。
                seaward=(seaSide-landSide).normalized;
                for(int i=0;i<40;i++){
                    Vector3 middle=(landSide+seaSide).normalized;
                    if(!SurfaceHeight(middle,out float hm)){return false;}
                    if(hm>0){landSide=middle;}else{seaSide=middle;}
                }
                coast=landSide;
                seaward=(seaward-coast*Vector3.Dot(seaward,coast)).normalized;
                return true;
            }
            return false;
        }

        private static bool SurfaceHeight(Vector3 direction,out float height){
            height=0;
            if(!LogicalVector3.TryFromFloat3(direction*Radius,out LogicalVector3 point)){return false;}
            return terrain.TrySampleSurface(in point,out height,out float _);
        }

        private static IEnumerable<object> Teleport(Vector3 direction,float altitude){
            int waited=0;
            while(!descent.PrepareTeleport(direction,altitude)){
                if(descent.LastFailure!=null){throw new InvalidOperationException("PrepareTeleport: "+descent.LastFailure);}
                if(waited>=300){failures.Add("瞬間移動の準備が 300 frame で終わりませんでした。");break;}
                Capture(320,240);
                waited++;
                yield return null;
            }
            if(!descent.Teleport(direction,altitude)){throw new InvalidOperationException("Teleport: "+descent.LastFailure);}
        }

        // camera を、その場の接平面の方向 toward へ向け、pitch 度下を見る。
        private static void LookToward(Vector3 toward,float pitch){
            Vector3 up=(Vector3)TryUp();
            Vector3 forward=Vector3.ProjectOnPlane(toward,up).normalized;
            Vector3 right=Vector3.Cross(up,forward);
            Quaternion rotation=Quaternion.LookRotation(Quaternion.AngleAxis(pitch,right)*forward,up);
            if(!global::Omni.Logical.Quaternion.TryFromFloat4(rotation.x,rotation.y,rotation.z,rotation.w,out global::Omni.Logical.Quaternion value)){throw new InvalidOperationException("rotation");}
            cameraLogical.LocalRotation=value;
            cameraLogical.SyncUnityTransform();
        }

        // 視線方向へ distance だけ動かす(Logical のまま)。
        private static void Nudge(float distance){
            Vector3 forward=camera.transform.forward*distance;
            if(!LogicalVector3.TryFromFloat3(forward,out LogicalVector3 delta)||!cameraLogical.TranslateWorld(in delta)){throw new InvalidOperationException("nudge");}
            cameraLogical.SyncUnityTransform();
        }

        private static float3 TryUp(){
            if(!LogicalPosition3.TryDifference(new LogicalPosition3(cameraLogical.LocalPosition),new LogicalPosition3(planetLogical.LocalPosition),out LogicalVector3 difference)||
                !difference.TryToFloat3(out float3 up)){
                return float3.zero;
            }
            return math.normalize(up);
        }

        private static IEnumerable<object> Converge(string name){
            int stable=0;int previous=-1;int frames=0;
            while(frames<ConvergeLimit){
                Capture();
                int signature=terrain.LastSelected.Count*31+ocean.LastSelected.Count+ocean.UsedSlots*7;
                foreach(PlanetCellKey key in terrain.LastSelected){signature^=key.GetHashCode();}
                foreach(PlanetCellKey key in ocean.LastSelected){signature^=key.GetHashCode()*17;}
                if(terrain.PendingGeneration==0&&signature==previous){stable++;}else{stable=0;}
                previous=signature;
                frames++;
                if(stable>=2){break;}
                yield return null;
            }
            if(frames>=ConvergeLimit){failures.Add(name+": "+ConvergeLimit+" frame で選択が収束しませんでした。");}
            report.Add(name+": converged frames="+frames+" terrainCells="+terrain.LastSelected.Count+" oceanCells="+ocean.LastSelected.Count);
        }

        // 選択中の海の Cell(階層ごとに最大 2 個)の格子の 9 点で、GPU の頂点・水深と CPU の評価を比べる。
        // あわせて、帯域制限で落とした波があれば、全成分の評価との差が GPU と CPU の差より十分大きいことを確かめる。
        private static void CompareCpuGpu(string name){
            var perLevel=new Dictionary<int,int>();
            float worst=0;float worstDepth=0;int points=0;int animatedLevels=0;int droppedCells=0;float dropped=0;
            int side=PlanetLayerSource.CellQuads+1;
            foreach(PlanetCellKey key in ocean.LastSelected){
                perLevel.TryGetValue(key.Level,out int count);
                if(count>=2){continue;}
                if(!ocean.TryReadbackCellForDiagnostics(in key,out VirtualGeometryPackedVertex[] vertices,out PlanetCellFrame _)){continue;}
                perLevel[key.Level]=count+1;
                if(ocean.IsAnimated(key.Level)&&count==0){animatedLevels++;}
                for(int j=0;j<=PlanetLayerSource.CellQuads;j+=PlanetLayerSource.CellQuads/2){
                    for(int i=0;i<=PlanetLayerSource.CellQuads;i+=PlanetLayerSource.CellQuads/2){
                        if(!ocean.TryEvaluateVertexForDiagnostics(in key,i,j,true,out Vector3 cpu,out float depth)){continue;}
                        VirtualGeometryPackedVertex gpu=vertices[j*side+i];
                        worst=Mathf.Max(worst,(gpu.Position-cpu).magnitude);
                        if(ocean.IsAnimated(key.Level)&&ocean.DropsWaves(key.Level)&&ocean.TryEvaluateVertexForDiagnostics(in key,i,j,false,out Vector3 full,out float _)){
                            dropped=Mathf.Max(dropped,(gpu.Position-full).magnitude);
                        }
                        worstDepth=Mathf.Max(worstDepth,Mathf.Abs(gpu.UV.x-depth));
                        points++;
                    }
                }
                if(ocean.IsAnimated(key.Level)&&ocean.DropsWaves(key.Level)){droppedCells++;}
            }
            report.Add(name+": oceanCpuGpu points="+points+" levels="+perLevel.Count+" animatedLevels="+animatedLevels+" maxError="+worst.ToString("G4",CultureInfo.InvariantCulture)+
                "m maxDepthError="+worstDepth.ToString("G4",CultureInfo.InvariantCulture)+"m droppedCells="+droppedCells+" maxDifferenceFromAllWaves="+dropped.ToString("G4",CultureInfo.InvariantCulture)+"m");
            // 帯域制限: 波を落とす Cell では、GPU の頂点は帯域制限した CPU の評価に一致し、全成分の評価とは(落とした波の分だけ)違う。
            if(droppedCells>0&&dropped<=worst*10f){failures.Add(name+": 帯域制限で落とすはずの波が、GPU の変位に入っているように見えます(全成分との差 "+dropped+"m、CPU との差 "+worst+"m)。");}
            if(points==0){failures.Add(name+": CPU と GPU を比べる海の頂点がありません。");}
            if(worst>1e-2f){failures.Add(name+": 海面の CPU と GPU の差が 1cm を超えました(最大 "+worst+"m)。");}
        }

        // 海(青が緑より強い)と陸の画素の数。
        private static void ClassCounts(Texture2D image,out int sea,out int land){
            sea=0;land=0;
            foreach(Color32 pixel in image.GetPixels32()){
                int c=Classify(pixel);
                if(c==1){sea++;}
                if(c==2){land++;}
            }
        }

        // 0: 背景、1: 海、2: 陸。
        private static int Classify(Color32 pixel){
            if(IsBackground(pixel)){return 0;}
            if(pixel.b>pixel.g){return 1;}
            return 2;
        }

        // 2 枚で海と陸の判定が入れ替わった画素(背景との境は数えない)。a の判定の境から 1px の帯の中の数は onContour に分ける。
        private static int CountFlips(Texture2D a,Texture2D b,string name,out int onContour){
            Color32[] pa=a.GetPixels32();Color32[] pb=b.GetPixels32();
            bool[] band=ClassBand(pa,a.width,a.height);
            int flips=0;onContour=0;
            var mask=new Color32[pa.Length];
            for(int i=0;i<pa.Length;i++){
                int ca=Classify(pa[i]);int cb=Classify(pb[i]);
                mask[i]=new Color32(0,0,0,255);
                if(ca==0||cb==0||ca==cb){continue;}
                if(band[i]){onContour++;mask[i]=new Color32(255,255,0,255);continue;}
                flips++;mask[i]=new Color32(255,0,0,255);
            }
            if(flips+onContour>0){
                var texture=new Texture2D(a.width,a.height,TextureFormat.RGBA32,false);texture.SetPixels32(mask);texture.Apply();
                Save(name,texture);
                UnityEngine.Object.DestroyImmediate(texture);
            }
            return flips;
        }

        // 細かい描画を基準に、海と陸の境から 1px の帯の外で判定が違う画素を数える。
        private static void CompareClasses(string name,Texture2D image,Texture2D reference){
            Color32[] a=image.GetPixels32();Color32[] b=reference.GetPixels32();
            int width=image.width;int height=image.height;
            var classes=new int[b.Length];
            for(int i=0;i<b.Length;i++){classes[i]=Classify(b[i]);}
            bool[] band=ClassBand(b,width,height);
            int different=0;int outside=0;
            var mask=new Color32[a.Length];
            for(int i=0;i<a.Length;i++){
                mask[i]=new Color32(0,0,0,255);
                if(Classify(a[i])==classes[i]){continue;}
                different++;
                if(band[i]){mask[i]=new Color32(255,255,0,255);continue;}
                outside++;mask[i]=new Color32(255,0,0,255);
            }
            var texture=new Texture2D(width,height,TextureFormat.RGBA32,false);texture.SetPixels32(mask);texture.Apply();
            Save(name+"ClassDifference",texture);
            UnityEngine.Object.DestroyImmediate(texture);
            report.Add(name+": coastline differing="+different+" outside1px="+outside);
            if(outside!=0){failures.Add(name+": 高度帯の遷移で、海と陸の境が細かい基準から 1px より離れた画素が "+outside+" ありました。");}
        }

        // 判定(背景・海・陸)の境から 1px 以内の画素。
        private static bool[] ClassBand(Color32[] pixels,int width,int height){
            var classes=new int[pixels.Length];
            for(int i=0;i<pixels.Length;i++){classes[i]=Classify(pixels[i]);}
            var band=new bool[pixels.Length];
            for(int y=0;y<height;y++){
                for(int x=0;x<width;x++){
                    int value=classes[y*width+x];
                    bool edge=false;
                    if(x>0&&classes[y*width+x-1]!=value){edge=true;}
                    if(x<width-1&&classes[y*width+x+1]!=value){edge=true;}
                    if(y>0&&classes[(y-1)*width+x]!=value){edge=true;}
                    if(y<height-1&&classes[(y+1)*width+x]!=value){edge=true;}
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

        private static int CountChangedSea(Texture2D a,Texture2D b){
            Color32[] pa=a.GetPixels32();Color32[] pb=b.GetPixels32();
            int changed=0;
            for(int i=0;i<pa.Length;i++){
                if(Classify(pa[i])!=1){continue;}
                if(pa[i].r!=pb[i].r||pa[i].g!=pb[i].g||pa[i].b!=pb[i].b){changed++;}
            }
            return changed;
        }

        private static Texture2D Capture(int width=Width,int height=ImageHeight){
            return M0Validation.Capture(camera,width,height);
        }

        private static void Save(string name,Texture2D image){
            File.WriteAllBytes(Results+"/"+name+".png",image.EncodeToPNG());
        }

        private static bool IsBackground(Color32 color){
            return color.r==Background.r&&color.g==Background.g&&color.b==Background.b;
        }

        // 内側の球(半径 R−Hmax から弦の沈み込みを引いたもの)に当たる光線の画素が背景色なら穴(P2・P3b・P4b と同じ判定、縁の許容量 2 画素)。
        private static int CountHoles(Texture2D image){
            Color32[] pixels=image.GetPixels32();
            int width=image.width;int imageHeight=image.height;
            if(!LogicalPosition3.TryDifference(new LogicalPosition3(planetLogical.LocalPosition),new LogicalPosition3(cameraLogical.LocalPosition),out LogicalVector3 difference)||
                !difference.TryToFloat3(out float3 center)){
                throw new InvalidOperationException("Planet center is not representable.");
            }
            int coarsest=terrain.MaximumLevel;
            foreach(PlanetCellKey key in terrain.LastSelected){coarsest=Mathf.Min(coarsest,key.Level);}
            float edge=terrain.Selector.QuadArc(coarsest)*2f;
            float inner=Radius-Height-edge*edge/(8f*(Radius-Height));
            float pixelAngle=camera.fieldOfView*Mathf.Deg2Rad/imageHeight;
            float tangent=Mathf.Tan(camera.fieldOfView*0.5f*Mathf.Deg2Rad);
            float aspect=(float)width/imageHeight;
            Quaternion rotation=camera.transform.rotation;
            int holes=0;
            for(int y=0;y<imageHeight;y++){
                for(int x=0;x<width;x++){
                    if(!IsBackground(pixels[y*width+x])){continue;}
                    Vector3 local=new Vector3(((x+0.5f)/width*2-1)*tangent*aspect,((y+0.5f)/imageHeight*2-1)*tangent,1);
                    Vector3 direction=(rotation*local).normalized;
                    if(HitsSphere(direction,center,inner,camera.nearClipPlane,pixelAngle)){holes++;}
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
            return sphereRadius-closest>2f*Mathf.Sqrt(c)*pixelAngle;
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Record;
            if(error!=null){failures.Add(error.ToString());}
            foreach(string message in errors){failures.Add("Unity logged error: "+message);}
            report.Add("UTC="+DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+" Unity="+Application.unityVersion+" GPU="+SystemInfo.graphicsDeviceName+" API="+SystemInfo.graphicsDeviceType);
            if(failures.Count==0){report.Add("PASSED");}else{report.Add("FAILED");report.AddRange(failures);}
            Directory.CreateDirectory(Results);
            File.WriteAllLines(Results+"/Report.txt",report);
            int code=1;
            if(failures.Count==0){code=0;}
            EditorApplication.Exit(code);
        }
    }
}
