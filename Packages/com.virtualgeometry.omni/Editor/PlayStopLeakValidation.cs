using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;
using VirtualizedGeometry.Editor;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// Play Mode の開始・終了(Play/Stop)を 10 回繰り返しても、GPU の memory と Unity の object が増え続けないことの検証(batchmode)。
    /// 月とボールのデモ scene を開き、Play Mode に入り、毎 frame 描いて(batchmode では camera は自動では描かれない)選択と生成を進め、停止する。
    /// 停止後に数 frame 進めた GPU の memory(<see cref="Profiler.GetAllocatedMemoryForGraphicsDriver"/>)と、Unity の object の数を、周ごとに記録する。
    /// 条件 A: domain reload と scene reload を止める(EnterPlayModeOptions)。条件 B: domain reload だけ止める。
    /// (既定の domain reload ありでは、static の状態ごと検証の状態が消えるので、この検証の枠組みでは測れない。)
    /// 1 周目は準備(shader・pipeline の初回の確保)なので、2 周目以降の増加で見る。
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.PlayStopLeakValidation.Run(結果は TestResults/Planet/PlayStopLeak)
    /// </summary>
    public static class PlayStopLeakValidation{
        private const int Cycles=10;
        private const int PlayFrames=150;
        private const int SettleFrames=45;
        // 2 周目から最後の周までの GPU の memory の増加の許容(割り当ての断片化の揺れを見込む)と、Unity の object の数の増加の許容。
        private const long MemoryGrowthTolerance=64L*1024*1024;
        private const int ObjectGrowthTolerance=64;

        private enum Stage{StartCycle,WaitPlaying,Playing,WaitEdit,Settle}

        private static string results;
        private static string scenePath;
        private static Stage stage;
        private static int stageFrames;
        private static int condition;
        private static int cycle;
        private static double started;
        private static long peak;
        private static readonly List<long> memoryAfterStop=new List<long>();
        private static readonly List<int> objectsAfterStop=new List<int>();
        // 2 周目と最後の周の、Unity の object の型ごとの数(増えた型を報告に書く)。
        private static Dictionary<string,int> typeCountsSecond;
        private static Dictionary<string,int> typeCountsLast;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();

        private static readonly EnterPlayModeOptions[] Conditions={
            EnterPlayModeOptions.DisableDomainReload|EnterPlayModeOptions.DisableSceneReload,
            EnterPlayModeOptions.DisableDomainReload,
        };
        private static readonly string[] ConditionNames={"A (DisableDomainReload|DisableSceneReload)","B (DisableDomainReload)"};

        public static void Run(){
            results="TestResults/Planet/PlayStopLeak";
            scenePath=MoonBallSettings.Moon.ScenePath;
            report.Clear();failures.Clear();errors.Clear();memoryAfterStop.Clear();objectsAfterStop.Clear();
            started=EditorApplication.timeSinceStartup;condition=0;cycle=0;stage=Stage.StartCycle;stageFrames=0;
            Directory.CreateDirectory(results);
            Application.logMessageReceived+=Record;
            try{
                MoonBallSceneBuilder.UsePipeline();
                EditorSceneManager.OpenScene(scenePath,OpenSceneMode.Single);
                EditorSettings.enterPlayModeOptionsEnabled=true;
                report.Add("environment: GPU="+SystemInfo.graphicsDeviceName+" API="+SystemInfo.graphicsDeviceType+" scene="+scenePath+" cycles="+Cycles+" playFrames="+PlayFrames);
                EditorApplication.update+=Tick;
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void Record(string condition2,string stack,LogType type){
            if(type==LogType.Error||type==LogType.Exception||type==LogType.Assert){
                if(condition2.Contains("[OmniConversion] ValidatedOnly")||condition2.Contains("Localization/ja.po will not be compiled")){return;}
                errors.Add(condition2);
            }
        }

        private static string Mebibytes(long bytes){
            return (bytes/(1024.0*1024.0)).ToString("F0",CultureInfo.InvariantCulture)+" MiB";
        }

        private static void Tick(){
            try{
                if(EditorApplication.timeSinceStartup-started>3000){throw new TimeoutException("PlayStopLeak validation timeout.");}
                stageFrames++;
                switch(stage){
                    case Stage.StartCycle:
                        if(EditorApplication.isPlaying||EditorApplication.isCompiling||EditorApplication.isUpdating){return;}
                        EditorSettings.enterPlayModeOptions=Conditions[condition];
                        EditorApplication.EnterPlaymode();
                        stage=Stage.WaitPlaying;stageFrames=0;
                        break;
                    case Stage.WaitPlaying:
                        if(EditorApplication.isPlaying&&!EditorApplication.isPaused){stage=Stage.Playing;stageFrames=0;peak=0;}
                        else if(stageFrames>1800){throw new TimeoutException("Play Mode に入れません。");}
                        break;
                    case Stage.Playing:
                        {
                            // batchmode には Game View が無く、camera は自動では描かれない。選択と生成は描画のたびに進むので、毎 frame 描く。
                            Camera camera=Camera.main;
                            if(camera==null){throw new InvalidOperationException("Main Camera がありません。");}
                            UnityEngine.Object.DestroyImmediate(M0Validation.Capture(camera,160,120));
                            peak=Math.Max(peak,Profiler.GetAllocatedMemoryForGraphicsDriver());
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
                        {
                            long memory=Profiler.GetAllocatedMemoryForGraphicsDriver();
                            UnityEngine.Object[] all=Resources.FindObjectsOfTypeAll<UnityEngine.Object>();
                            int objects=all.Length;
                            memoryAfterStop.Add(memory);objectsAfterStop.Add(objects);
                            if(memoryAfterStop.Count==2){typeCountsSecond=CountTypes(all);}
                            if(memoryAfterStop.Count==Cycles){typeCountsLast=CountTypes(all);}
                            report.Add("condition "+ConditionNames[condition]+" cycle "+(memoryAfterStop.Count)+": peak="+Mebibytes(peak)+" afterStop="+Mebibytes(memory)+" unityObjects="+objects);
                            cycle++;
                            if(cycle>=Cycles){
                                Evaluate();
                                condition++;cycle=0;memoryAfterStop.Clear();objectsAfterStop.Clear();
                                if(condition>=Conditions.Length){Finish(null);return;}
                            }
                            stage=Stage.StartCycle;stageFrames=0;
                        }
                        break;
                }
            }catch(Exception error){
                Finish(error);
            }
        }

        private static Dictionary<string,int> CountTypes(UnityEngine.Object[] all){
            var counts=new Dictionary<string,int>();
            foreach(UnityEngine.Object value in all){
                if(value==null){continue;}
                string name=value.GetType().Name;
                counts.TryGetValue(name,out int count);
                counts[name]=count+1;
                // GameObject は名前と hideFlags ごとにも数える(増えた GameObject が何かを報告に書く)。
                if(value is GameObject){
                    string detailed="GameObject '"+value.name+"' hideFlags="+value.hideFlags;
                    counts.TryGetValue(detailed,out int detailedCount);
                    counts[detailed]=detailedCount+1;
                }
            }
            return counts;
        }

        // 2 周目から最後の周までの増加を見る。memory は 2 周目以降の最大と最小の差、object は最後と 2 周目の差。
        private static void Evaluate(){
            long first=memoryAfterStop[1];long maximum=first;
            for(int i=1;i<memoryAfterStop.Count;i++){maximum=Math.Max(maximum,memoryAfterStop[i]);}
            long growth=maximum-first;
            int objectGrowth=objectsAfterStop[objectsAfterStop.Count-1]-objectsAfterStop[1];
            if(typeCountsSecond!=null&&typeCountsLast!=null){
                var grown=new List<string>();
                foreach(KeyValuePair<string,int> pair in typeCountsLast){
                    typeCountsSecond.TryGetValue(pair.Key,out int before);
                    if(pair.Value>before){grown.Add(pair.Key+" +"+(pair.Value-before));}
                }
                if(grown.Count>0){report.Add("condition "+ConditionNames[condition]+": object types that grew between cycle 2 and cycle "+Cycles+": "+string.Join(", ",grown));}
            }
            report.Add("condition "+ConditionNames[condition]+": GPU memory growth after cycle 2 = "+Mebibytes(growth)+" (allowed "+Mebibytes(MemoryGrowthTolerance)+"), unity object growth = "+objectGrowth+" (allowed "+ObjectGrowthTolerance+")");
            if(!(growth<=MemoryGrowthTolerance)){failures.Add("条件 "+ConditionNames[condition]+": Play/Stop を繰り返すと GPU の memory が "+Mebibytes(growth)+" 増えました(許す増加 "+Mebibytes(MemoryGrowthTolerance)+")。");}
            if(!(objectGrowth<=ObjectGrowthTolerance)){failures.Add("条件 "+ConditionNames[condition]+": Play/Stop を繰り返すと Unity の object が "+objectGrowth+" 個増えました(許す増加 "+ObjectGrowthTolerance+")。");}
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Record;
            EditorSettings.enterPlayModeOptionsEnabled=false;
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
