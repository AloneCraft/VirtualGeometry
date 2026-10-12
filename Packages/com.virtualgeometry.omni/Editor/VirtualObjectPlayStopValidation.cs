using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Omni.BatchRendering;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using VirtualGeometry.VirtualObjects;
using VirtualGeometry.World;
using VirtualizedGeometry.Editor;
using LogicalQuaternion=Omni.Logical.Quaternion;
using LogicalTransform=Omni.Logical.Transform;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// 仮想オブジェクトの Play/Stop の漏れの検証(batchmode)。半径 600 km の惑星の地表の建物 3×3 棟の近く(地上 1.7 m)に camera を置いた scene を保存し、
    /// Play Mode に入って毎 frame 描き(仮想オブジェクトは LateUpdate で進む)、止める。これを domain reload と scene reload を止めた条件 A と、
    /// domain reload だけ止めた条件 B で 5 回ずつ繰り返す。
    /// <list type="bullet">
    /// <item>Play 中: 表現が Omni の一括描画へ登録され(<see cref="RadialBatchRendererRegistry.RegisteredRendererCount"/> が 0 より大きい)、建物が子の帯まで出る(空の検証でないこと)。</item>
    /// <item>止めた後: 一括描画の登録が Play の前の数に戻り、破棄待ち(<see cref="VirtualObjectRetirement.PendingCount"/>)が 0 で、表現の GameObject が残っていない。</item>
    /// </list>
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.VirtualObjectPlayStopValidation.Run(結果は TestResults/Planet/VirtualObjectPlayStop)
    /// </summary>
    public static class VirtualObjectPlayStopValidation{
        private const string Results="TestResults/Planet/VirtualObjectPlayStop";
        private const string Root="Assets/Development/PlanetWorld/VirtualObjectPlay";
        private const string ScenePath=Root+"/VirtualObjectPlay.unity";
        private const string SetPath=Root+"/Buildings.asset";
        private const string ShaderPath="Packages/com.virtualgeometry.omni/Shaders/RadialGlobalLit.vgshader";
        private const float PlanetRadius=600000f;
        private const int Cycles=5;
        private const int PlayFrames=180;
        private const int SettleFrames=45;
        // 表現が作る GameObject の名前の始まり(残っていたら漏れ)。
        private static readonly string[] RepresentationNames={"Virtual Object Pool","Billboard ","Part ","Building VG","Building Detail"};

        private enum Stage{StartCycle,WaitPlaying,Playing,WaitEdit,Settle}

        private static readonly EnterPlayModeOptions[] Conditions={
            EnterPlayModeOptions.DisableDomainReload|EnterPlayModeOptions.DisableSceneReload,
            EnterPlayModeOptions.DisableDomainReload,
        };
        private static readonly string[] ConditionNames={"A (DisableDomainReload|DisableSceneReload)","B (DisableDomainReload)"};

        private static Stage stage;
        private static int stageFrames;
        private static int condition;
        private static int cycle;
        private static int baseline;
        private static int peakRegistered;
        private static int deepestBand;
        private static double started;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();

        public static void Run(){
            report.Clear();failures.Clear();errors.Clear();
            started=EditorApplication.timeSinceStartup;condition=0;cycle=0;stage=Stage.StartCycle;stageFrames=0;
            Directory.CreateDirectory(Results);
            Application.logMessageReceived+=Record;
            try{
                BuildScene();
                EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single);
                report.Add("brg: "+global::Omni.Editor.BatchRendering.RadialSceneBrgEditorIntegration.PrepareLoadedSceneSidecarsForAutomation());
                EditorSettings.enterPlayModeOptionsEnabled=true;
                baseline=RadialBatchRendererRegistry.RegisteredRendererCount;
                report.Add("scene="+ScenePath+" cycles="+Cycles+" playFrames="+PlayFrames+" baselineRegistered="+baseline);
                EditorApplication.update+=Tick;
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void Record(string message,string stack,LogType type){
            if(type==LogType.Error||type==LogType.Exception||type==LogType.Assert){
                // Omni が Unity の版の違いで出す診断は、この検証の対象外(Requirements/PlanetWorld.md §8.4)。
                if(message.Contains("[OmniConversion] ValidatedOnly")||message.Contains("Localization/ja.po will not be compiled")){return;}
                errors.Add(message);
            }
        }

        // 惑星・建物 3×3 棟・camera(地上 1.7 m、建物を向く)の scene を作って保存する。
        private static void BuildScene(){
            Directory.CreateDirectory(Root);
            MoonBallSceneBuilder.UsePipeline();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            VirtualObjectSampleAssets.BuildingKind kind=VirtualObjectSampleAssets.EnsureBuildingKind(false);
            Shader shader=AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if(shader==null){throw new InvalidOperationException("Shader not found: "+ShaderPath);}
            string groundPath=Root+"/Ground.mat";
            Material ground=AssetDatabase.LoadAssetAtPath<Material>(groundPath);
            if(ground==null){ground=new Material(shader);AssetDatabase.CreateAsset(ground,groundPath);}
            ground.SetColor("_BaseColor",new Color(0.35f,0.55f,0.3f,1f));
            EditorUtility.SetDirty(ground);

            GameObject holder=new GameObject("Planet");holder.SetActive(false);
            holder.AddComponent<LogicalTransform>();
            PlanetTerrainSource planet=holder.AddComponent<PlanetTerrainSource>();
            holder.AddComponent<OmniPlanetPlacement>();
            planet.Material=ground;
            planet.Configure(PlanetRadius,300f,PlanetTerrainSource.GpuMaximumLevel,2f);
            holder.SetActive(true);

            Vector3 up=new Vector3(0.3f,1f,0.2f).normalized;
            Vector3 east=Vector3.Cross(up,Vector3.forward).normalized;
            Vector3 north=Vector3.Cross(east,up).normalized;
            VirtualObjectSet set=AssetDatabase.LoadAssetAtPath<VirtualObjectSet>(SetPath);
            if(set==null){set=ScriptableObject.CreateInstance<VirtualObjectSet>();AssetDatabase.CreateAsset(set,SetPath);}
            var records=new List<VirtualObjectPlacementRecord>();
            for(int i=0;i<9;i++){
                double3 position=(double3)(float3)up*PlanetRadius+(double3)(float3)east*((i%3-1)*70.0)+(double3)(float3)north*((i/3-1)*70.0);
                records.Add(new VirtualObjectPlacementRecord{
                    Id="b"+i,Prefab=kind.Definition,
                    X=position.x.ToString("R",CultureInfo.InvariantCulture),Y=position.y.ToString("R",CultureInfo.InvariantCulture),Z=position.z.ToString("R",CultureInfo.InvariantCulture),
                    Rotation=Quaternion.identity,Scale=1f,Seed=(uint)i,
                });
            }
            set.Placements=records.ToArray();
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();

            GameObject objects=new GameObject("Virtual Objects");objects.SetActive(false);
            objects.transform.SetParent(holder.transform,false);
            objects.AddComponent<LogicalTransform>();
            VirtualObjectSystem system=objects.AddComponent<VirtualObjectSystem>();
            objects.AddComponent<OmniObjectPlacement>();
            system.Sets=new[]{set};system.GroundTerrain=planet;
            objects.SetActive(true);

            // camera: 西の列の建物の西の面から 6 m(地上ではその建物の境界の球の中で、子の帯まで出る)、地上 1.7 m。
            Vector3 direction=(up*PlanetRadius+east*-88f).normalized;
            if(!LogicalVector3.TryFromFloat3((float3)(direction*PlanetRadius),out LogicalVector3 probe)||!planet.TrySampleSurface(in probe,out float height,out float _)){throw new InvalidOperationException("surface");}
            Vector3 eye=direction*(PlanetRadius+height+1.7f);
            Vector3 target=up*(PlanetRadius+height+30f);
            GameObject cameraObject=new GameObject("Main Camera");cameraObject.tag="MainCamera";
            Camera camera=cameraObject.AddComponent<Camera>();
            camera.nearClipPlane=0.1f;camera.farClipPlane=200000f;camera.fieldOfView=60f;camera.clearFlags=CameraClearFlags.SolidColor;
            camera.backgroundColor=new Color(0.45f,0.6f,0.85f,1f);camera.allowHDR=false;camera.allowMSAA=false;
            LogicalTransform cameraLogical=cameraObject.AddComponent<LogicalTransform>();
            cameraObject.AddComponent<global::Omni.RadialCamera>().SetIdentityViewScale();
            cameraObject.AddComponent<VirtualObjectViewer>();
            Quaternion rotation=Quaternion.LookRotation(target-eye,direction);
            if(!LogicalVector3.TryFromFloat3((float3)eye,out LogicalVector3 cameraPosition)||
                !LogicalQuaternion.TryFromFloat4(rotation.x,rotation.y,rotation.z,rotation.w,out LogicalQuaternion logicalRotation)){
                throw new InvalidOperationException("camera");
            }
            cameraLogical.LocalPosition=cameraPosition;cameraLogical.LocalRotation=logicalRotation;cameraLogical.SyncUnityTransform();
            Light sun=new GameObject("Sun").AddComponent<Light>();
            sun.type=LightType.Directional;sun.shadows=LightShadows.None;sun.transform.rotation=Quaternion.LookRotation(east*0.6f-up*0.8f,up);
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(0.25f,0.25f,0.28f);
            if(!EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(),ScenePath)){throw new IOException("Could not save scene: "+ScenePath);}
        }

        private static void Tick(){
            try{
                if(EditorApplication.timeSinceStartup-started>3000){throw new TimeoutException("VirtualObjectPlayStop validation timeout.");}
                stageFrames++;
                switch(stage){
                    case Stage.StartCycle:
                        if(EditorApplication.isPlaying||EditorApplication.isCompiling||EditorApplication.isUpdating){return;}
                        EditorSettings.enterPlayModeOptions=Conditions[condition];
                        EditorApplication.EnterPlaymode();
                        stage=Stage.WaitPlaying;stageFrames=0;peakRegistered=0;deepestBand=-1;
                        break;
                    case Stage.WaitPlaying:
                        if(EditorApplication.isPlaying&&!EditorApplication.isPaused){stage=Stage.Playing;stageFrames=0;}
                        else if(stageFrames>1800){throw new TimeoutException("Play Mode に入れません。");}
                        break;
                    case Stage.Playing:
                        {
                            // batchmode には Game View が無く、camera は自動では描かれないので、毎 frame 描く。
                            Camera camera=Camera.main;
                            if(camera==null){throw new InvalidOperationException("Main Camera がありません。");}
                            UnityEngine.Object.DestroyImmediate(M0Validation.Capture(camera,320,240));
                            peakRegistered=Math.Max(peakRegistered,RadialBatchRendererRegistry.RegisteredRendererCount);
                            VirtualObjectSystem system=UnityEngine.Object.FindAnyObjectByType<VirtualObjectSystem>();
                            if(system!=null){
                                foreach(VirtualObjectNode root in system.Roots){deepestBand=Math.Max(deepestBand,root.ShownBand);}
                            }
                            if(stageFrames>=PlayFrames){
                                EditorApplication.ExitPlaymode();
                                stage=Stage.WaitEdit;stageFrames=0;
                            }
                        }
                        break;
                    case Stage.WaitEdit:
                        if(!EditorApplication.isPlaying&&!EditorApplication.isPlayingOrWillChangePlaymode){stage=Stage.Settle;stageFrames=0;}
                        else if(stageFrames>1800){throw new TimeoutException("Play Mode を終了できません。");}
                        break;
                    case Stage.Settle:
                        if(stageFrames<SettleFrames){return;}
                        Check();
                        cycle++;
                        if(cycle>=Cycles){
                            condition++;cycle=0;
                            if(condition>=Conditions.Length){Finish(null);return;}
                        }
                        stage=Stage.StartCycle;stageFrames=0;
                        break;
                }
            }catch(Exception error){
                Finish(error);
            }
        }

        // 止めた後の漏れを数える。
        private static void Check(){
            int registered=RadialBatchRendererRegistry.RegisteredRendererCount;
            int pending=VirtualObjectRetirement.PendingCount;
            var leftovers=new List<string>();
            foreach(GameObject value in Resources.FindObjectsOfTypeAll<GameObject>()){
                if(value==null||EditorUtility.IsPersistent(value)){continue;}
                foreach(string prefix in RepresentationNames){
                    if(value.name.StartsWith(prefix,StringComparison.Ordinal)){leftovers.Add(value.name);break;}
                }
            }
            string label="condition "+ConditionNames[condition]+" cycle "+(cycle+1);
            string names="";
            if(leftovers.Count>0){names=" ("+string.Join(", ",leftovers.GetRange(0,Math.Min(8,leftovers.Count)))+")";}
            report.Add(label+": peakRegistered="+peakRegistered+" deepestShownBand="+deepestBand+" afterStop registered="+registered+" retirementPending="+pending+" leftovers="+leftovers.Count+names);
            if(peakRegistered<=baseline){failures.Add(label+": Play 中に表現が一括描画へ登録されませんでした(空の検証)。");}
            if(deepestBand<4){failures.Add(label+": Play 中に建物が子の帯まで出ませんでした(見せた帯の最大 "+deepestBand+")。");}
            if(registered!=baseline){failures.Add(label+": 止めた後に一括描画の登録が "+registered+" 残りました(Play の前は "+baseline+")。");}
            if(pending!=0){failures.Add(label+": 止めた後に破棄待ちが "+pending+" 残りました。");}
            if(leftovers.Count!=0){failures.Add(label+": 止めた後に表現の GameObject が "+leftovers.Count+" 残りました。");}
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Record;
            EditorSettings.enterPlayModeOptionsEnabled=false;
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
