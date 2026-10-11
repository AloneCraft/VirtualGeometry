using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using VirtualGeometry.World;

namespace VirtualGeometry.Omni{
    /// <summary>
    /// ビルドした Player で BodyStack(<see cref="BodyStackDemo"/>)を動かして確かめる。Editor では何もしない。引数 -planetResults &lt;folder&gt; が無いときも何もしない。
    /// 4 つの視点(接点 3、2、1、全体)で、選択が収束するのを待ち、120 frame の時間(CPU の frame 間隔、<see cref="FrameTimingManager"/> の CPU・GPU の時間)と
    /// 画素(背景色を目立つ色にして、接触の視点で水平より下を向く光線の画素に背景が出ていないか。camera の描画を RenderTexture へ)を記録し、PlayerReport.txt を書いて Application.Quit(終了コード)する。
    /// エラーのログ、設定エラー、初期化の失敗、収束しないこと、地面の穴は不合格。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlanetPlayerSmoke:MonoBehaviour{
        private const int ConvergeLimit=900;
        private const int StableFrames=5;
        private const int MeasureFrames=120;
        private const int InitializeLimit=900;

        private readonly List<string> report=new List<string>();
        private readonly List<string> failures=new List<string>();
        private readonly List<string> errors=new List<string>();
        private readonly List<string> ignored=new List<string>();
        private string results;
        private BodyStackDemo demo;
        private Camera view;
        private bool finished;

        private void Start(){
            if(Application.isEditor){return;}
            this.results=ReadArgument("-planetResults");
            if(string.IsNullOrEmpty(this.results)){return;}
            this.demo=this.GetComponent<BodyStackDemo>();
            this.view=this.GetComponent<Camera>();
            if(this.demo==null||this.view==null){
                this.failures.Add("BodyStackDemo または Camera がありません。");
                this.Finish();
                return;
            }
            Application.logMessageReceived+=this.Record;
            QualitySettings.vSyncCount=0;
            Application.targetFrameRate=-1;
            // 背景を目立つ色にして、地面があるはずの所に背景が出ていないか(穴)を見る。
            this.view.backgroundColor=new Color(1,0,1,1);
            this.StartCoroutine(this.Steps());
        }

        private void OnDestroy(){
            Application.logMessageReceived-=this.Record;
        }

        private static string ReadArgument(string name){
            string[] arguments=Environment.GetCommandLineArgs();
            for(int i=0;i<arguments.Length-1;i++){
                if(arguments[i]==name){return arguments[i+1];}
            }
            return null;
        }

        private void Record(string condition,string stack,LogType type){
            if(type!=LogType.Error&&type!=LogType.Exception&&type!=LogType.Assert){return;}
            // Omni の Scene BRG(通常の Renderer を一括で描く経路)は、Renderer が 1 つも無い scene では manifest が無く、Player で LogError を出す(Omni 側の診断。VG の描画とは無関係)。
            // 無視した数は報告に書く。
            if(condition.Contains("[Omni][Scene BRG] No active Scene BRG manifest entry is available.")){this.ignored.Add(condition);return;}
            this.errors.Add(condition);
        }

