using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Debug=UnityEngine.Debug;
using Object=UnityEngine.Object;

namespace VirtualizedGeometry.Samples.SpaceElevator.Editor{
    /// <summary>
    /// SpaceElevator.unity の描画負荷を条件別に計測する。batchmode(-force-d3d12)で実行し、結果を TestResults/SpaceElevator/perf_&lt;label&gt;.json と .txt へ書く。scene は保存しない。
    /// 実行: Unity.exe -batchmode -force-d3d12 -projectPath &lt;project&gt; -executeMethod VirtualizedGeometry.Samples.SpaceElevator.Editor.SpaceElevatorPerf.Run -perfLabel &lt;label&gt; -logFile Logs/SpaceElevatorPerf.log
    /// 条件ごとに Main Camera(参照画像と同じ構図)と平面反射カメラを描き、1 frame あたりの
    ///   (1) CPU 時間(描画命令の組み立てと投入)、(2) GPU の完了待ちを含む時間、(3) 管理ヒープの確保量、(4) VG の CPU/GPU 時間
    /// を記録する。条件の差で、影・反射・VG・後処理・解像度のどこが時間を使っているかを切り分ける(解像度を下げて時間が変わらなければ画素負荷ではなく pass 数や CPU が主因)。
    /// 使用 scene: SpaceElevator.unity。Scene view は batchmode に無いため、Game 種別の Main Camera で代用する(同じ pipeline・VG 描画経路)。
    /// </summary>
    public static class SpaceElevatorPerf{
        private const string Output="TestResults/SpaceElevator";
        private const int WarmupFrames=20,MeasureFrames=90,SettleFrames=3,MaxSettleFrames=900;

        [Serializable]
        private sealed class Row{
            public string name;
            public int width,height,frames;
            public double cpuMeanMs,cpuP50Ms,cpuP95Ms,syncMeanMs,syncP50Ms,syncP95Ms,gcBytesPerFrame,vgCpuMs,vgGpuMs;
            public long drawnTriangles;
            public int selectItems,drawItems,feedbackItems;
            public double hzbGpuMs,selectGpuMs,drawGpuMs;
        }

        [Serializable]
        private sealed class Report{
            public string label,unity,gpu,api,scene;
            public int renderers,proxyByMaterial;
            public bool gpuRecorderSupported;
            public Row[] rows;
        }

        private sealed class Condition{
            public string Name;
            public int Width,Height;
            public bool Move;
            public Action Apply;
        }

        private static readonly List<string> errors=new List<string>();
        private static readonly List<Row> rows=new List<Row>();
        private static readonly Queue<Condition> conditions=new Queue<Condition>();
        private static readonly List<double> cpuSamples=new List<double>();
        private static readonly List<double> syncSamples=new List<double>();
        private static List<VirtualGeometryRenderer> renderers;
        private static HashSet<VirtualGeometryRenderer> materialProxies;
        private static SpaceElevatorPlanarReflection reflection;
        private static Camera main;
        private static Light sun;
        private static UniversalAdditionalCameraData cameraData;
        private static RenderTexture target;
        private static Texture2D syncPixel;
        private static Condition current;
        private static ProfilerRecorder gcRecorder;
        private static string label;
        private static double started;
        private static int frames,settled,measured;
        private static long gcTotal;
        private static bool settling;
        private static Vector3 moveOrigin;
        private static int moveFrame;
        private static string previousRenderPipeline;
        private static bool profileMarkers;
        private static readonly List<ProfilerRecorder> markerRecorders=new List<ProfilerRecorder>();
        private static readonly List<string> markerNames=new List<string>();
        private static string markerReport=string.Empty;

