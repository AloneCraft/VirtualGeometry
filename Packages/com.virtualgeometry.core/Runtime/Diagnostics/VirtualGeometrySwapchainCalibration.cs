using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using Unity.Profiling;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;
namespace VirtualizedGeometry{
    /// <summary>通常swapchainとFrameTimingの生値を検証する。Profilerを有効化せず、GPU時間を推定しない。</summary>
    public static class VirtualGeometrySwapchainCalibration{
        [Serializable] private sealed class Result{
            public string api,unity,scope;public bool gpuRecorderSupported,frameTimingEnabled,profilerEnabled,gpuAreaEnabled,batchMode,focused;public int maxQueuedFrames,samplerPositiveFrames;public double samplerMaximumMs;public int updates,cameraCallbacks,timingSamples,positiveGpuSamples,zeroGpuSamples,nonBackgroundPixels=-1,width,height;public double maximumGpuMs;public string captureFailure;public string[] warnings;
        }
        public static IEnumerator Run(GameObject normal,GameObject lod,GameObject virtualObject,Camera camera,string output){
            Directory.CreateDirectory(output);bool useSampler=Array.IndexOf(Environment.GetCommandLineArgs(),"-vgSampler")>=0;if(Array.IndexOf(Environment.GetCommandLineArgs(),"-vgQueue1")>=0){QualitySettings.maxQueuedFrames=1;}camera.enabled=false;Object.Destroy(lod);Object.Destroy(virtualObject);yield return null;yield return Resources.UnloadUnusedAssets();
            QualitySettings.vSyncCount=0;Application.targetFrameRate=-1;Application.runInBackground=true;Profiler.SetAreaEnabled(ProfilerArea.GPU,false);Profiler.enabled=false;normal.SetActive(true);camera.targetTexture=null;camera.enabled=true;
            var result=new Result{api=SystemInfo.graphicsDeviceType.ToString(),unity=Application.unityVersion,gpuRecorderSupported=SystemInfo.supportsGpuRecorder,frameTimingEnabled=FrameTimingManager.IsFeatureEnabled(),profilerEnabled=Profiler.enabled,gpuAreaEnabled=Profiler.GetAreaEnabled(ProfilerArea.GPU),batchMode=Application.isBatchMode,focused=Application.isFocused,width=Screen.width,height=Screen.height,maxQueuedFrames=QualitySettings.maxQueuedFrames,scope="通常camera/swapchain。Profiler無効、FrameTimingManager.gpuFrameTimeの生値。起動window状態はLaunch.json/logを参照。画像は計測後のbackbuffer。"};
            var warnings=new List<string>();Application.LogCallback log=(message,stack,type)=>{if(type==LogType.Warning||type==LogType.Error||type==LogType.Exception){warnings.Add(message);}};Application.logMessageReceived+=log;
            var csv=new List<string>{"update,frame_start,first_submit,present,complete,cpu_ms,gpu_ms,camera_callbacks"};
                        var sampler=useSampler?new ProfilingSampler("VG Explicit Camera GPU Probe"):null;if(sampler!=null){sampler.enableRecording=true;}
            var gpuRecorder=useSampler?ProfilerRecorder.StartNew(ProfilerCategory.Render,"VG Explicit Camera GPU Probe",1,ProfilerRecorderOptions.StartImmediately|ProfilerRecorderOptions.WrapAroundWhenCapacityReached|ProfilerRecorderOptions.SumAllSamplesInFrame|ProfilerRecorderOptions.GpuRecorder):default;
            var samplerRows=new List<string>{"update,sample_count,gpu_ms"};
            Action<ScriptableRenderContext,Camera> begin=(context,current)=>{if(current!=camera||sampler==null){return;}using(var command=new CommandBuffer()){sampler.Begin(command);context.ExecuteCommandBuffer(command);}};
            Action<ScriptableRenderContext,Camera> end=(context,current)=>{if(current!=camera){return;}result.cameraCallbacks++;if(sampler!=null){using(var command=new CommandBuffer()){sampler.End(command);context.ExecuteCommandBuffer(command);context.Submit();}}};
            RenderPipelineManager.beginCameraRendering+=begin;RenderPipelineManager.endCameraRendering+=end;var timings=new FrameTiming[16];var seen=new HashSet<ulong>();double started=Time.realtimeSinceStartupAsDouble;
            while(Time.realtimeSinceStartupAsDouble-started<3){
                result.updates++;if(sampler!=null){float ms=0;int samples=0;if(gpuRecorder.Valid&&gpuRecorder.Count>0){var gpuSample=gpuRecorder.GetSample(0);ms=(float)(gpuSample.Value/1e6);samples=(int)gpuSample.Count;}if(samples>0&&ms>0){result.samplerPositiveFrames++;result.samplerMaximumMs=Math.Max(result.samplerMaximumMs,ms);}samplerRows.Add(result.updates+","+samples+","+ms.ToString("R",CultureInfo.InvariantCulture));}FrameTimingManager.CaptureFrameTimings();uint count=FrameTimingManager.GetLatestTimings(16,timings);
                for(int i=0;i<count;i++){
                    var t=timings[i];if(t.frameStartTimestamp==0||!seen.Add(t.frameStartTimestamp)){continue;}
                    result.timingSamples++;if(t.gpuFrameTime>0){result.positiveGpuSamples++;}else{result.zeroGpuSamples++;}result.maximumGpuMs=Math.Max(result.maximumGpuMs,t.gpuFrameTime);
                    csv.Add(string.Join(",",result.updates,t.frameStartTimestamp,t.firstSubmitTimestamp,t.cpuTimePresentCalled,t.cpuTimeFrameComplete,t.cpuFrameTime.ToString("R",CultureInfo.InvariantCulture),t.gpuFrameTime.ToString("R",CultureInfo.InvariantCulture),result.cameraCallbacks));
                }
                yield return null;
            }
            yield return new WaitForEndOfFrame();
            try{
                var image=new Texture2D(Screen.width,Screen.height,TextureFormat.RGB24,false);var previous=RenderTexture.active;
                try{RenderTexture.active=null;image.ReadPixels(new Rect(0,0,Screen.width,Screen.height),0,0);image.Apply();int colored=0;foreach(var pixel in image.GetPixels32()){if(pixel.r+pixel.g+pixel.b>6){colored++;}}result.nonBackgroundPixels=colored;File.WriteAllBytes(Path.Combine(output,"Swapchain.png"),image.EncodeToPNG());}
                finally{RenderTexture.active=previous;Object.Destroy(image);}
            }catch(Exception error){result.captureFailure=error.ToString();}
            RenderPipelineManager.beginCameraRendering-=begin;RenderPipelineManager.endCameraRendering-=end;if(sampler!=null){sampler.enableRecording=false;for(int drain=0;drain<32;drain++){yield return null;}sampler.Dispose();gpuRecorder.Dispose();}camera.enabled=false;Application.logMessageReceived-=log;result.warnings=warnings.ToArray();
            File.WriteAllLines(Path.Combine(output,"SamplerRaw.csv"),samplerRows);File.WriteAllLines(Path.Combine(output,"TimingRaw.csv"),csv);File.WriteAllText(Path.Combine(output,"Results.json"),JsonUtility.ToJson(result,true));
            bool passed=result.cameraCallbacks>0&&result.positiveGpuSamples>100&&result.nonBackgroundPixels>100&&warnings.Count==0&&string.IsNullOrEmpty(result.captureFailure);
            Debug.Log("Swapchain calibration "+(passed?"PASS":"FAIL")+": callbacks="+result.cameraCallbacks+", GPU ms="+result.maximumGpuMs+", positive samples="+result.positiveGpuSamples+", non-background="+result.nonBackgroundPixels);Application.Quit(passed?0:1);
        }
    }
}