        private IEnumerator Steps(){
            this.report.Add("player: unity="+Application.unityVersion+" development="+Debug.isDebugBuild+" gpu="+SystemInfo.graphicsDeviceName+" api="+SystemInfo.graphicsDeviceType+" screen="+Screen.width+"x"+Screen.height+
                " frameTiming="+FrameTimingManager.IsFeatureEnabled()+" gpuRecorder="+SystemInfo.supportsGpuRecorder+" maxQueuedFrames="+QualitySettings.maxQueuedFrames+" vSync="+QualitySettings.vSyncCount);
            // BodyStackDemo が自分で初期化するのを待つ。
            int waited=0;
            while(!this.demo.IsInitialized){
                if(this.demo.Failure!=null){this.failures.Add("初期化に失敗: "+this.demo.Failure);this.Finish();yield break;}
                if(waited++>InitializeLimit){this.failures.Add("BodyStackDemo が "+InitializeLimit+" frame で初期化されません。");this.Finish();yield break;}
                yield return null;
            }
            this.report.Add("initialized after "+waited+" frames");
            for(int step=0;step<4;step++){
                string name;
                bool contact=step<3;
                if(contact){
                    int number=3-step;
                    name="Contact"+number;
                    this.demo.ShowContactView(number);
                }else{
                    name="Overview";
                    this.demo.ShowOverview();
                }
                // 選択が収束するまで待つ。
                int stable=0;int previous=-1;int frames=0;
                while(frames<ConvergeLimit){
                    yield return null;
                    frames++;
                    int signature=0;int pending=0;int notReady=0;
                    PlanetTerrainSource[] bodies=this.demo.Bodies;
                    for(int i=0;i<bodies.Length;i++){
                        signature=signature*31+bodies[i].LastSelected.Count;
                        foreach(PlanetCellKey key in bodies[i].LastSelected){signature^=key.GetHashCode()*(i+1);}
                        pending+=bodies[i].PendingGeneration;
                        notReady+=bodies[i].LastNotReadyCells;
                    }
                    if(pending==0&&notReady==0&&signature==previous){stable++;}else{stable=0;}
                    previous=signature;
                    if(stable>=StableFrames){break;}
                }
                if(frames>=ConvergeLimit){this.failures.Add(name+": "+ConvergeLimit+" frame で選択が収束しませんでした。");}
                // 時間
                var cpuSamples=new List<float>();var mainSamples=new List<float>();var renderSamples=new List<float>();var gpuSamples=new List<float>();
                var frameTimes=new List<float>();
                var timings=new FrameTiming[1];
                int drawn=0;int skipped=0;
                for(int frame=0;frame<MeasureFrames;frame++){
                    yield return null;
                    frameTimes.Add(Time.unscaledDeltaTime*1000f);
                    FrameTimingManager.CaptureFrameTimings();
                    if(FrameTimingManager.GetLatestTimings(1,timings)>0){
                        cpuSamples.Add((float)timings[0].cpuFrameTime);mainSamples.Add((float)timings[0].cpuMainThreadFrameTime);
                        renderSamples.Add((float)timings[0].cpuRenderThreadFrameTime);gpuSamples.Add((float)timings[0].gpuFrameTime);
                    }
                }
                PlanetTerrainSource[] sources=this.demo.Bodies;
                var counts=new List<string>();
                for(int i=0;i<sources.Length;i++){
                    counts.Add(sources[i].LastSelected.Count.ToString(CultureInfo.InvariantCulture));
                    drawn+=sources[i].LastDrawnCells;skipped+=sources[i].LastSkippedCells;
                    if(sources[i].LastError!=null){this.failures.Add(name+": 設定エラー(天体 "+i+"): "+sources[i].LastError);}
                    if(sources[i].LastNotReadyCells!=0){this.failures.Add(name+": 天体 "+i+" の描けなかった Cell が "+sources[i].LastNotReadyCells+" あります。");}
                }
                // 画面の画素(HUD や Development Console を含まない、camera の描画を RenderTexture へ)。
                yield return new WaitForEndOfFrame();
                Texture2D image=this.Capture(Screen.width,Screen.height);
                Color32[] pixels=image.GetPixels32();
                int width=image.width;int height=image.height;
                // 穴: 接触の視点では camera は面の上にあるので、水平より 2 画素以上下を向く光線の画素は、地面でなければならない(背景なら穴)。
                // 背景の画素の光線と、上向き(demo.Up。天体は回転しない)との内積で判定する。全体の視点(積んだ天体の外)では判定しない。
                int background=0;int belowSurface=0;
                float tangent=Mathf.Tan(this.view.fieldOfView*0.5f*Mathf.Deg2Rad);
                float aspect=(float)width/height;
                float pixelAngle=this.view.fieldOfView*Mathf.Deg2Rad/height;
                float limit=-Mathf.Sin(2f*pixelAngle);
                Vector3 up=this.demo.Up;
                Quaternion rotation=this.view.transform.rotation;
                for(int y=0;y<height;y++){
                    for(int x=0;x<width;x++){
                        Color32 pixel=pixels[y*width+x];
                        if(!(pixel.r>200&&pixel.g<60&&pixel.b>200)){continue;}
                        background++;
                        if(!contact){continue;}
                        Vector3 local=new Vector3(((x+0.5f)/width*2f-1f)*tangent*aspect,((y+0.5f)/height*2f-1f)*tangent,1f);
                        Vector3 direction=(rotation*local).normalized;
                        if(Vector3.Dot(direction,up)<limit){belowSurface++;}
                    }
                }
                File.WriteAllBytes(Path.Combine(this.results,"PlayerView_"+name+".png"),image.EncodeToPNG());
                Destroy(image);
                if(belowSurface!=0){this.failures.Add(name+": 水平より下を向く光線の画素に、背景が "+belowSurface+" あります(穴)。");}
                if(background>=width*height){this.failures.Add(name+": 全画素が背景です。");}
                this.report.Add(name+": convergedFrames="+frames+" cells(selected)="+string.Join("/",counts)+" drawn="+drawn+" skipped="+skipped+" pixels="+width+"x"+height+" background="+background+" belowSurface="+belowSurface);
                this.report.Add(name+": frameInterval(ms) "+Describe(frameTimes)+" | cpuFrame "+Describe(cpuSamples)+" | cpuMain "+Describe(mainSamples)+" | cpuRender "+Describe(renderSamples)+" | gpu "+Describe(gpuSamples));
            }
            this.Finish();
        }