        /// <summary>batchmode の入口。</summary>
        public static void Run(){
            errors.Clear();rows.Clear();conditions.Clear();current=null;started=EditorApplication.timeSinceStartup;
            label="before";
            string[] arguments=Environment.GetCommandLineArgs();
            for(int i=0;i+1<arguments.Length;i++){
                if(arguments[i]=="-perfLabel"){label=arguments[i+1];}
            }
            profileMarkers=Array.IndexOf(arguments,"-perfProfile")>=0;
            // URP の SubShader(RenderPipeline タグ)で Material の pass を引くため、描画前でも URP と同じタグを入れておく(SpaceElevatorValidation と同じ)。
            previousRenderPipeline=Shader.globalRenderPipeline;Shader.globalRenderPipeline="UniversalPipeline";
            Application.logMessageReceived+=Log;
            try{
                SpaceElevatorSceneBuilder.UsePipeline();
                EditorSceneManager.OpenScene(SpaceElevatorSceneBuilder.ScenePath,OpenSceneMode.Single);
                CollectRenderers();
                main=Camera.main;
                if(main==null){throw new InvalidOperationException("SpaceElevator.unity に Main Camera がありません。");}
                reflection=main.GetComponent<SpaceElevatorPlanarReflection>();
                sun=RenderSettings.sun;
                cameraData=main.GetUniversalAdditionalCameraData();
                SpaceElevatorCapture.BlenderCamera hero=SpaceElevatorCapture.Hero;
                SpaceElevatorCapture.Apply(main,hero);
                moveOrigin=main.transform.position;
                Enqueue(hero.Width,hero.Height);
                if(profileMarkers||Array.IndexOf(arguments,"-perfQuick")>=0){
                    // marker 計測と -perfQuick は、基準と移動中の 2 条件だけにする(marker 計測は遅い)。
                    var kept=new Queue<Condition>();
                    foreach(Condition condition in conditions){
                        if(condition.Name.StartsWith("baseline(",StringComparison.Ordinal)||condition.Name.StartsWith("baseline カメラ",StringComparison.Ordinal)){kept.Enqueue(condition);}
                    }
                    conditions.Clear();
                    foreach(Condition condition in kept){conditions.Enqueue(condition);}
                }
                gcRecorder=ProfilerRecorder.StartNew(ProfilerCategory.Memory,"GC Allocated In Frame");
                settling=true;frames=0;settled=0;
                EditorApplication.update+=Tick;
            }catch(Exception error){Finish(error);}
        }

        // 条件は「基準」から順に要素を外していく。各条件の Apply は直前の状態を引き継がず、毎回全体を設定し直す。
        private static void Enqueue(int width,int height){
            int halfWidth=width/2,halfHeight=height/2;
            LightShadows shadows=LightShadows.Soft;
            if(sun!=null){shadows=sun.shadows;}
            Action<bool,bool,bool,bool> set=(reflectionOn,shadowOn,virtualOn,postOn)=>{
                if(reflection!=null){reflection.enabled=reflectionOn;}
                if(sun!=null){
                    if(shadowOn){sun.shadows=shadows;}else{sun.shadows=LightShadows.None;}
                }
                foreach(VirtualGeometryRenderer renderer in renderers){
                    if(renderer!=null){renderer.IsProxyForced=!virtualOn;}
                }
                cameraData.renderPostProcessing=postOn;
            };
            conditions.Enqueue(new Condition{Name="baseline(反射+影+VG+後処理)",Width=width,Height=height,Apply=()=>set(true,true,true,true)});
            conditions.Enqueue(new Condition{Name="baseline カメラ移動中",Width=width,Height=height,Move=true,Apply=()=>set(true,true,true,true)});
            conditions.Enqueue(new Condition{Name="反射なし",Width=width,Height=height,Apply=()=>set(false,true,true,true)});
            conditions.Enqueue(new Condition{Name="影なし",Width=width,Height=height,Apply=()=>set(true,false,true,true)});
            conditions.Enqueue(new Condition{Name="反射なし+影なし",Width=width,Height=height,Apply=()=>set(false,false,true,true)});
            conditions.Enqueue(new Condition{Name="後処理なし",Width=width,Height=height,Apply=()=>set(true,true,true,false)});
            conditions.Enqueue(new Condition{Name="VGなし(通常Proxy)",Width=width,Height=height,Apply=()=>set(true,true,false,true)});
            conditions.Enqueue(new Condition{Name="反射なし+影なし+後処理なし",Width=width,Height=height,Apply=()=>set(false,false,true,false)});
            conditions.Enqueue(new Condition{Name="baseline 半解像度",Width=halfWidth,Height=halfHeight,Apply=()=>set(true,true,true,true)});
            conditions.Enqueue(new Condition{Name="反射なし+影なし 半解像度",Width=halfWidth,Height=halfHeight,Apply=()=>set(false,false,true,true)});
        }

        private static void Log(string message,string stack,LogType type){
            if(type==LogType.Error||type==LogType.Exception||type==LogType.Assert){errors.Add(message);}
        }

