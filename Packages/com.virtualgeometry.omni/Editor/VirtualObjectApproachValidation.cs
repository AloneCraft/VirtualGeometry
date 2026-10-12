using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using VirtualGeometry.VirtualObjects;
using VirtualGeometry.World;
using VirtualizedGeometry;
using VirtualizedGeometry.Editor;
using LogicalPosition3=Omni.Logical.Position3;
using LogicalQuaternion=Omni.Logical.Quaternion;
using LogicalScalar=Omni.Logical.Scalar;
using LogicalTransform=Omni.Logical.Transform;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// M1c: 仮想オブジェクトの建物(なし → ビルボード → VG メッシュ → プレハブ → 子の「型 + 番号」)を、半径 600 km の惑星の地表に 2 か所 × 9 棟置き、
    /// 軌道から地上 1.7 m まで、降下カメラ(<see cref="PlanetDescentCamera"/>)の速さを「倍率 × 安全速度」に抑えて降りる。
    /// 表現の準備には遅延(<see cref="VirtualObjectBudget.InjectedPrepareDelaySeconds"/>)を入れ、倍率 0.5 / 1 / 2 / 4 で走らせる。
    /// <list type="bullet">
    /// <item>穴 0: 毎 frame、望む帯が覆われていない実体(<see cref="VirtualObjectStatus.Uncovered"/>)が 0。加えて画素でも、前の frame に写っていた建物(赤)が次の frame で 0 画素になったら穴と数える。</item>
    /// <item>段の往復 0: 近づく間に、見せている帯が粗い方へ戻らない。</item>
    /// <item>予算: 同時の準備数、1 frame の表示数、GPU の memory の見積もりが上限以下。</item>
    /// <item>遅れ: 倍率 1 以下では、望む帯より粗い帯で見せている実体の frame の割合が 1% 以下。倍率 4 では遅れが出る(安全速度が効いている確認)。</item>
    /// <item>子: 近い建物は子の帯で見せ、番号ごとに外壁の型、上書きした番号だけガラスの型。子の型のアセットは 2 つのまま。</item>
    /// <item>瞬間移動: 移動先を <see cref="VirtualObjectSystem.PrepareTeleport"/> で準備してから移ると、直後の frame から穴 0。</item>
    /// <item>決定性: 実行ごとに地面に置いた位置(Logical)と子の割り当ての hash が同じ。</item>
    /// </list>
    /// 建物は照明の影響を受けない赤の材質(画素の判定用)。見た目の確認用に、通常の材質で地上の画像も 1 枚撮る。
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.VirtualObjectApproachValidation.Run
    /// </summary>
    public static class VirtualObjectApproachValidation{
        private const string Results="TestResults/Planet/VirtualObjectApproach";
        private const string Root="Assets/Development/PlanetWorld/VirtualObjectApproach";
        private const string ShaderPath="Packages/com.virtualgeometry.omni/Shaders/RadialGlobalLit.vgshader";
        private const float PlanetRadius=600000f;
        private const float TerrainHeight=300f;
        private const float Eye=1.7f;
        private const int Width=320;
        private const int ImageHeight=240;
        private const float Step=1f/30f;
        private const float InjectedDelay=0.4f;
        private const int DescentLimit=6000;
        private const float Spacing=70f;
        // 降りる点(場所の中心から西へ)。西の列の建物の西の面(中心から −82 m)から 6 m の所で、地上ではその建物の境界の球の中(子の帯)に入る。
        private const float LandingEast=-88f;
        private static readonly float[] Multipliers={0.5f,1f,2f,4f};
        private static readonly Color32 Background=new Color32(255,0,255,255);

        private static Camera camera;
        private static Light sun;
        private static LogicalTransform cameraLogical;
        private static LogicalTransform planetLogical;
        private static PlanetTerrainSource planet;
        private static PlanetDescentCamera descent;
        private static VirtualObjectSystem system;
        private static OmniObjectPlacement placement;
        private static VirtualObjectSampleAssets.BuildingKind kind;
        private static VirtualObjectSampleAssets.BuildingKind appearance;
        private static VirtualObjectSet diagnosticSet;
        private static VirtualObjectSet appearanceSet;
        private static Vector3 up;
        private static Vector3 east;
        private static Vector3 north;
        private static readonly float[] siteHeights=new float[2];
        // 画素の判定で数えた建物の画素の合計(判定が空でないことの確認)。
        private static long observedPixels;
        private static IEnumerator steps;
        private static double started;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();
        private static readonly StringBuilder csv=new StringBuilder();

        public static void Run(){
            report.Clear();failures.Clear();errors.Clear();csv.Clear();started=EditorApplication.timeSinceStartup;
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
                if(EditorApplication.timeSinceStartup-started>3400){throw new TimeoutException("VirtualObjectApproach validation timeout.");}
                EditorApplication.QueuePlayerLoopUpdate();
                if(!steps.MoveNext()){Finish(null);}
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void Setup(){
            Directory.CreateDirectory(Root);
            MoonBallSceneBuilder.UsePipeline();
            // 新しい scene を開くと使っていないアセットが解放されるので、アセットは scene を作った後に読む。
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            kind=VirtualObjectSampleAssets.EnsureBuildingKind(true);
            appearance=VirtualObjectSampleAssets.EnsureBuildingKind(false);
            Shader shader=AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if(shader==null){throw new InvalidOperationException("Shader not found: "+ShaderPath);}

            camera=new GameObject("Approach Camera").AddComponent<Camera>();
            camera.nearClipPlane=0.1f;camera.farClipPlane=200000f;camera.fieldOfView=60f;camera.clearFlags=CameraClearFlags.SolidColor;
            camera.backgroundColor=new Color(1f,0f,1f,1f);camera.allowHDR=false;camera.allowMSAA=false;
            // 表現の帯の選択(画面の高さ)と撮影の解像度を合わせる。
            camera.targetTexture=new RenderTexture(Width,ImageHeight,24){name="Approach Target",hideFlags=HideFlags.DontSave};
            cameraLogical=camera.gameObject.AddComponent<LogicalTransform>();
            camera.gameObject.AddComponent<global::Omni.RadialCamera>().SetIdentityViewScale();
            sun=new GameObject("Approach Sun").AddComponent<Light>();
            sun.type=LightType.Directional;sun.shadows=LightShadows.None;
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(0.25f,0.25f,0.28f);

            Material ground=new Material(shader);ground.SetColor("_BaseColor",new Color(0.35f,0.55f,0.3f,1f));
            GameObject holder=new GameObject("Approach Planet");holder.SetActive(false);
            planetLogical=holder.AddComponent<LogicalTransform>();
            planet=holder.AddComponent<PlanetTerrainSource>();
            holder.AddComponent<OmniPlanetPlacement>();
            var settings=new SerializedObject(planet);
            settings.FindProperty("poolCapacity").intValue=2048;
            settings.FindProperty("maximumDrawSlots").intValue=2048;
            settings.FindProperty("generationBudget").intValue=32;
            settings.FindProperty("baseWavelength").floatValue=20000f;
            settings.FindProperty("octaves").intValue=20;
            settings.ApplyModifiedPropertiesWithoutUndo();
            planet.Material=ground;
            planet.Configure(PlanetRadius,TerrainHeight,PlanetTerrainSource.GpuMaximumLevel,2f);
            HeightShiftOverride.Apply(planet);
            holder.SetActive(true);

            // 建物を置く 2 か所(A: 降りる所、B: 瞬間移動の先。A から東へ 3 km)。
            up=new Vector3(0.3f,1f,0.2f).normalized;
            east=Vector3.Cross(up,Vector3.forward).normalized;
            north=Vector3.Cross(east,up).normalized;
            // 太陽は camera の後ろ(西)の上から。camera から見える西の面と屋根が照らされ、画素の判定の赤が暗くならない(日陰の面は真っ黒になる)。
            sun.transform.rotation=Quaternion.LookRotation(east*0.6f-up*0.8f,up);
            diagnosticSet=CreateSet(kind.Definition);
            appearanceSet=CreateSet(appearance.Definition);
            for(int site=0;site<2;site++){
                Vector3 direction=Direction(site,0f,0f);
                if(!LogicalVector3.TryFromFloat3((float3)(direction*PlanetRadius),out LogicalVector3 probe)||!planet.TrySampleSurface(in probe,out float height,out float _)){throw new InvalidOperationException("site height");}
                siteHeights[site]=height;
            }

            GameObject objects=new GameObject("Virtual Objects");objects.SetActive(false);
            objects.transform.SetParent(holder.transform,false);
            objects.AddComponent<LogicalTransform>();
            system=objects.AddComponent<VirtualObjectSystem>();
            placement=objects.AddComponent<OmniObjectPlacement>();
            system.ManualStep=true;system.GroundTerrain=planet;system.Sets=new[]{diagnosticSet};
            system.Budget=new VirtualObjectBudget();
            objects.SetActive(true);
            system.AddCamera(camera);

            if(!LogicalVector3.TryFromFloat3(new float3(0f,PlanetRadius*3f,0f),out LogicalVector3 far)){throw new InvalidOperationException("far");}
            cameraLogical.LocalPosition=far;cameraLogical.SyncUnityTransform();
            descent=camera.gameObject.AddComponent<PlanetDescentCamera>();
            descent.Planet=planet;descent.EyeHeight=Eye;descent.ReadInput=false;
            var cameraSettings=new SerializedObject(descent);
            cameraSettings.FindProperty("speedPerAltitude").floatValue=2f;
            cameraSettings.ApplyModifiedPropertiesWithoutUndo();
            // 一括描画の材質を準備する(仮想オブジェクトの材質は OmniObjectPlacement が申告する)。
            report.Add("brg: "+global::Omni.Editor.BatchRendering.RadialSceneBrgEditorIntegration.PrepareLoadedSceneSidecarsForAutomation());
        }

        // 2 か所 × 3×3 棟。位置は惑星の局所(地表の半径の球の上。地面への置き方は仮想オブジェクトが決める)。
        private static VirtualObjectSet CreateSet(VirtualObject definition){
            var set=ScriptableObject.CreateInstance<VirtualObjectSet>();
            set.hideFlags=HideFlags.DontSave;
            var records=new List<VirtualObjectPlacementRecord>();
            for(int site=0;site<2;site++){
                for(int i=0;i<9;i++){
                    double x=(i%3-1)*Spacing+site*3000.0;double z=(i/3-1)*Spacing;
                    double3 position=(double3)(float3)up*PlanetRadius+(double3)(float3)east*x+(double3)(float3)north*z;
                    records.Add(new VirtualObjectPlacementRecord{
                        Id="b"+site+"-"+i,Prefab=definition,
                        X=position.x.ToString("R",CultureInfo.InvariantCulture),Y=position.y.ToString("R",CultureInfo.InvariantCulture),Z=position.z.ToString("R",CultureInfo.InvariantCulture),
                        Rotation=Quaternion.identity,Scale=1f,Seed=(uint)(site*100+i),
                    });
                }
            }
            set.Placements=records.ToArray();
            return set;
        }

        // 場所 site の中心(惑星の局所)から、east・north へずらした点の方向。
        private static Vector3 Direction(int site,float offsetEast,float offsetNorth){
            Vector3 point=up*PlanetRadius+east*(site*3000f+offsetEast)+north*offsetNorth;
            return point.normalized;
        }

        private static IEnumerator Steps(){
            report.Add("planet: radius="+PlanetRadius+" terrainHeight="+TerrainHeight+" buildings=2x9 spacing="+Spacing+"m injectedDelay="+InjectedDelay+"s capture="+Width+"x"+ImageHeight+
                " siteHeights="+siteHeights[0].ToString("0.0",CultureInfo.InvariantCulture)+","+siteHeights[1].ToString("0.0",CultureInfo.InvariantCulture));
            report.Add("kind: bands="+Describe(kind.Definition.Bands)+" childTemplates="+kind.Definition.ChildTemplates.Length+" overriddenNumber="+kind.OverriddenNumber);
            csv.Append("run,frame,altitude,speed,safeSpeed,uncovered,lagging,nodes,preparing,ready,active,activations,gpuMB,shownNearest,pixelHoles\n");
            string firstHash=null;
            foreach(float multiplier in Multipliers){
                RunState state=new RunState{Multiplier=multiplier};
                foreach(object step in Descend(state)){yield return step;}
                if(firstHash==null){firstHash=state.Hash;}
                if(state.Hash!=firstHash){failures.Add("決定性: 倍率 "+multiplier+" の実行の、置いた位置と子の割り当ての hash が最初の実行と違います("+state.Hash+" と "+firstHash+")。");}
                if(multiplier==1f){
                    foreach(object step in Teleport()){yield return step;}
                }
            }
            report.Add("determinism hash="+firstHash);
            foreach(object step in Appearance()){yield return step;}
        }

        private sealed class RunState{
            public float Multiplier;
            public string Hash;
        }

        // 1 回の降下。
        private static IEnumerable<object> Descend(RunState state){
            string name="x"+state.Multiplier.ToString("0.##",CultureInfo.InvariantCulture);
            system.Shutdown();
            system.Budget.InjectedPrepareDelaySeconds=0f;
            system.Initialize();
            Vector3 start=Direction(0,LandingEast,0f);
            if(!descent.Teleport(start,PlanetRadius*1.5f)){throw new InvalidOperationException("Teleport: "+descent.LastFailure);}
            LookAt(0);
            system.NotifyTeleport();
            descent.SpeedLimit=float.PositiveInfinity;
            // 軌道で、常駐の帯(ビルボード)が揃うまで待つ(遅延なし)。
            int warm=0;
            while(warm<600){
                system.Step(Step);
                Capture();
                warm++;
                if(system.Status.Preparing==0&&system.Status.Lagging==0&&system.Status.Uncovered==0&&warm>10){break;}
                yield return null;
            }
            state.Hash=PlacementHash();
            system.Budget.InjectedPrepareDelaySeconds=InjectedDelay;
            observedPixels=0;
            VirtualObjectBudget budget=system.Budget;
            int frames=0;int uncoveredFrames=0;int laggingNodeFrames=0;int evaluatedNodeFrames=0;int pixelHoles=0;int oscillations=0;int saved=0;
            int peakPreparing=0;int peakActivations=0;long peakGpu=0;float minimumSafe=float.PositiveInfinity;
            var previousShown=new Dictionary<VirtualObjectNode,int>();
            var previousPixels=new Dictionary<VirtualObjectNode,int>();
            while(frames<DescentLimit&&descent.Altitude>Eye*1.01f){
                float safe=system.SafeSpeed;
                float limit=float.PositiveInfinity;
                if(!float.IsPositiveInfinity(safe)){limit=state.Multiplier*safe;}
                descent.SpeedLimit=limit;
                if(!descent.Move(new Vector3(0f,-1f,0f),Vector2.zero,Step)){throw new InvalidOperationException("Move: "+descent.LastFailure);}
                LookAt(0);
                system.Step(Step);
                Texture2D image=Capture();
                VirtualObjectStatus status=system.Status;
                if(status.Uncovered>0){uncoveredFrames++;}
                laggingNodeFrames+=status.Lagging;evaluatedNodeFrames+=status.Nodes;
                peakPreparing=Mathf.Max(peakPreparing,status.Preparing);peakActivations=Mathf.Max(peakActivations,status.Activations);peakGpu=Math.Max(peakGpu,status.GpuBytes);
                minimumSafe=Mathf.Min(minimumSafe,status.SafeSpeed);
                int frameHoles=PixelHoles(image,previousPixels);
                pixelHoles+=frameHoles;
                if(frameHoles>0&&saved<4){saved++;Save(name+"PixelHole"+frames,image);}
                oscillations+=CountOscillations(previousShown);
                VirtualObjectNode nearest=Nearest();
                int shown=-1;
                if(nearest!=null){shown=nearest.ShownBand;}
                csv.Append(name).Append(',').Append(frames).Append(',').Append(descent.Altitude.ToString("G5",CultureInfo.InvariantCulture)).Append(',')
                    .Append(status.Speed.ToString("G4",CultureInfo.InvariantCulture)).Append(',').Append(Format(status.SafeSpeed)).Append(',').Append(status.Uncovered).Append(',')
                    .Append(status.Lagging).Append(',').Append(status.Nodes).Append(',').Append(status.Preparing).Append(',').Append(status.Ready).Append(',').Append(status.Active).Append(',')
                    .Append(status.Activations).Append(',').Append((status.GpuBytes/1048576.0).ToString("0.0",CultureInfo.InvariantCulture)).Append(',').Append(shown).Append(',').Append(frameHoles).Append('\n');
                if(frames%200==0&&saved<12){Save(name+"Descent"+frames,image);}
                UnityEngine.Object.DestroyImmediate(image);
                frames++;
                yield return null;
            }
            // 地上で、子の帯まで揃うのを待つ。
            int dwell=0;
            while(dwell<300){
                descent.SpeedLimit=float.PositiveInfinity;
                LookAt(0);
                system.Step(Step);
                Texture2D image=Capture();
                VirtualObjectStatus status=system.Status;
                if(status.Uncovered>0){uncoveredFrames++;}
                int frameHoles=PixelHoles(image,previousPixels);
                pixelHoles+=frameHoles;
                peakPreparing=Mathf.Max(peakPreparing,status.Preparing);peakActivations=Mathf.Max(peakActivations,status.Activations);peakGpu=Math.Max(peakGpu,status.GpuBytes);
                UnityEngine.Object.DestroyImmediate(image);
                dwell++;
                if(status.Preparing==0&&status.Lagging==0&&dwell>30){break;}
                yield return null;
            }
            Texture2D last=Capture(640,480);
            Save(name+"Ground",last);
            UnityEngine.Object.DestroyImmediate(last);
            float lagRatio=0f;
            if(evaluatedNodeFrames>0){lagRatio=(float)laggingNodeFrames/evaluatedNodeFrames;}
            report.Add(name+": frames="+frames+" altitude="+descent.Altitude.ToString("G4",CultureInfo.InvariantCulture)+" uncoveredFrames="+uncoveredFrames+" pixelHoles="+pixelHoles+
                " oscillations="+oscillations+" lagRatio="+lagRatio.ToString("0.0000",CultureInfo.InvariantCulture)+" laggingNodeFrames="+laggingNodeFrames+
                " peakPreparing="+peakPreparing+"/"+budget.MaxConcurrentPrepares+" peakActivations="+peakActivations+"/"+budget.MaxActivationsPerFrame+
                " peakGpuMB="+(peakGpu/1048576.0).ToString("0.0",CultureInfo.InvariantCulture)+"/"+(budget.GpuBytes/1048576.0).ToString("0",CultureInfo.InvariantCulture)+
                " minimumSafeSpeed="+Format(minimumSafe)+" observedBuildingPixels="+observedPixels+" failures="+system.Status.Failures+" errors="+string.Join(" | ",system.Errors));
            if(observedPixels==0){failures.Add(name+": 画素の判定で建物の画素が 1 つも数えられませんでした(画素の穴の判定が空)。");}
            if(frames>=DescentLimit){failures.Add(name+": "+DescentLimit+" frame で地上まで降りられませんでした(高さ "+descent.Altitude+" m)。");}
            if(uncoveredFrames!=0){failures.Add(name+": 望む帯が覆われていない frame が "+uncoveredFrames+" ありました(穴)。");}
            if(pixelHoles!=0){failures.Add(name+": 写っていた建物が次の frame で消えた(画素の穴)ことが "+pixelHoles+" 回ありました。");}
            if(oscillations!=0){failures.Add(name+": 近づく間に、見せている帯が粗い方へ戻ったことが "+oscillations+" 回ありました(段の往復)。");}
            if(peakPreparing>budget.MaxConcurrentPrepares){failures.Add(name+": 同時の準備数が上限を超えました。");}
            if(peakActivations>budget.MaxActivationsPerFrame){failures.Add(name+": 1 frame の表示数が上限を超えました。");}
            if(peakGpu>budget.GpuBytes){failures.Add(name+": GPU の memory の見積もりが上限を超えました。");}
            if(system.Status.Failures!=0){failures.Add(name+": 表現の失敗が "+system.Status.Failures+" ありました: "+string.Join(" | ",system.Errors));}
            if(state.Multiplier<=1f&&lagRatio>0.01f){failures.Add(name+": 安全速度以下なのに、遅れた実体の frame の割合が "+lagRatio.ToString("0.000",CultureInfo.InvariantCulture)+" ありました(1% 以下のはず)。");}
            if(state.Multiplier>=4f&&laggingNodeFrames==0){failures.Add(name+": 安全速度の 4 倍でも遅れが出ませんでした(安全速度の推定か、遅延の注入が効いていない)。");}
            CheckChildren(name);
            if(state.Multiplier==Multipliers[0]){
                foreach(object step in NegativeControl()){yield return step;}
            }
        }

        // 負の対照: 高度 150 m から場所 A を見て、VG メッシュの帯で写っている建物の GameObject を全部隠し(表現の切り替えに見せかけて)、画素の穴の判定が数えること
        // (VG の部品は無効にした frame から描かれない。一括描画の Renderer は無効の通知が要るので使わない)。
        private static IEnumerable<object> NegativeControl(){
            if(!descent.Teleport(Direction(0,LandingEast,0f),150f)){throw new InvalidOperationException("Teleport: "+descent.LastFailure);}
            LookAt(0);
            system.NotifyTeleport();
            var counts=new Dictionary<VirtualObjectNode,int>();
            for(int frame=0;frame<300;frame++){
                system.Step(Step);
                Texture2D image=Capture();
                PixelHoles(image,counts);
                UnityEngine.Object.DestroyImmediate(image);
                if(frame>30&&system.Status.Preparing==0&&system.Status.Lagging==0){break;}
                yield return null;
            }
            var hidden=new List<GameObject>();
            int before=0;
            foreach(VirtualObjectNode root in system.Roots){
                if(root.ShownBand!=2||!(root.RepresentationAt(2) is GameObjectRepresentation representation)||representation.Instance==null){continue;}
                hidden.Add(representation.Instance);
                counts.TryGetValue(root,out int count);
                before+=Mathf.Max(0,count);
                // 次の frame を、切り替えの直後として扱う。
                switches[root]=(Signature(root),pixelFrame+1);
            }
            if(hidden.Count==0){failures.Add("負の対照: VG メッシュの帯で写っている建物がありませんでした。");yield break;}
            foreach(GameObject instance in hidden){instance.SetActive(false);}
            yield return null;
            Texture2D after=Capture();
            int holes=PixelHoles(after,counts);
            Save("NegativeControlHidden",after);
            UnityEngine.Object.DestroyImmediate(after);
            foreach(GameObject instance in hidden){instance.SetActive(true);}
            report.Add("negative control: hid "+hidden.Count+" VG buildings (pixels in their circles before="+before+") pixelHoles="+holes);
            if(holes<1){failures.Add("負の対照: 写っていた建物を隠しても、画素の穴の判定が数えませんでした。");}
            yield return null;
        }

        // 近い建物が子の帯で見せていて、番号ごとの型が割り当てのとおりか。
        private static void CheckChildren(string name){
            VirtualObjectNode nearest=Nearest();
            if(nearest==null){failures.Add(name+": 建物が見つかりません。");return;}
            report.Add(name+": nearest="+nearest.Path+" distance="+nearest.Distance.ToString("0.0",CultureInfo.InvariantCulture)+" pixels="+nearest.Pixels.ToString("0",CultureInfo.InvariantCulture)+
                " desired="+nearest.DesiredBand+" shown="+nearest.ShownBand);
            if(nearest.ShownBand!=4){failures.Add(name+": 地上で最も近い建物が子の帯で見せていません(見せている帯 "+nearest.ShownBand+"、望む帯 "+nearest.DesiredBand+")。");return;}
            IReadOnlyList<VirtualObjectNode> children=nearest.Children;
            int parts=((MeshClusterPartition)kind.Definition.Partition).GetPartCount(nearest);
            var templates=new HashSet<VirtualObject>();
            int wrong=0;
            for(int i=0;i<children.Count;i++){
                VirtualObjectNode child=children[i];
                templates.Add(child.Definition);
                VirtualObject expected=kind.FacadeTemplate;
                if(child.Number==kind.OverriddenNumber){expected=kind.GlassTemplate;}
                if(child.Definition!=expected){wrong++;}
            }
            report.Add(name+": children="+children.Count+" parts="+parts+" distinctTemplates="+templates.Count+" wrongTemplate="+wrong);
            if(children.Count!=parts){failures.Add(name+": 子の数("+children.Count+")が部分の数("+parts+")と違います。");}
            if(templates.Count!=2){failures.Add(name+": 子の型の数が 2 ではありません("+templates.Count+")。");}
            if(wrong!=0){failures.Add(name+": 割り当てと違う型の子が "+wrong+" ありました。");}
        }

        // A の地上から B の地上へ瞬間移動する。移動先を先に準備して表示まで済ませ、移った直後の frame から穴が無いこと。
        private static IEnumerable<object> Teleport(){
            Vector3 direction=Direction(1,LandingEast,0f);
            if(!LogicalVector3.TryFromFloat3((float3)(direction*PlanetRadius),out LogicalVector3 probe)||!planet.TrySampleSurface(in probe,out float height,out float _)){throw new InvalidOperationException("teleport probe");}
            if(!LogicalVector3.TryFromFloat3((float3)(direction*(PlanetRadius+height+Eye)),out LogicalVector3 destination)){throw new InvalidOperationException("teleport destination");}
            Vector3 target=Target(1);
            Quaternion rotation=Quaternion.LookRotation(target-direction*(PlanetRadius+height+Eye),direction);
            int waited=0;bool ready=false;
            while(waited<900){
                bool terrain=descent.PrepareTeleport(direction,Eye);
                bool objects=system.PrepareTeleport(in destination,rotation,StandardObjectPlacement.ProjectionScale(camera),600f);
                LookAt(0);
                system.Step(Step);
                Texture2D image=Capture();UnityEngine.Object.DestroyImmediate(image);
                waited++;
                if(terrain&&objects&&system.PrepareTeleport(in destination,rotation,StandardObjectPlacement.ProjectionScale(camera),600f)){ready=true;break;}
                yield return null;
            }
            if(!ready){failures.Add("瞬間移動: 900 frame で移動先の準備が終わりませんでした。");system.CancelTeleport();yield break;}
            if(!descent.Teleport(direction,Eye)){throw new InvalidOperationException("Teleport: "+descent.LastFailure);}
            LookAt(1);
            system.NotifyTeleport();
            int uncovered=0;int pixelHoles=0;
            var previousPixels=new Dictionary<VirtualObjectNode,int>();
            var perFrame=new List<string>();
            for(int frame=0;frame<60;frame++){
                system.Step(Step);
                Texture2D image=Capture();
                if(system.Status.Uncovered>0){uncovered++;perFrame.Add(frame+":"+system.Status.Uncovered);}
                pixelHoles+=PixelHoles(image,previousPixels);
                if(frame==0){Save("TeleportFirstFrame",image);}
                UnityEngine.Object.DestroyImmediate(image);
                yield return null;
            }
            report.Add("teleport: waited="+waited+" uncoveredFrames="+uncovered+" pixelHoles="+pixelHoles+" perFrame="+string.Join(" ",perFrame));
            if(uncovered!=0){failures.Add("瞬間移動: 移った後に、望む帯が覆われていない frame が "+uncovered+" ありました。");}
            if(pixelHoles!=0){failures.Add("瞬間移動: 移った後に、写っていた建物が消えたことが "+pixelHoles+" 回ありました。");}
        }

        // 見た目の確認用(判定なし): 通常の材質の建物で、地上から撮る。
        private static IEnumerable<object> Appearance(){
            system.Shutdown();
            system.Sets=new[]{appearanceSet};
            system.Budget.InjectedPrepareDelaySeconds=0f;
            system.Initialize();
            report.Add("brg(appearance): "+global::Omni.Editor.BatchRendering.RadialSceneBrgEditorIntegration.PrepareLoadedSceneSidecarsForAutomation());
            Vector3 direction=Direction(0,-150f,-40f);
            if(!descent.Teleport(direction,Eye)){throw new InvalidOperationException("Teleport: "+descent.LastFailure);}
            LookAt(0);
            system.NotifyTeleport();
            for(int frame=0;frame<240;frame++){
                system.Step(Step);
                Texture2D image=Capture();UnityEngine.Object.DestroyImmediate(image);
                if(frame>30&&system.Status.Preparing==0&&system.Status.Lagging==0){break;}
                yield return null;
            }
            Texture2D ground=Capture(640,480);Save("AppearanceGround",ground);UnityEngine.Object.DestroyImmediate(ground);
            if(!descent.Teleport(direction,400f)){throw new InvalidOperationException("Teleport: "+descent.LastFailure);}
            LookAt(0);
            system.NotifyTeleport();
            for(int frame=0;frame<240;frame++){
                system.Step(Step);
                Texture2D image=Capture();UnityEngine.Object.DestroyImmediate(image);
                if(frame>30&&system.Status.Preparing==0&&system.Status.Lagging==0){break;}
                yield return null;
            }
            Texture2D air=Capture(640,480);Save("AppearanceAir",air);UnityEngine.Object.DestroyImmediate(air);
            report.Add("appearance: registeredRenderers="+placement.RegisteredRendererCount);
        }

        // 地面に置いた後の、全部の実体の位置(Logical)と向きの hash。
        private static string PlacementHash(){
            var text=new StringBuilder();
            foreach(VirtualObjectNode root in system.Roots){
                text.Append(root.InstanceId).Append('|').Append(root.Origin.X.ToString()).Append('|').Append(root.Origin.Y.ToString()).Append('|').Append(root.Origin.Z.ToString()).Append('|')
                    .Append(root.EffectiveRotation.ToString("R")).Append('\n');
            }
            using(var sha=System.Security.Cryptography.SHA256.Create()){
                byte[] hash=sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
                return BitConverter.ToString(hash,0,8).Replace("-","");
            }
        }

        // 近づく間に、見せている帯が粗い方へ戻った実体の数(ルートだけ)。
        private static int CountOscillations(Dictionary<VirtualObjectNode,int> previous){
            int count=0;
            foreach(VirtualObjectNode root in system.Roots){
                int shown=root.ShownBand;
                if(previous.TryGetValue(root,out int before)&&shown>=0&&before>=0&&shown<before){count++;}
                previous[root]=shown;
            }
            return count;
        }

        // 実体ごとの、見せている表現の組(実体と子の見せている帯)と、それが最後に変わった frame。
        private static readonly Dictionary<VirtualObjectNode,(int signature,int changed)> switches=new Dictionary<VirtualObjectNode,(int signature,int changed)>();
        private static int pixelFrame;

        // 見せている表現の組の目印(実体の帯と、子の帯の和)。
        private static int Signature(VirtualObjectNode root){
            int value=root.ShownBand*100000;
            IReadOnlyList<VirtualObjectNode> children=root.Children;
            for(int i=0;i<children.Count;i++){value+=(children[i].ShownBand+1)*(i+1);}
            return value;
        }

        // 実体ごとに、境界の球の画面の円の中の赤の画素を数え、前の frame より半分未満に減り、かつ 20 画素以上減ったら穴と数える
        // (円には手前や奥の別の建物の赤も入るので、0 になるのを待たず減り方で見る)。
        // 数えるのは、見せている表現の組が変わった frame から 3 frame の間だけ(それ以外の急な減り方は、地形や手前の建物に隠れたもの)。
        private static int PixelHoles(Texture2D image,Dictionary<VirtualObjectNode,int> previous){
            pixelFrame++;
            foreach(VirtualObjectNode root in system.Roots){
                int signature=Signature(root);
                if(!switches.TryGetValue(root,out (int signature,int changed) last)||last.signature!=signature){switches[root]=(signature,pixelFrame);}
            }
            Color32[] pixels=image.GetPixels32();
            int width=image.width;int height=image.height;
            if(!LogicalPosition3.TryDifference(new LogicalPosition3(cameraLogical.LocalPosition),new LogicalPosition3(planetLogical.LocalPosition),out LogicalVector3 cameraLocal)){return 0;}
            Quaternion inverse=Quaternion.Inverse(camera.transform.rotation);
            float tangent=Mathf.Tan(camera.fieldOfView*0.5f*Mathf.Deg2Rad);
            float aspect=(float)width/height;
            float projection=height/(2f*tangent);
            int holes=0;
            foreach(VirtualObjectNode root in system.Roots){
                VirtualObject definition=root.Definition;
                Vector3 centerOffset=root.EffectiveRotation*definition.BoundsCenter;
                if(!LogicalVector3.TrySubtract(root.Origin,cameraLocal,out LogicalVector3 difference)||!difference.TryToFloat3(out float3 offset)){continue;}
                Vector3 view=inverse*((Vector3)offset+centerOffset);
                int count=0;
                if(view.z>root.Radius){
                    float sx=(view.x/(view.z*tangent*aspect)*0.5f+0.5f)*width;float sy=(view.y/(view.z*tangent)*0.5f+0.5f)*height;
                    float r=root.Radius*projection/view.z+2f;
                    int x0=Mathf.Max(0,Mathf.FloorToInt(sx-r));int x1=Mathf.Min(width-1,Mathf.CeilToInt(sx+r));
                    int y0=Mathf.Max(0,Mathf.FloorToInt(sy-r));int y1=Mathf.Min(height-1,Mathf.CeilToInt(sy+r));
                    for(int y=y0;y<=y1;y++){
                        for(int x=x0;x<=x1;x++){
                            float dx=x+0.5f-sx;float dy=y+0.5f-sy;
                            if(dx*dx+dy*dy>r*r){continue;}
                            if(VirtualObjectSampleAssets.IsBuildingPixel(pixels[y*width+x])){count++;}
                        }
                    }
                }else{
                    count=-1;
                }
                bool switching=switches.TryGetValue(root,out (int signature,int changed) state)&&pixelFrame-state.changed<=3;
                if(switching&&count>=0&&previous.TryGetValue(root,out int before)&&before-count>=20&&count*2<before){holes++;}
                if(count>0){observedPixels+=count;}
                previous[root]=count;
            }
            return holes;
        }

        private static VirtualObjectNode Nearest(){
            VirtualObjectNode best=null;
            foreach(VirtualObjectNode root in system.Roots){
                if(best==null||root.Distance<best.Distance){best=root;}
            }
            return best;
        }

        // 場所 site の建物の中心(地面から 30 m)。
        private static Vector3 Target(int site){
            return Direction(site,0f,0f)*(PlanetRadius+siteHeights[site]+30f);
        }

        // camera を、場所 site の建物の中心(地面から 30 m)へ向ける(上は惑星の外向き)。
        private static void LookAt(int site){
            if(!LogicalPosition3.TryDifference(new LogicalPosition3(cameraLogical.LocalPosition),new LogicalPosition3(planetLogical.LocalPosition),out LogicalVector3 local)||
                !local.TryToFloat3(out float3 position)){
                return;
            }
            Vector3 eye=(Vector3)position;
            Vector3 target=Target(site);
            Vector3 vertical=eye.normalized;
            Vector3 forward=target-eye;
            if(Vector3.Cross(forward.normalized,vertical).sqrMagnitude<1e-8f){vertical=north;}
            Quaternion rotation=Quaternion.LookRotation(forward,vertical);
            if(!LogicalQuaternion.TryFromFloat4(rotation.x,rotation.y,rotation.z,rotation.w,out LogicalQuaternion value)){return;}
            cameraLogical.LocalRotation=value;
            cameraLogical.SyncUnityTransform();
        }

        private static Texture2D Capture(int width=Width,int height=ImageHeight){
            return M0Validation.Capture(camera,width,height);
        }

        private static void Save(string name,Texture2D image){
            File.WriteAllBytes(Results+"/"+name+".png",image.EncodeToPNG());
        }

        private static string Format(float value){
            if(float.IsPositiveInfinity(value)){return "inf";}
            return value.ToString("G4",CultureInfo.InvariantCulture);
        }

        private static string Describe(RepresentationBand[] bands){
            var parts=new List<string>();
            foreach(RepresentationBand band in bands){parts.Add(band.Representation+"@"+band.EnterPixels.ToString("0.#",CultureInfo.InvariantCulture));}
            return string.Join(",",parts);
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Record;
            if(error!=null){failures.Add(error.ToString());}
            foreach(string message in errors){failures.Add("Unity logged error: "+message);}
            try{
                if(system!=null){system.Shutdown();}
            }catch(Exception shutdown){
                failures.Add("Shutdown: "+shutdown.Message);
            }
            report.Add("UTC="+DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+" Unity="+Application.unityVersion+" GPU="+SystemInfo.graphicsDeviceName+" API="+SystemInfo.graphicsDeviceType);
            if(failures.Count==0){report.Add("PASSED");}else{report.Add("FAILED");report.AddRange(failures);}
            Directory.CreateDirectory(Results);
            File.WriteAllLines(Results+"/Report.txt",report);
            File.WriteAllText(Results+"/Frames.csv",csv.ToString());
            int code=1;
            if(failures.Count==0){code=0;}
            EditorApplication.Exit(code);
        }
    }
}
