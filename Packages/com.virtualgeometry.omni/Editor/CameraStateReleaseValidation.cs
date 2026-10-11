using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;
using VirtualGeometry.World;
using VirtualizedGeometry.Editor;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// camera を破棄したとき、その camera のために作った GPU buffer(cluster の cull の候補・描画記録・可視 index・引数。月の source で 1 camera あたり約 400 MB)が解放されることの検証。
    /// 一時的な camera(Scene View、撮影用、閉じた scene の camera)は破棄されるが、解放しないと、source が無効になるまで GPU の memory に残り続ける。
    /// 手順: 月とボールを描く主 camera で収束 → 一時 camera を複製して描く(camera ごとの状態ができる)→ 破棄して数 frame 進める。
    /// GPU の memory(<see cref="Profiler.GetAllocatedMemoryForGraphicsDriver"/>)が、一時 camera を描いたときに増え、破棄後に増えた分の大半が戻ることを確かめる。
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.CameraStateReleaseValidation.Run(結果は TestResults/Planet/CameraStateRelease)
    /// </summary>
    public static class CameraStateReleaseValidation{
        private const int TemporaryCameras=2;
        private const int ConvergeLimit=900;
        // 一時 camera を描いて増えるはずの下限(月の source の 1 camera あたり約 400 MB の半分)。これより増えないなら、測れていない。
        private const long MinimumGrowthPerCamera=150L*1024*1024;
        // 破棄後に戻るべき割合。
        private const double RequiredReleaseRatio=0.8;

        private static string results;
        private static MoonBallObjects objects;
        private static Camera camera;
        private static PlanetTerrainSource moon;
        private static PlanetTerrainSource ball;
        private static MoonBallDemo demo;
        private static IEnumerator steps;
        private static double started;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();

        public static void Run(){
            results="TestResults/Planet/CameraStateRelease";
            report.Clear();failures.Clear();errors.Clear();started=EditorApplication.timeSinceStartup;
            Directory.CreateDirectory(results);
            Application.logMessageReceived+=Record;
            try{
                MoonBallSceneBuilder.UsePipeline();
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                objects=MoonBallSceneBuilder.Populate(MoonBallSettings.Moon);
                HeightShiftOverride.ApplyToScene();
                camera=objects.Camera;
                moon=objects.MoonSource;ball=objects.BallSource;demo=objects.Demo;
                demo.enabled=false;
                steps=Steps();
                EditorApplication.update+=Tick;
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void Record(string condition,string stack,LogType type){
            if(type==LogType.Error||type==LogType.Exception||type==LogType.Assert){
                if(condition.Contains("[OmniConversion] ValidatedOnly")||condition.Contains("Localization/ja.po will not be compiled")){return;}
                errors.Add(condition);
            }
        }

        private static void Tick(){
            try{
                if(EditorApplication.timeSinceStartup-started>1500){throw new TimeoutException("CameraStateRelease validation timeout.");}
                EditorApplication.QueuePlayerLoopUpdate();
                if(!steps.MoveNext()){Finish(null);}
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void Render(Camera target){
            UnityEngine.Object.DestroyImmediate(M0Validation.Capture(target,160,120));
        }

        private static IEnumerable<object> Frames(int count){
            for(int i=0;i<count;i++){
                Render(camera);
                yield return null;
            }
        }

        private static IEnumerable<object> Converge(string name){
            int stable=0;int previous=-1;int frames=0;
            while(frames<ConvergeLimit){
                Render(camera);
                int signature=moon.LastSelected.Count*31+ball.LastSelected.Count;
                foreach(PlanetCellKey key in moon.LastSelected){signature^=key.GetHashCode();}
                if(moon.PendingGeneration==0&&ball.PendingGeneration==0&&signature==previous){stable++;}else{stable=0;}
                previous=signature;
                frames++;
                if(stable>=2){break;}
                yield return null;
            }
            if(frames>=ConvergeLimit){failures.Add(name+": "+ConvergeLimit+" frame で選択が収束しませんでした。");}
        }

        private static long Memory(){
            return Profiler.GetAllocatedMemoryForGraphicsDriver();
        }

        private static string Mebibytes(long bytes){
            return (bytes/(1024.0*1024.0)).ToString("F0",CultureInfo.InvariantCulture)+" MiB";
        }

        private static IEnumerator Steps(){
            int waited=0;
            while(moon.Selector==null||ball.Selector==null){
                if(waited++>100){throw new InvalidOperationException("月またはボールが有効になりません。");}
                yield return null;
            }
            if(!demo.Initialize()){throw new InvalidOperationException("Initialize: "+demo.Failure);}
            demo.ShowContactView();
            foreach(object step in Converge("Main")){yield return step;}
            foreach(object step in Frames(10)){yield return step;}
            long baseline=Memory();
            report.Add("environment: GPU="+SystemInfo.graphicsDeviceName+" API="+SystemInfo.graphicsDeviceType+" graphicsDriverMemory(baseline)="+Mebibytes(baseline));

            // 一時 camera を複製して描く。複製には MoonBallDemo と PlanetDescentCamera を付けない(主 camera の操作を奪わない)。
            var temporary=new List<GameObject>();
            for(int i=0;i<TemporaryCameras;i++){
                GameObject copy=UnityEngine.Object.Instantiate(objects.CameraObject);
                copy.name="Temporary Camera "+i;
                copy.tag="Untagged";
                foreach(MoonBallDemo extra in copy.GetComponents<MoonBallDemo>()){UnityEngine.Object.DestroyImmediate(extra);}
                foreach(PlanetDescentCamera extra in copy.GetComponents<PlanetDescentCamera>()){UnityEngine.Object.DestroyImmediate(extra);}
                temporary.Add(copy);
            }
            for(int round=0;round<3;round++){
                foreach(GameObject copy in temporary){Render(copy.GetComponent<Camera>());}
                yield return null;
            }
            foreach(object step in Frames(10)){yield return step;}
            long withCameras=Memory();
            long growth=withCameras-baseline;
            report.Add("with "+TemporaryCameras+" temporary cameras: graphicsDriverMemory="+Mebibytes(withCameras)+" growth="+Mebibytes(growth));
            if(growth<MinimumGrowthPerCamera*TemporaryCameras){
                failures.Add("一時 camera を描いても GPU の memory が増えません(増加 "+Mebibytes(growth)+"、期待 "+Mebibytes(MinimumGrowthPerCamera*TemporaryCameras)+" 以上)。測れていません。");
            }

            // 破棄して、数 frame 進める(GPU の memory の解放は数 frame 遅れる)。
            foreach(GameObject copy in temporary){UnityEngine.Object.DestroyImmediate(copy);}
            temporary.Clear();
            foreach(object step in Frames(30)){yield return step;}
            long afterDestroy=Memory();
            long released=withCameras-afterDestroy;
            double ratio=0;
            if(growth>0){ratio=(double)released/growth;}
            report.Add("after destroying the cameras (30 frames): graphicsDriverMemory="+Mebibytes(afterDestroy)+" released="+Mebibytes(released)+" ratio="+ratio.ToString("F2",CultureInfo.InvariantCulture));
            if(!(ratio>=RequiredReleaseRatio)){
                failures.Add("一時 camera を破棄しても、増えた GPU の memory が戻りません(戻った割合 "+ratio.ToString("F2",CultureInfo.InvariantCulture)+"、期待 "+RequiredReleaseRatio+" 以上。camera の状態の解放漏れ)。");
            }

            // 主 camera は破棄後も描ける。
            foreach(object step in Converge("Main after destroy")){yield return step;}
            report.Add("pool: moon used="+moon.UsedSlots+"/"+moon.PoolCapacity+" lastError="+(moon.LastError??"(none)")+"/"+(ball.LastError??"(none)"));
            if(moon.LastError!=null||ball.LastError!=null){failures.Add("設定エラー: "+moon.LastError+" "+ball.LastError);}
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