        // 半透明など Material が VG で描けない部品は、仕様どおり Proxy で描かれるので VG の完了判定から外す(SpaceElevatorValidation と同じ規則)。
        private static void CollectRenderers(){
            renderers=new List<VirtualGeometryRenderer>(Object.FindObjectsByType<VirtualGeometryRenderer>());
            materialProxies=new HashSet<VirtualGeometryRenderer>();
            foreach(VirtualGeometryRenderer renderer in renderers){
                foreach(Material material in renderer.GetComponent<MeshRenderer>().sharedMaterials){
                    if(material!=null&&!VirtualGeometryMaterials.TryResolve(material,out _,out _,out string _)){
                        materialProxies.Add(renderer);
                        break;
                    }
                }
            }
        }

        private static bool VirtualSettled(Camera camera,out string pending){
            var text=new StringBuilder();int count=0;
            foreach(VirtualGeometryRenderer renderer in renderers){
                if(renderer==null||!renderer.isActiveAndEnabled||materialProxies.Contains(renderer)||renderer.IsProxyForced){continue;}
                if((camera.cullingMask&(1<<renderer.gameObject.layer))==0){continue;}
                bool busy=renderer.IsFallbackVisible||!renderer.IsStreamingRootReady;
                var stats=renderer.StreamingStatistics;
                if(!busy&&stats!=null&&(stats.Queued!=0||stats.Loading!=0||stats.Uploading!=0)){busy=true;}
                if(busy){
                    count++;
                    if(count<=4){text.Append(renderer.name).Append('[').Append(renderer.DiagnosticReason).Append("] ");}
                }
            }
            pending=count+" pending "+text;
            return count==0;
        }

        private static void EnsureTarget(int width,int height){
            if(target!=null&&target.width==width&&target.height==height){return;}
            if(target!=null){target.Release();Object.DestroyImmediate(target);}
            target=new RenderTexture(width,height,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB){name="SpaceElevator Perf Target",hideFlags=HideFlags.HideAndDontSave};
            target.Create();
            if(syncPixel==null){syncPixel=new Texture2D(1,1,TextureFormat.RGBA32,false,false){hideFlags=HideFlags.HideAndDontSave};}
        }

        // Main Camera と平面反射カメラを 1 frame 分描く。戻り値は(描画命令の投入までの CPU 時間、GPU 完了待ちを含む時間)。
        private static void RenderFrame(Condition condition,out double cpuMs,out double syncMs){
            EnsureTarget(condition.Width,condition.Height);
            // 移動中の条件は、Scene view を操作している状況に近づけるため、毎 frame 少しずつ位置を動かす(約 0.5 m/frame の往復)。
            main.transform.position=moveOrigin;
            if(condition.Move){
                moveFrame++;
                main.transform.position=moveOrigin+new Vector3(Mathf.Sin(moveFrame*0.05f)*10f,0f,Mathf.Cos(moveFrame*0.05f)*10f);
            }
            var watch=Stopwatch.StartNew();
            RenderTexture previousTarget=main.targetTexture;
            main.targetTexture=target;
            if(reflection!=null&&reflection.isActiveAndEnabled){
                reflection.UpdateReflectionCamera();
                var mirrorRequest=new RenderPipeline.StandardRequest{destination=reflection.Target};
                RenderPipeline.SubmitRenderRequest(reflection.ReflectionCamera,mirrorRequest);
            }
            var request=new RenderPipeline.StandardRequest{destination=target};
            RenderPipeline.SubmitRenderRequest(main,request);
            cpuMs=watch.Elapsed.TotalMilliseconds;
            // 1 画素を読み戻して GPU の完了まで待つ。待ち時間を含む値が、GPU が律速のときの実 frame 時間に近い。
            RenderTexture previousActive=RenderTexture.active;
            RenderTexture.active=target;
            syncPixel.ReadPixels(new Rect(0,0,1,1),0,0);
            RenderTexture.active=previousActive;
            syncMs=watch.Elapsed.TotalMilliseconds;
            main.targetTexture=previousTarget;
        }