        // camera の描画を RenderTexture へ(URP の StandardRequest)。画面の IMGUI や Development Console は含まない。
        private Texture2D Capture(int width,int height){
            RenderTexture target=RenderTexture.GetTemporary(width,height,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear,1);
            RenderTexture previous=RenderTexture.active;
            RenderTexture oldTarget=this.view.targetTexture;
            try{
                this.view.targetTexture=target;
                var request=new RenderPipeline.StandardRequest{destination=target};
                if(!RenderPipeline.SupportsRenderRequest(this.view,request)){throw new InvalidOperationException("Active pipeline does not support StandardRequest.");}
                RenderPipeline.SubmitRenderRequest(this.view,request);
                RenderTexture.active=target;
                var result=new Texture2D(width,height,TextureFormat.RGBA32,false,true);
                result.ReadPixels(new Rect(0,0,width,height),0,0);
                result.Apply();
                return result;
            }finally{
                this.view.targetTexture=oldTarget;
                RenderTexture.active=previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        // 平均・中央値・最大(ms)。標本が無ければ "n=0"。
        private static string Describe(List<float> samples){
            if(samples.Count==0){return "n=0";}
            var sorted=new List<float>(samples);sorted.Sort();
            float sum=0;
            for(int i=0;i<sorted.Count;i++){sum+=sorted[i];}
            return "n="+sorted.Count+" mean="+(sum/sorted.Count).ToString("F2",CultureInfo.InvariantCulture)+" median="+sorted[sorted.Count/2].ToString("F2",CultureInfo.InvariantCulture)+" max="+sorted[sorted.Count-1].ToString("F2",CultureInfo.InvariantCulture);
        }

        private void Finish(){
            if(this.finished){return;}
            this.finished=true;
            foreach(string message in this.errors){this.failures.Add("Unity logged error: "+message);}
            if(this.ignored.Count>0){this.report.Add("ignored known Omni diagnostics: "+this.ignored.Count+" x "+this.ignored[0]);}
            this.report.Add("UTC="+DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture));
            if(this.failures.Count==0){this.report.Add("PASSED");}else{this.report.Add("FAILED");this.report.AddRange(this.failures);}
            Directory.CreateDirectory(this.results);
            File.WriteAllLines(Path.Combine(this.results,"PlayerReport.txt"),this.report);
            int code=1;
            if(this.failures.Count==0){code=0;}
            Application.Quit(code);
        }
    }
}
