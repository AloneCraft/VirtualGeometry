using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using Debug=UnityEngine.Debug;
using Object=UnityEngine.Object;

namespace VirtualizedGeometry{
    /// <summary>可視swapchainの同じasset/軌道を別Player processで計測する。画像と同期診断は計測後。</summary>
    public sealed class VirtualGeometryBenchmark:MonoBehaviour{
        public GameObject Normal;public GameObject Lod;public GameObject Virtual;public Camera Camera;
        public int InputTriangles;public long UniqueGeometryBytes;public long GeometryBudget;public long CpuBudget;
        private const int FramesPerPass=360;private string mode,output;private int frame,flushFrames;private bool measuring,finished;
        private ProfilerRecorder drawCalls,triangles,batches;private VirtualGeometryRenderer vg;
        private readonly FrameTiming[] timings=new FrameTiming[16];
        private readonly Dictionary<ulong,FrameTiming> latestTimings=new Dictionary<ulong,FrameTiming>();
        private readonly List<Snapshot> snapshots=new List<Snapshot>();private readonly Dictionary<int,Row> rows=new Dictionary<int,Row>();private readonly List<string> errors=new List<string>();
        private readonly List<string> timingProbe=new List<string>{"capture_update,matched_frame,frame_start,first_submit,present,complete,cpu_ms,gpu_ms"};
        private long cameraRenders,cpuBytes,gpuBytes;private readonly double[] phaseStart=new double[2],phaseCompleted=new double[2];private double deadline;private int swapchainPixels=-1,zeroToPositive;
        private sealed class Snapshot{public long Ticks;public int Frame;public bool Rendered;public long DrawCalls,Triangles,CpuBytes,GpuBytes,GpuGeometry,CpuCache,Io,Misses,Overflow,QualityMiss,Evictions;public int Transfer,Resident;}
        [Serializable] private sealed class Row{public string phase;public int frame;public double cpuMs,gpuMs,mainMs,renderMs,presentWaitMs;public ulong start,submit,present,complete;public long snapshotTicks,drawCalls,triangles,cpuBytes,gpuBytes,gpuGeometry,cpuCache,io,misses,overflow,qualityMiss,evictions;public int transfer,resident;}
        [Serializable] private sealed class Summary{public string phase;public int samples,gpuSamples,gpuMissingSamples;public double cpuP50,cpuP95,cpuP99,gpuP50,gpuP95,gpuP99,fps,cpuMean,gpuMean;public long drawCallsPeak,trianglesPeak,cpuPeakBytes,gpuDriverPeakBytes,gpuGeometryPeakBytes,cpuCachePeakBytes,ioBytes,misses,overflow,qualityMissSamples,evictions;public int transferPeak,residentPeak;}
        [Serializable] private sealed class Result{public string mode;public bool passed,gpuTimingAvailable,drawCounterAvailable;public string status,failure;public Summary[] summaries;public string[] warnings;public int inputTriangles,swapchainNonBackgroundPixels,gpuZeroToPositiveUpdates;public long uniqueGeometryBytes,geometryBudget,cameraCallbacks;public string measurementScope;}
        private IEnumerator Start(){
            this.Camera.enabled=false;this.mode="Virtual";this.output=Path.GetFullPath("TestResults/M5/BenchmarkRun");string[] args=Environment.GetCommandLineArgs();
            for(int i=0;i<args.Length-1;i++){if(args[i]=="-vgMode"){this.mode=args[i+1];}if(args[i]=="-vgResults"){this.output=Path.GetFullPath(args[i+1]);}}
            if(Array.IndexOf(args,"-vgCalibration")>=0){this.finished=true;yield return VirtualGeometrySwapchainCalibration.Run(this.Normal,this.Lod,this.Virtual,this.Camera,this.output);yield break;}
            Directory.CreateDirectory(this.output);File.WriteAllText(Path.Combine(this.output,"Visibility.txt"),string.Empty);File.WriteAllText(Path.Combine(this.output,"GeometryDiagnostics.csv"),"trajectory,drawn_triangles,geometry_draw_calls,selected_clusters\n");Application.logMessageReceived+=this.Log;
            QualitySettings.vSyncCount=0;QualitySettings.maxQueuedFrames=1;Application.targetFrameRate=-1;Application.runInBackground=true;VirtualGeometryDiagnostics.Enabled=false;Profiler.SetAreaEnabled(ProfilerArea.GPU,false);Profiler.enabled=false;
            if(this.mode!="Normal"&&this.mode!="LOD"&&this.mode!="Virtual"){this.Finish(new InvalidOperationException("Unknown benchmark mode."));yield break;}
            if(this.mode!="Normal"){Object.Destroy(this.Normal);this.Normal=null;}if(this.mode!="LOD"){Object.Destroy(this.Lod);this.Lod=null;}if(this.mode!="Virtual"){Object.Destroy(this.Virtual);this.Virtual=null;}
            yield return null;yield return Resources.UnloadUnusedAssets();GC.Collect();yield return null;
            this.drawCalls=ProfilerRecorder.StartNew(ProfilerCategory.Render,"Draw Calls Count",1);this.triangles=ProfilerRecorder.StartNew(ProfilerCategory.Render,"Triangles Count",1);this.batches=ProfilerRecorder.StartNew(ProfilerCategory.Render,"Batches Count",1);
            if(!FrameTimingManager.IsFeatureEnabled()||!this.drawCalls.Valid||!this.triangles.Valid){this.Finish(new InvalidOperationException("Required FrameTiming/Render profiler counters are unavailable."));yield break;}
            File.WriteAllText(Path.Combine(this.output,"Environment.txt"),"Unity="+Application.unityVersion+"\nOS="+SystemInfo.operatingSystem+"\nGPU="+SystemInfo.graphicsDeviceName+"\nAPI="+SystemInfo.graphicsDeviceType+"\nResolution="+Screen.width+"x"+Screen.height+"\nVSync="+QualitySettings.vSyncCount+"\nMaxQueuedFrames="+QualitySettings.maxQueuedFrames+"\nTargetFPS="+Application.targetFrameRate+"\nGpuRecorderSupported="+SystemInfo.supportsGpuRecorder+"\nProfilerEnabled="+Profiler.enabled+"\nGpuAreaEnabled="+Profiler.GetAreaEnabled(ProfilerArea.GPU)+"\nFrameTimingEnabled="+FrameTimingManager.IsFeatureEnabled()+"\nFrameTimerFrequency="+FrameTimingManager.GetCpuTimerFrequency()+"\nGpuTimerFrequency="+FrameTimingManager.GetGpuTimerFrequency()+"\nStopwatchFrequency="+Stopwatch.Frequency+"\nDevelopment="+Debug.isDebugBuild+"\nBatchMode="+Application.isBatchMode+"\nCold=empty VG CPU/GPU residency; OS file cache is not flushed. Warm=repeated identical 360-frame path in same process.\nGPU memory=Unity graphics-driver allocation estimate; CPU memory=sampled process working set.\nRendering=normal enabled camera to visible 1920x1080 swapchain, no timed StandardRequest/readback/pacing.\nMaterial=opaque Lit, base(0.65,0.4,0.2),metallic0,smoothness0.25; one directional light; shadows/fog/ambient disabled identically.\n");
            if(Screen.width!=1920||Screen.height!=1080||SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Direct3D12||FrameTimingManager.GetCpuTimerFrequency()!=(ulong)Stopwatch.Frequency||Application.isBatchMode){this.Finish(new InvalidOperationException("Benchmark requires a visible 1920x1080 DX12 Player without batchmode."));yield break;}
            if(this.Normal!=null){this.Normal.SetActive(true);}if(this.Lod!=null){this.Lod.SetActive(true);}if(this.Virtual!=null){this.Virtual.SetActive(true);this.vg=this.Virtual.GetComponent<VirtualGeometryRenderer>();}
            this.Camera.targetTexture=null;RenderPipelineManager.endCameraRendering+=this.EndCamera;
            this.snapshots.Add(new Snapshot{Ticks=VirtualGeometryProcessMemory.Timestamp,Frame=-1});this.measuring=true;this.deadline=Time.realtimeSinceStartupAsDouble+300;
            while(!this.finished&&(this.measuring||this.flushFrames<32)){yield return null;}if(this.finished){yield break;}
            yield return new WaitForEndOfFrame();
            try{
                if(this.errors.Count>0){throw new InvalidOperationException(string.Join(" | ",this.errors));}
                if(this.cameraRenders<FramesPerPass*2||this.snapshots.Any(s=>s.Frame>=0&&!s.Rendered)){throw new InvalidOperationException("A measured frame had no normal camera rendering; keep the Player window visible.");}
                foreach(string phase in new[]{"cold","warm"}){var selected=this.rows.Values.Where(row=>row.phase==phase).ToArray();int gpu=selected.Count(row=>row.gpuMs>0);if(selected.Length<320||selected.Any(row=>row.cpuMs<=0)||gpu<180){throw new InvalidOperationException("Insufficient CPU/GPU timings for "+phase+": CPU="+selected.Length+", GPU="+gpu);}}
                this.CaptureBackbuffer();this.Camera.enabled=false;
                for(int i=0;i<4;i++){this.SetCamera(i*90);this.Capture("Trajectory"+(i*90));if(this.vg!=null){var d=this.vg.ReadMetricsForDiagnostics(this.Camera);File.AppendAllText(Path.Combine(this.output,"GeometryDiagnostics.csv"),(i*90)+","+d.DrawnTriangles+","+d.DrawCalls+","+d.SelectedClusters+"\n");}}
                if(this.vg!=null){var diagnostic=this.vg.ReadMetricsForDiagnostics(this.Camera);File.WriteAllText(Path.Combine(this.output,"AfterMeasurementDiagnostics.txt"),"selectedClusters="+diagnostic.SelectedClusters+"\ndrawnTriangles="+diagnostic.DrawnTriangles+"\ngeometry="+diagnostic.GpuGeometryBytes+"\nmetadata="+diagnostic.GpuMetadataBytes+"\nselection="+diagnostic.GpuSelectionBytes+"\n");if(this.vg.StreamingStatistics.GpuAllocatedBytes>this.GeometryBudget||this.vg.StreamingStatistics.PeakCpuBytes>this.CpuBudget||this.vg.StreamingStatistics.UnsafeSlotReuses!=0){throw new InvalidOperationException("VG benchmark violated memory/fence invariants.");}}
                this.Finish(null);
            }catch(Exception error){this.Finish(error);}
        }
        private void Update(){try{this.Tick();}catch(Exception error){this.Finish(error);}}
        private void Tick(){
            if(this.finished||this.snapshots.Count==0){return;}
            if(Time.realtimeSinceStartupAsDouble>this.deadline){throw new TimeoutException("Benchmark did not finish within 300 seconds.");}
            long now=VirtualGeometryProcessMemory.Timestamp;if(this.frame==FramesPerPass&&this.phaseCompleted[0]==0){this.phaseCompleted[0]=now;}if(this.frame==FramesPerPass*2&&this.phaseCompleted[1]==0){this.phaseCompleted[1]=now;}
            var snapshot=new Snapshot{Ticks=now,Frame=this.measuring?this.frame:-1,DrawCalls=this.drawCalls.LastValue,Triangles=this.triangles.LastValue};this.snapshots.Add(snapshot);
            if(this.measuring){
                int phase=this.frame/FramesPerPass;if(this.frame%FramesPerPass==0){this.phaseStart[phase]=now;}this.SetCamera(this.frame%FramesPerPass);this.Camera.enabled=true;
                if(this.frame%10==0){this.cpuBytes=VirtualGeometryProcessMemory.WorkingSetBytes;this.gpuBytes=Profiler.GetAllocatedMemoryForGraphicsDriver();}
                var s=this.vg?.StreamingStatistics;snapshot.CpuBytes=this.cpuBytes;snapshot.GpuBytes=this.gpuBytes;snapshot.GpuGeometry=s?.GpuAllocatedBytes??0;snapshot.CpuCache=s?.CpuBytes??0;snapshot.Io=s?.IoBytes??0;snapshot.Misses=s?.Misses??0;snapshot.Overflow=s?.QueueOverflow??0;snapshot.QualityMiss=s?.QualityTargetMisses??0;snapshot.Evictions=s?.Evictions??0;snapshot.Transfer=s?.TransferBytesThisFrame??0;snapshot.Resident=s?.Resident??0;
                this.frame++;if(this.frame==FramesPerPass*2){this.measuring=false;}
            }else{this.flushFrames++;}
            FrameTimingManager.CaptureFrameTimings();uint count=FrameTimingManager.GetLatestTimings((uint)this.timings.Length,this.timings);
            for(int i=0;i<count;i++){
                var t=this.timings[i];if(t.frameStartTimestamp==0){continue;}bool had=this.latestTimings.TryGetValue(t.frameStartTimestamp,out var prior);if(had&&prior.gpuFrameTime==t.gpuFrameTime){continue;}
                if(had&&prior.gpuFrameTime>0&&t.gpuFrameTime<=0){continue;}if(had&&prior.gpuFrameTime<=0&&t.gpuFrameTime>0){this.zeroToPositive++;}this.latestTimings[t.frameStartTimestamp]=t;
                int index=this.snapshots.FindIndex(s=>(ulong)s.Ticks>=t.frameStartTimestamp);this.timingProbe.Add(string.Join(",",Time.frameCount,index>0?this.snapshots[index].Frame:-1,t.frameStartTimestamp,t.firstSubmitTimestamp,t.cpuTimePresentCalled,t.cpuTimeFrameComplete,t.cpuFrameTime.ToString("R",CultureInfo.InvariantCulture),t.gpuFrameTime.ToString("R",CultureInfo.InvariantCulture)));
                if(index<=0||index+1>=this.snapshots.Count){continue;}var sample=this.snapshots[index];if(sample.Frame<0||!sample.Rendered){continue;}var next=this.snapshots[index+1];
                this.rows[sample.Frame]=new Row{phase=sample.Frame<FramesPerPass?"cold":"warm",frame=sample.Frame%FramesPerPass,cpuMs=t.cpuFrameTime,gpuMs=t.gpuFrameTime>0?t.gpuFrameTime:-1,mainMs=t.cpuMainThreadFrameTime,renderMs=t.cpuRenderThreadFrameTime,presentWaitMs=t.cpuMainThreadPresentWaitTime,start=t.frameStartTimestamp,submit=t.firstSubmitTimestamp,present=t.cpuTimePresentCalled,complete=t.cpuTimeFrameComplete,snapshotTicks=sample.Ticks,
                    drawCalls=next.DrawCalls,triangles=next.Triangles,cpuBytes=sample.CpuBytes,gpuBytes=sample.GpuBytes,gpuGeometry=sample.GpuGeometry,cpuCache=sample.CpuCache,io=sample.Io,misses=sample.Misses,overflow=sample.Overflow,qualityMiss=sample.QualityMiss,evictions=sample.Evictions,transfer=sample.Transfer,resident=sample.Resident};
            }
        }
        private void EndCamera(ScriptableRenderContext context,Camera camera){if(camera!=this.Camera){return;}this.cameraRenders++;if(this.snapshots.Count>0){this.snapshots[this.snapshots.Count-1].Rendered=true;}}
        private void SetCamera(int index){float t=(float)index/FramesPerPass*Mathf.PI*2;this.Camera.transform.position=new Vector3(Mathf.Sin(t)*25,Mathf.Cos(t*2)*10,-(140+100*Mathf.Cos(t)));this.Camera.transform.LookAt(Vector3.zero);}
        private void Log(string message,string stack,LogType type){if(type==LogType.Warning||type==LogType.Error||type==LogType.Exception||type==LogType.Assert){this.errors.Add(message);}}
        private void CaptureBackbuffer(){
            var previous=RenderTexture.active;var image=new Texture2D(Screen.width,Screen.height,TextureFormat.RGB24,false);
            try{RenderTexture.active=null;image.ReadPixels(new Rect(0,0,Screen.width,Screen.height),0,0);image.Apply();this.swapchainPixels=image.GetPixels32().Count(p=>p.r+p.g+p.b>6);File.WriteAllBytes(Path.Combine(this.output,"Swapchain.png"),image.EncodeToPNG());if(this.swapchainPixels<100){throw new InvalidOperationException("Normal swapchain image contains no visible geometry.");}}
            finally{RenderTexture.active=previous;Object.Destroy(image);}
        }
        private void Capture(string name){
            var target=RenderTexture.GetTemporary(1920,1080,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear,1);var old=this.Camera.targetTexture;var active=RenderTexture.active;Texture2D image=null;
            try{this.Camera.targetTexture=target;RenderPipeline.SubmitRenderRequest(this.Camera,new RenderPipeline.StandardRequest{destination=target});RenderTexture.active=target;image=new Texture2D(1920,1080,TextureFormat.RGBA32,false,true);image.ReadPixels(new Rect(0,0,1920,1080),0,0);image.Apply();File.WriteAllBytes(Path.Combine(this.output,name+".png"),image.EncodeToPNG());int colored=image.GetPixels32().Count(p=>p.r+p.g+p.b>6);File.AppendAllText(Path.Combine(this.output,"Visibility.txt"),name+",non-background pixels="+colored+"\n");if(colored<100){throw new InvalidOperationException("Benchmark image contains no visible geometry.");}}
            finally{this.Camera.targetTexture=old;RenderTexture.active=active;RenderTexture.ReleaseTemporary(target);if(image!=null){Object.Destroy(image);}}
        }
        private static double Percentile(double[] sorted,double p){if(sorted.Length==0){return -1;}return sorted[Mathf.Clamp((int)Math.Ceiling((sorted.Length-1)*p),0,sorted.Length-1)];}
        private Summary Summarize(string phase){
            Row[] r=this.rows.Values.Where(row=>row.phase==phase).ToArray();if(r.Length==0){return new Summary{phase=phase};}double[] cpu=r.Select(row=>row.cpuMs).OrderBy(v=>v).ToArray(),gpu=r.Where(row=>row.gpuMs>0).Select(row=>row.gpuMs).OrderBy(v=>v).ToArray();
            return new Summary{phase=phase,samples=r.Length,gpuSamples=gpu.Length,gpuMissingSamples=r.Length-gpu.Length,cpuP50=Percentile(cpu,.5),cpuP95=Percentile(cpu,.95),cpuP99=Percentile(cpu,.99),gpuP50=Percentile(gpu,.5),gpuP95=Percentile(gpu,.95),gpuP99=Percentile(gpu,.99),cpuMean=cpu.Average(),gpuMean=gpu.Length==0?-1:gpu.Average(),fps=FramesPerPass*FrameTimingManager.GetCpuTimerFrequency()/(this.phaseCompleted[phase=="cold"?0:1]-this.phaseStart[phase=="cold"?0:1]),drawCallsPeak=r.Max(row=>row.drawCalls),trianglesPeak=r.Max(row=>row.triangles),cpuPeakBytes=r.Max(row=>row.cpuBytes),gpuDriverPeakBytes=r.Max(row=>row.gpuBytes),gpuGeometryPeakBytes=r.Max(row=>row.gpuGeometry),cpuCachePeakBytes=r.Max(row=>row.cpuCache),ioBytes=r.Max(row=>row.io),misses=r.Max(row=>row.misses),overflow=r.Max(row=>row.overflow),qualityMissSamples=r.Count(row=>row.qualityMiss>0),evictions=r.Max(row=>row.evictions),transferPeak=r.Max(row=>row.transfer),residentPeak=r.Max(row=>row.resident)};
        }
        private void Finish(Exception error){
            if(this.finished){return;}this.finished=true;Application.logMessageReceived-=this.Log;RenderPipelineManager.endCameraRendering-=this.EndCamera;this.Camera.enabled=false;this.Camera.targetTexture=null;this.drawCalls.Dispose();this.triangles.Dispose();this.batches.Dispose();Profiler.enabled=false;
            var summaries=new[]{this.Summarize("cold"),this.Summarize("warm")};bool gpuAvailable=summaries.All(s=>s.gpuSamples>=180);
            var result=new Result{mode=this.mode,passed=error==null,gpuTimingAvailable=gpuAvailable,drawCounterAvailable=this.rows.Values.Any(row=>row.drawCalls>0),status=error!=null?"FAIL":summaries.Any(s=>s.gpuMissingSamples>0)?"MEASURED_WITH_MISSING_GPU_SAMPLES":"PASS_CPU_GPU_TIMING",failure=error?.ToString(),summaries=summaries,warnings=this.errors.ToArray(),inputTriangles=this.InputTriangles,uniqueGeometryBytes=this.UniqueGeometryBytes,geometryBudget=this.GeometryBudget,cameraCallbacks=this.cameraRenders,swapchainNonBackgroundPixels=this.swapchainPixels,gpuZeroToPositiveUpdates=this.zeroToPositive,
                measurementScope="Visible normal camera/swapchain, VSync0, maxQueuedFrames1, unlimited target rate. FrameTimingManager CPU/GPU fields matched by raw QPC frameStartTimestamp to Update snapshots. Late zero-to-positive GPU values replace earlier missing values; unreturned GPU samples stay missing and are counted separately, never treated as zero ms. GPU quantiles use positive actual GPU samples only. CPU includes waiting/overhead; FPS is Player-loop frame cadence/wall time. No timed StandardRequest, readback, PNG or diagnostic overlay. GPU Profiler disabled. Render counters are prior completed frame; indirect VG triangles are separately read after measurement. Cold residency, OS file cache uncontrolled."};
            File.WriteAllLines(Path.Combine(this.output,"TimingRaw.csv"),this.timingProbe);File.WriteAllLines(Path.Combine(this.output,"FrameSnapshots.csv"),new[]{"frame,qpc,rendered"}.Concat(this.snapshots.Select(s=>s.Frame+","+s.Ticks+","+(s.Rendered?1:0))));File.WriteAllText(Path.Combine(this.output,"Results.json"),JsonUtility.ToJson(result,true));
            var csv=new List<string>{"phase,frame,cpu_ms,gpu_ms,main_ms,render_ms,present_wait_ms,draw_calls,rendered_triangles,cpu_working_set,gpu_driver_estimate,gpu_geometry,cpu_cache,io_bytes,misses,overflow,quality_miss,evictions,transfer,resident,frame_start,first_submit,present,complete,snapshot_qpc"};
            foreach(Row r in this.rows.Values.OrderBy(row=>row.phase).ThenBy(row=>row.frame)){csv.Add(string.Join(",",r.phase,r.frame,r.cpuMs.ToString("R",CultureInfo.InvariantCulture),r.gpuMs<0?string.Empty:r.gpuMs.ToString("R",CultureInfo.InvariantCulture),r.mainMs.ToString("R",CultureInfo.InvariantCulture),r.renderMs.ToString("R",CultureInfo.InvariantCulture),r.presentWaitMs.ToString("R",CultureInfo.InvariantCulture),r.drawCalls,r.triangles,r.cpuBytes,r.gpuBytes,r.gpuGeometry,r.cpuCache,r.io,r.misses,r.overflow,r.qualityMiss,r.evictions,r.transfer,r.resident,r.start,r.submit,r.present,r.complete,r.snapshotTicks));}
            File.WriteAllLines(Path.Combine(this.output,"Metrics.csv"),csv);if(this.vg!=null){this.vg.enabled=false;}if(error==null){Debug.Log("Benchmark "+this.mode+" CPU/GPU measurement complete: "+result.status);}else{Debug.LogException(error);}Application.Quit(error==null?0:1);
        }
    }
}