        private static void Tick(){
            try{
                if(EditorApplication.timeSinceStartup-started>1800){throw new TimeoutException("SpaceElevator perf timeout.");}
                if(current==null){
                    if(conditions.Count==0){Complete();return;}
                    current=conditions.Dequeue();
                    current.Apply();
                    frames=0;settled=0;measured=0;settling=true;
                    cpuSamples.Clear();syncSamples.Clear();
                }
                RenderFrame(current,out double cpu,out double sync);
                frames++;
                if(settling){
                    // VG の起動完了(root 準備・読込待ちなし)が続くまで測らない。VG なしの条件は WarmupFrames だけ待つ。
                    bool ready=VirtualSettled(main,out string pending);
                    if(ready){settled++;}else{settled=0;}
                    if(settled>=SettleFrames&&frames>=WarmupFrames){
                        settling=false;measured=0;
                        VirtualGeometryDiagnostics.Enabled=true;
                        gcTotal=0;
                        if(profileMarkers){StartMarkers();}
                    }else if(frames>=MaxSettleFrames){
                        throw new TimeoutException(current.Name+": "+MaxSettleFrames+" frame 待っても VG が揃いません("+pending+")。");
                    }
                    EditorApplication.QueuePlayerLoopUpdate();
                    return;
                }
                cpuSamples.Add(cpu);syncSamples.Add(sync);
                gcTotal+=gcRecorder.LastValue;
                measured++;
                if(measured<MeasureFrames){EditorApplication.QueuePlayerLoopUpdate();return;}
                rows.Add(MakeRow(current));
                VirtualGeometryDiagnostics.Enabled=false;
                current=null;
                EditorApplication.QueuePlayerLoopUpdate();
            }catch(Exception error){Finish(error);}
        }

        private static Row MakeRow(Condition condition){
            var row=new Row{name=condition.Name,width=condition.Width,height=condition.Height,frames=cpuSamples.Count};
            row.cpuMeanMs=Mean(cpuSamples);row.cpuP50Ms=Percentile(cpuSamples,0.5);row.cpuP95Ms=Percentile(cpuSamples,0.95);
            row.syncMeanMs=Mean(syncSamples);row.syncP50Ms=Percentile(syncSamples,0.5);row.syncP95Ms=Percentile(syncSamples,0.95);
            row.gcBytesPerFrame=(double)gcTotal/Math.Max(1,cpuSamples.Count);
            row.vgCpuMs=VirtualGeometryDiagnostics.CpuMilliseconds;
            row.vgGpuMs=VirtualGeometryDiagnostics.GpuMilliseconds;
            row.hzbGpuMs=VirtualGeometryDiagnostics.HzbGpuMilliseconds;row.selectGpuMs=VirtualGeometryDiagnostics.SelectGpuMilliseconds;row.drawGpuMs=VirtualGeometryDiagnostics.DrawGpuMilliseconds;
            long triangles=0;
            foreach(VirtualGeometryRenderer renderer in renderers){
                if(renderer==null||!renderer.isActiveAndEnabled||materialProxies.Contains(renderer)||renderer.IsProxyForced){continue;}
                triangles+=renderer.ReadMetricsForDiagnostics(main).DrawnTriangles;
            }
            row.drawnTriangles=triangles;
            row.selectItems=VirtualGeometryDiagnostics.LastSelectItems;row.drawItems=VirtualGeometryDiagnostics.LastDrawItems;row.feedbackItems=VirtualGeometryDiagnostics.LastFeedbackItems;
            if(markerRecorders.Count>0){markerReport+="["+condition.Name+"]"+Environment.NewLine+CollectMarkers();}
            return row;
        }

        // 診断用: 時間を測る全 marker の 1 frame あたり合計(包含時間)を記録し、大きい順に出す。条件「baseline」だけで使う。
        private static void StartMarkers(){
            var handles=new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            foreach(ProfilerRecorderHandle handle in handles){
                ProfilerRecorderDescription description=ProfilerRecorderHandle.GetDescription(handle);
                if(description.UnitType!=ProfilerMarkerDataUnit.TimeNanoseconds){continue;}
                var recorder=new ProfilerRecorder(handle,MeasureFrames+8,ProfilerRecorderOptions.StartImmediately|ProfilerRecorderOptions.SumAllSamplesInFrame);
                markerRecorders.Add(recorder);
                markerNames.Add(description.Name);
            }
        }

