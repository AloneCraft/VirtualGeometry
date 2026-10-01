using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;

namespace VirtualizedGeometry{
    /// <summary>Development Player専用の明示的な診断。同期readbackとPNG保存を通常性能測定へ含めない。</summary>
    public sealed class VirtualGeometryPlayerValidation:MonoBehaviour{
        public VirtualGeometryRenderer First;public VirtualGeometryRenderer Second;public Camera Primary;public Camera Secondary;
        public long GpuBudget;public long CpuBudget;public int TransferCap;public int IoCap;
        private readonly List<string> results=new List<string>();private readonly List<string> errors=new List<string>();
        private readonly List<string> metrics=new List<string>{"frame,root_ready,fallback,drawn_triangles,gpu_allocated,gpu_resident,cpu_bytes,io_bytes,queued,loading,uploading,resident,failed,transfer,misses,overflow,evictions,quality_miss,unsafe_reuse"};
        private string output;private bool finished;private Color32[] rootColor;private int bootstrapFrames;private long rootTriangles;private long peakTriangles;private int pendingFrames;private int qualityMissFrames;private int peakResidentPages;
        private IEnumerator Start(){
            this.output=Path.GetFullPath("TestResults/M5/PlayerRun");string[] args=Environment.GetCommandLineArgs();for(int i=0;i<args.Length-1;i++){if(args[i]=="-vgResults"){this.output=Path.GetFullPath(args[i+1]);}}
            Directory.CreateDirectory(this.output);Application.logMessageReceived+=this.Log;QualitySettings.vSyncCount=0;Application.targetFrameRate=60;
            File.WriteAllText(Path.Combine(this.output,"Environment.txt"),"UTC="+DateTime.UtcNow.ToString("O")+"\nUnity="+Application.unityVersion+"\nDevelopment="+Debug.isDebugBuild+"\nOS="+SystemInfo.operatingSystem+"\nGPU="+SystemInfo.graphicsDeviceName+"\nAPI="+SystemInfo.graphicsDeviceType+"\nDriver="+SystemInfo.graphicsDeviceVersion+"\nGPU memory MiB="+SystemInfo.graphicsMemorySize+"\nDiagnostic synchronous readback enabled; not a performance benchmark.\n");
            IEnumerator routine=this.Run();
            while(true){object next=null;bool more=false;try{more=routine.MoveNext();if(more){next=routine.Current;}}catch(Exception error){this.Finish(error);yield break;}if(!more){this.Finish(null);yield break;}yield return next;}
        }
        private void Log(string message,string stack,LogType type){if(type==LogType.Warning||type==LogType.Error||type==LogType.Exception||type==LogType.Assert){this.errors.Add(message);}}
        private IEnumerator Run(){
            Require(SystemInfo.graphicsDeviceType==GraphicsDeviceType.Direct3D12,"Player API is not DX12.");
            Require(this.First.SharesStreamingResourceWith(this.Second)&&VirtualGeometryRenderer.SharedResourceCount==1,"Initial shared resource duplication.");
            Require(this.First.GetComponent<MeshFilter>()==null,"Build retained source MeshFilter.");
            this.Primary.enabled=false;this.Secondary.enabled=false;
            double deadline=Time.realtimeSinceStartupAsDouble+30;
            while(true){
                Color32[] image=this.Capture("Bootstrap",this.Primary,this.bootstrapFrames==0);
                if(this.First.IsStreamingRootReady&&!this.First.IsFallbackVisible){break;}
                Require(this.First.IsFallbackVisible&&this.Second.IsFallbackVisible,"Bootstrap must display both proxies.");
                if(this.rootColor==null){this.rootColor=image;}else{Require(Difference(this.rootColor,image)==0,"Bootstrap image changed during partial root upload.");}
                this.bootstrapFrames++;this.Record();Require(Time.realtimeSinceStartupAsDouble<deadline,"Root upload timeout.");yield return null;
            }
            Color32[] coarse=this.Capture("VirtualRoot",this.Primary,true);Require(!this.First.IsFallbackVisible&&!this.Second.IsFallbackVisible,"Root publication did not switch to virtual.");
            Require(Difference(this.rootColor,coarse)==0,"Player root differs from bootstrap proxy.");this.rootTriangles=this.First.ReadMetricsForDiagnostics(this.Primary).DrawnTriangles*2;this.peakTriangles=this.rootTriangles;
            Require(this.bootstrapFrames>0,"Bootstrap was not observed.");this.results.Add("Bootstrap proxy frames="+this.bootstrapFrames+", root color mismatch=0, root triangles="+this.rootTriangles+", shared resources="+VirtualGeometryRenderer.SharedResourceCount);
            Require(this.First.PhysicsProxy.Raycast(new Ray(new Vector3(-9,0,-20),Vector3.forward),out RaycastHit hit,100),"Player physics proxy missed.");this.results.Add("Physics proxy distance="+hit.distance.ToString("R",CultureInfo.InvariantCulture));
            var surface=new Material(this.First.MeshAsset.ProxyLitMaterial){name="Player Lit"};surface.SetColor("_BaseColor",new Color(0.65f,0.4f,0.2f,1));surface.SetFloat("_Metallic",0.25f);surface.SetFloat("_Smoothness",0.4f);this.First.SetMaterials(surface);this.Second.SetMaterials(surface);
            Color32[] lit=this.Capture("LitVirtual",this.Primary,true);Require(Difference(coarse,lit)>100,"Player Lit did not change the image.");
            this.First.SetFallbackOverride(true);this.Second.SetFallbackOverride(true);Color32[] proxyLit=this.Capture("LitProxy",this.Primary,true);
            int litDifference=Difference(lit,proxyLit);Require(litDifference==0,"Player Lit/Proxy root comparison differs: "+litDifference);
            this.First.SetFallbackOverride(false);this.Second.SetFallbackOverride(false);this.results.Add("Player Lit versus URP Lit proxy changed pixels="+litDifference);
            this.First.SetLodSettings(0,0);this.Second.SetLodSettings(0,0);
            for(int i=0;i<240;i++){
                this.Primary.transform.position=new Vector3(Mathf.Sin(i*0.025f)*5,0,-20+Mathf.Sin(i*0.02f)*4);
                this.Capture("Streaming",this.Primary,i==239);this.Record();var stats=this.First.StreamingStatistics;
                if(stats.QualityTargetMisses>0){this.qualityMissFrames++;}
                long triangles=this.First.ReadMetricsForDiagnostics(this.Primary).DrawnTriangles+this.Second.ReadMetricsForDiagnostics(this.Primary).DrawnTriangles;this.peakTriangles=Math.Max(this.peakTriangles,triangles);
                if(stats.Queued+stats.Loading+stats.Uploading>0){
                    if(this.pendingFrames++==0){this.First.SetDebugView(VirtualGeometryDebugView.Loading);this.Second.SetDebugView(VirtualGeometryDebugView.Loading);this.Capture("LoadingPending",this.Primary,true);this.First.SetDebugView(VirtualGeometryDebugView.None);this.Second.SetDebugView(VirtualGeometryDebugView.None);}
                }
                yield return null;
            }
            var final=this.First.StreamingStatistics;Require(final.IoBytes>22512&&this.peakResidentPages>1&&this.peakTriangles>this.rootTriangles&&this.pendingFrames>0,"Player streaming did not refine after real I/O.");
            Require(this.First.SharesStreamingResourceWith(this.Second)&&VirtualGeometryRenderer.SharedResourceCount==1,"Player instances duplicated geometry or cache.");
            this.results.Add("Instances=2, resources=1, peak resident pages="+this.peakResidentPages+", final resident pages="+final.Resident+", geometry="+final.GpuAllocatedBytes+", resident peak="+final.PeakGpuResidentBytes+", CPU peak="+final.PeakCpuBytes+", I/O="+final.IoBytes+", peak transfer="+final.PeakTransferBytesPerFrame+", peak I/O concurrency="+final.PeakIoConcurrency);
            this.results.Add("Root/peak triangles="+this.rootTriangles+"/"+this.peakTriangles+", pending frames="+this.pendingFrames+", quality-miss frames="+this.qualityMissFrames+", latest feedback quality misses="+final.QualityTargetMisses+", overflow="+final.QueueOverflow+", evictions="+final.Evictions+", unsafe reuse="+final.UnsafeSlotReuses);
            uint[] before=this.First.ReadbackSelectionForDiagnostics(this.Primary).Arguments;this.Capture("OrthographicCamera",this.Secondary,true);uint[] after=this.First.ReadbackSelectionForDiagnostics(this.Primary).Arguments;
            Require(Equal(before,after)&&this.First.DiagnosticCameraStateCount==2&&this.Second.DiagnosticCameraStateCount==2,"Player camera state overwrite.");this.results.Add("Independent perspective/orthographic camera states=2 per instance; earlier arguments preserved.");
            this.Primary.transform.position=new Vector3(0,0,-20);this.First.SetFallbackOverride(false,true);this.Second.SetFallbackOverride(true);Color32[] unsupported=this.Capture("ForcedUnsupported",this.Primary,true);
            Require(this.First.IsFallbackVisible&&this.Second.IsFallbackVisible&&Difference(proxyLit,unsupported)==0,"Player forced fallback failed or double drew.");this.results.Add("Forced unsupported fallback: exact proxy image; reason="+this.First.DiagnosticReason);
            for(int cycle=0;cycle<3;cycle++){
                this.First.enabled=false;Require(VirtualGeometryRenderer.SharedResourceCount==1,"First release disposed shared geometry.");this.Second.enabled=false;Require(VirtualGeometryRenderer.SharedResourceCount==0,"Final release leaked resources.");yield return null;
                this.First.enabled=true;this.Second.enabled=true;Require(this.First.SharesStreamingResourceWith(this.Second)&&VirtualGeometryRenderer.SharedResourceCount==1,"Re-enable failed to share resources.");
                this.Capture("Lifecycle",this.Primary,false);yield return null;
            }
            this.First.enabled=false;this.Second.enabled=false;yield return null;Require(VirtualGeometryRenderer.SharedResourceCount==0,"Exit leaked shared resources.");this.results.Add("Enable/disable lifecycle cycles=3; final resources=0; cancellation during bootstrap exercised.");
            if(this.errors.Count>0){throw new InvalidOperationException(string.Join(" | ",this.errors));}
        }
        private void Record(){
            var s=this.First.StreamingStatistics;Require(s!=null,"Streaming statistics unavailable.");this.peakResidentPages=Math.Max(this.peakResidentPages,s.Resident);
            Require(s.GpuAllocatedBytes<=this.GpuBudget&&s.GpuResidentBytes<=this.GpuBudget&&s.CpuBytes<=this.CpuBudget&&s.TransferBytesThisFrame<=this.TransferCap&&s.PeakIoConcurrency<=this.IoCap&&s.UnsafeSlotReuses==0,"Player exceeded a resource cap or reused an in-flight slot.");
            long drawn=this.First.IsFallbackVisible?this.First.MeshAsset.ProxyTriangleCount:this.First.ReadMetricsForDiagnostics(this.Primary).DrawnTriangles;
            this.metrics.Add(string.Join(",",Time.frameCount,this.First.IsStreamingRootReady?1:0,this.First.IsFallbackVisible?1:0,drawn,s.GpuAllocatedBytes,s.GpuResidentBytes,s.CpuBytes,s.IoBytes,s.Queued,s.Loading,s.Uploading,s.Resident,s.Failed,s.TransferBytesThisFrame,s.Misses,s.QueueOverflow,s.Evictions,s.QualityTargetMisses,s.UnsafeSlotReuses));
        }
        private Color32[] Capture(string name,Camera camera,bool save){
            const int width=640,height=480;RenderTexture target=RenderTexture.GetTemporary(width,height,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear,1);RenderTexture old=camera.targetTexture,active=RenderTexture.active;Texture2D image=null;
            try{
                camera.targetTexture=target;var request=new RenderPipeline.StandardRequest{destination=target};Require(RenderPipeline.SupportsRenderRequest(camera,request),"Player StandardRequest unsupported.");RenderPipeline.SubmitRenderRequest(camera,request);
                RenderTexture.active=target;image=new Texture2D(width,height,TextureFormat.RGBA32,false,true);image.ReadPixels(new Rect(0,0,width,height),0,0);image.Apply();if(save){File.WriteAllBytes(Path.Combine(this.output,name+".png"),image.EncodeToPNG());}return image.GetPixels32();
            }finally{camera.targetTexture=old;RenderTexture.active=active;RenderTexture.ReleaseTemporary(target);if(image!=null){Object.Destroy(image);}}
        }
        private static int Difference(Color32[] a,Color32[] b){int count=0;for(int i=0;i<a.Length;i++){if(a[i].r!=b[i].r||a[i].g!=b[i].g||a[i].b!=b[i].b){count++;}}return count;}
        private static bool Equal(uint[] a,uint[] b){if(a.Length!=b.Length){return false;}for(int i=0;i<a.Length;i++){if(a[i]!=b[i]){return false;}}return true;}
        private static void Require(bool condition,string reason){if(!condition){throw new InvalidOperationException(reason);}}
        [Serializable] private sealed class Report{public bool passed;public string[] observations;public string[] errors;public string failure;}
        private void Finish(Exception error){
            if(this.finished){return;}this.finished=true;Application.logMessageReceived-=this.Log;this.First.enabled=false;this.Second.enabled=false;
            var report=new Report{passed=error==null,observations=this.results.ToArray(),errors=this.errors.ToArray(),failure=error?.ToString()};
            File.WriteAllText(Path.Combine(this.output,"Results.json"),JsonUtility.ToJson(report,true));File.WriteAllText(Path.Combine(this.output,"Results.txt"),string.Join("\n",this.results)+"\n"+(error==null?"PASS":error.ToString()));File.WriteAllLines(Path.Combine(this.output,"Metrics.csv"),this.metrics);
            if(error==null){Debug.Log("M5 Player validation PASS");}else{Debug.LogException(error);}Application.Quit(error==null?0:1);
        }
    }
}