        private static string CollectMarkers(){
            var entries=new List<KeyValuePair<double,string>>();
            for(int i=0;i<markerRecorders.Count;i++){
                ProfilerRecorder recorder=markerRecorders[i];
                int count=recorder.Count;
                if(count==0){recorder.Dispose();continue;}
                double sum=0;
                for(int j=0;j<count;j++){sum+=recorder.GetSample(j).Value;}
                recorder.Dispose();
                double meanMs=sum/count/1000000.0;
                if(meanMs>=0.3){entries.Add(new KeyValuePair<double,string>(meanMs,markerNames[i]));}
            }
            markerRecorders.Clear();markerNames.Clear();
            entries.Sort((left,right)=>right.Key.CompareTo(left.Key));
            var text=new StringBuilder();
            text.AppendLine("== marker(1 frame あたりの平均 ms、包含時間・全 thread 合算。0.3 ms 以上。EndJob 等の job 系は無視する)");
            int limit=Math.Min(entries.Count,90);
            for(int i=0;i<limit;i++){text.AppendLine(string.Format(CultureInfo.InvariantCulture,"{0,9:F2}  {1}",entries[i].Key,entries[i].Value));}
            return text.ToString();
        }

        private static double Mean(List<double> values){
            if(values.Count==0){return 0;}
            double sum=0;
            foreach(double value in values){sum+=value;}
            return sum/values.Count;
        }

        private static double Percentile(List<double> values,double fraction){
            if(values.Count==0){return 0;}
            var sorted=new List<double>(values);
            sorted.Sort();
            int index=Mathf.Clamp((int)Math.Ceiling(fraction*sorted.Count)-1,0,sorted.Count-1);
            return sorted[index];
        }

        private static void Complete(){
            Finish(null);
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Log;
            Shader.globalRenderPipeline=previousRenderPipeline;
            VirtualGeometryDiagnostics.Enabled=false;
            if(gcRecorder.Valid){gcRecorder.Dispose();}
            if(target!=null){target.Release();Object.DestroyImmediate(target);target=null;}
            if(syncPixel!=null){Object.DestroyImmediate(syncPixel);syncPixel=null;}
            Directory.CreateDirectory(Output);
            var report=new Report{
                label=label,unity=Application.unityVersion,gpu=SystemInfo.graphicsDeviceName,api=SystemInfo.graphicsDeviceType.ToString(),scene=SpaceElevatorSceneBuilder.ScenePath,
                renderers=0,proxyByMaterial=0,gpuRecorderSupported=SystemInfo.supportsGpuRecorder,rows=rows.ToArray()
            };
            if(renderers!=null){report.renderers=renderers.Count;}
            if(materialProxies!=null){report.proxyByMaterial=materialProxies.Count;}
            File.WriteAllText(Output+"/perf_"+label+".json",JsonUtility.ToJson(report,true));
            var text=new StringBuilder();
            text.AppendLine("label="+label+" gpu="+report.gpu+" api="+report.api+" renderers="+report.renderers+" proxyByMaterial="+report.proxyByMaterial+" gpuRecorder="+report.gpuRecorderSupported);
            foreach(Row row in rows){
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,"{0} [{1}x{2}] cpu mean={3:F2} p50={4:F2} p95={5:F2} ms | sync mean={6:F2} p50={7:F2} p95={8:F2} ms | gc={9:F0} B/frame | VG cpu={10:F2} gpu={11:F2} ms | drawn tris={12} | items select={13} draw={14} feedback={15} | gpu hzb={16:F2} select={17:F2} draw={18:F2} ms",
                    row.name,row.width,row.height,row.cpuMeanMs,row.cpuP50Ms,row.cpuP95Ms,row.syncMeanMs,row.syncP50Ms,row.syncP95Ms,row.gcBytesPerFrame,row.vgCpuMs,row.vgGpuMs,row.drawnTriangles,row.selectItems,row.drawItems,row.feedbackItems,row.hzbGpuMs,row.selectGpuMs,row.drawGpuMs));
            }
            text.Append(markerReport);
            if(errors.Count>0){text.AppendLine("Console error: "+string.Join(" | ",errors));}
            if(error!=null){text.AppendLine("FAIL: "+error.Message);Debug.LogException(error);}else{text.AppendLine("DONE");}
            File.WriteAllText(Output+"/perf_"+label+".txt",text.ToString());
            if(Application.isBatchMode){
                int code=1;
                if(error==null){code=0;}
                EditorApplication.Exit(code);
            }
        }
    }
}
