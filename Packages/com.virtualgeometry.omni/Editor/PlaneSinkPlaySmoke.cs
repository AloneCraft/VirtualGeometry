using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VirtualizedGeometry.Editor;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// PlaneSink デモ scene を Play Mode で動かして確かめる(batchmode)。
    /// scene を開き、Play Mode に入り、<see cref="PlaneSinkDemo"/> が自分で初期化(Update)してから、キーを <see cref="IPlaneSinkKeys"/> の差し替えで送り、
    /// キーごとの量と向き(沈める・浮かせる、水平の移動、周回、距離、領域、全体、自動、初期化、D の切り替え)と、camera が常にボールの中心を向くこと(demo 自身の状態からの計算)、エラーが出ないことを確かめる。
    /// 焦点の無い batchmode では Input System の仮想キーボードのイベントが届かないので、実際のキーボードとマウスから Input System を通る経路は確かめない(MoonBallPlaySmoke と同じ)。
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.PlaneSinkPlaySmoke.Run
    /// </summary>
    public static class PlaneSinkPlaySmoke{
        private const string Results="TestResults/Planet/PlaneSinkPlay";
        private static int frames;
        private static int phase;
        private static int caseIndex;
        private static int caseFrame;
        private static PlaneSinkDemo demo;
        private static global::Omni.Logical.Transform cameraLogical;
        private static double started;
        private static Snapshot before;
        private static double worstCenterAngle;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();
        private static readonly ScriptedKeys scripted=new ScriptedKeys();

        private sealed class Snapshot{
            public long X,Y,Z;
            public float Yaw,Pitch,Distance;
            public PlaneSinkDemo.Area Area;
            public double OriginDistance;
            public bool Auto;
        }

        // 1 つの確認: 準備 → キーを押す(hold frame の間、押したまま)→ wait frame 待つ → 結果を調べる(失敗なら理由、成功なら null)。
        private sealed class Case{
            public string Name;
            public PlaneSinkKey[] Keys;
            public int Hold;
            public int Wait;
            public Action Prepare;
            public Func<Snapshot,Snapshot,string> Check;
        }

        /// <summary>押した frame だけ押されている入力元。Tap で押し、Clear で離す。PressedThisFrame は最初に聞かれたときだけ true。</summary>
        private sealed class ScriptedKeys:IPlaneSinkKeys{
            private readonly HashSet<PlaneSinkKey> held=new HashSet<PlaneSinkKey>();
            private readonly HashSet<PlaneSinkKey> fresh=new HashSet<PlaneSinkKey>();
            public Vector2 PendingLook;
            public float PendingZoom;

            public void Tap(PlaneSinkKey key){
                this.held.Add(key);this.fresh.Add(key);
            }

            public void Clear(){
                this.held.Clear();this.fresh.Clear();
            }

            public bool Held(PlaneSinkKey key){return this.held.Contains(key);}
            public bool PressedThisFrame(PlaneSinkKey key){return this.fresh.Remove(key);}

            public Vector2 LookDelta(){
                Vector2 value=this.PendingLook;this.PendingLook=Vector2.zero;
                return value;
            }

            public float ZoomDelta(){
                float value=this.PendingZoom;this.PendingZoom=0f;
                return value;
            }
        }

        public static void Run(){
            frames=0;phase=0;caseIndex=0;caseFrame=0;demo=null;worstCenterAngle=0;started=EditorApplication.timeSinceStartup;
            report.Clear();failures.Clear();errors.Clear();
            Directory.CreateDirectory(Results);
            Application.logMessageReceived+=Record;
            try{
                MoonBallSceneBuilder.UsePipeline();
                EditorSceneManager.OpenScene(PlaneSinkSceneBuilder.ScenePath,OpenSceneMode.Single);
                // domain reload を止めて、static の状態を Play Mode の間も保つ。
                EditorSettings.enterPlayModeOptionsEnabled=true;
                EditorSettings.enterPlayModeOptions=EnterPlayModeOptions.DisableDomainReload|EnterPlayModeOptions.DisableSceneReload;
                EditorApplication.update+=Tick;
                EditorApplication.EnterPlaymode();
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

        private static Snapshot Take(){
            return new Snapshot{X=demo.OffsetX,Y=demo.OffsetY,Z=demo.OffsetZ,Yaw=demo.CameraYaw,Pitch=demo.CameraPitch,Distance=demo.CameraDistance,
                Area=demo.CurrentArea,OriginDistance=demo.Layout.OriginDistance,Auto=demo.AutoPlay};
        }

        private static Case[] BuildCases(){
            Action defaultCamera=()=>{demo.AutoPlay=false;demo.SetState(PlaneSinkDemo.Area.Left,0,0,0,0f,20f,0.35f);};
            return new[]{
                // 沈める・浮かせる(0.1mm 単位)。押した 1 回で、ちょうど 1 回分動く。
                Quantity("Up (raise 1mm)",new[]{PlaneSinkKey.Raise},10,defaultCamera),
                Quantity("Down (sink 1mm)",new[]{PlaneSinkKey.Sink},-10,defaultCamera),
                Quantity("Shift+Up (10mm)",new[]{PlaneSinkKey.Coarse,PlaneSinkKey.Raise},100,defaultCamera),
                Quantity("Shift+Down (-10mm)",new[]{PlaneSinkKey.Coarse,PlaneSinkKey.Sink},-100,defaultCamera),
                Quantity("Ctrl+Up (0.1mm)",new[]{PlaneSinkKey.Fine,PlaneSinkKey.Raise},1,defaultCamera),
                // Shift と Ctrl を同時に押したときは、Ctrl(0.1mm)が優先される。
                Quantity("Shift+Ctrl+Up (0.1mm)",new[]{PlaneSinkKey.Coarse,PlaneSinkKey.Fine,PlaneSinkKey.Raise},1,defaultCamera),
                // 水平の移動は camera の yaw が基準(yaw 0 で前 = +Z、右 = +X)。
                Motion("W (yaw 0: +Z)",PlaneSinkKey.Forward,0f,(a,b)=>b.Z>a.Z&&b.X==a.X&&b.Y==a.Y),
                Motion("S (yaw 0: -Z)",PlaneSinkKey.Back,0f,(a,b)=>b.Z<a.Z&&b.X==a.X&&b.Y==a.Y),
                Motion("D (yaw 0: +X)",PlaneSinkKey.Right,0f,(a,b)=>b.X>a.X&&b.Z==a.Z&&b.Y==a.Y),
                Motion("A (yaw 0: -X)",PlaneSinkKey.Left,0f,(a,b)=>b.X<a.X&&b.Z==a.Z&&b.Y==a.Y),
                Motion("W (yaw 90: +X)",PlaneSinkKey.Forward,90f,(a,b)=>b.X>a.X&&b.Z==a.Z&&b.Y==a.Y),
                // camera の周回と距離
                Orbit("Left arrow (yaw -)",PlaneSinkKey.YawLeft,(a,b)=>b.Yaw<a.Yaw),
                Orbit("Right arrow (yaw +)",PlaneSinkKey.YawRight,(a,b)=>b.Yaw>a.Yaw),
                Orbit("PageUp (pitch +)",PlaneSinkKey.PitchUp,(a,b)=>b.Pitch>a.Pitch),
                Orbit("PageDown (pitch -)",PlaneSinkKey.PitchDown,(a,b)=>b.Pitch<a.Pitch),
                Orbit("Home (closer)",PlaneSinkKey.ZoomIn,(a,b)=>b.Distance<a.Distance),
                Orbit("End (farther)",PlaneSinkKey.ZoomOut,(a,b)=>b.Distance>a.Distance),
                new Case{Name="Right-drag (yaw +2 deg)",Keys=new PlaneSinkKey[0],Hold=1,Wait=3,Prepare=()=>{defaultCamera();scripted.PendingLook=new Vector2(10,0);},
                    Check=(a,b)=>{if(Mathf.Abs((b.Yaw-a.Yaw)-2f)<0.01f){return null;}return "yaw が "+(b.Yaw-a.Yaw)+" 度動きました(期待 +2)。";}},
                new Case{Name="Wheel up (closer)",Keys=new PlaneSinkKey[0],Hold=1,Wait=3,Prepare=()=>{defaultCamera();scripted.PendingZoom=1f;},
                    Check=(a,b)=>{if(b.Distance<a.Distance){return null;}return "距離が縮みませんでした。";}},
                // 領域・全体・自動・初期化・D の切り替え
                new Case{Name="2 (right area)",Keys=new[]{PlaneSinkKey.AreaRight},Hold=2,Wait=3,Prepare=defaultCamera,
                    Check=(a,b)=>{if(b.Area==PlaneSinkDemo.Area.Right){return null;}return "右の領域になりませんでした。";}},
                new Case{Name="1 (left area)",Keys=new[]{PlaneSinkKey.AreaLeft},Hold=2,Wait=3,Prepare=()=>{defaultCamera();demo.SetArea(PlaneSinkDemo.Area.Right);},
                    Check=(a,b)=>{if(b.Area==PlaneSinkDemo.Area.Left){return null;}return "左の領域になりませんでした。";}},
                new Case{Name="3 (overview)",Keys=new[]{PlaneSinkKey.Overview},Hold=2,Wait=3,Prepare=defaultCamera,
                    Check=(a,b)=>{if(Mathf.Abs(b.Pitch-35f)<1e-3f&&Mathf.Abs(b.Distance-12f)<1e-3f){return null;}return "全体の視点(pitch 35、距離 12)になりませんでした: "+b.Pitch+"、"+b.Distance+"。";}},
                new Case{Name="Space (auto on, then the ball moves)",Keys=new[]{PlaneSinkKey.Auto},Hold=2,Wait=90,Prepare=defaultCamera,
                    Check=(a,b)=>{if(b.Auto&&b.Y!=a.Y){return null;}return "自動で沈めなかった(auto="+b.Auto+"、y "+a.Y+" → "+b.Y+")。";}},
                new Case{Name="Space (auto off)",Keys=new[]{PlaneSinkKey.Auto},Hold=2,Wait=3,Prepare=null,
                    Check=(a,b)=>{if(!b.Auto){return null;}return "自動が止まりませんでした。";}},
                new Case{Name="R (reset)",Keys=new[]{PlaneSinkKey.Reset},Hold=2,Wait=3,Prepare=()=>{demo.AutoPlay=true;demo.SetState(PlaneSinkDemo.Area.Left,30,-200,40,33f,50f,2f);},
                    Check=(a,b)=>{if(!b.Auto&&b.X==0&&b.Y==0&&b.Z==0&&b.Yaw==0f&&Mathf.Abs(b.Pitch-20f)<1e-3f&&Mathf.Abs(b.Distance-0.35f)<1e-3f){return null;}return "初期化されませんでした("+b.X+","+b.Y+","+b.Z+" yaw "+b.Yaw+" pitch "+b.Pitch+" 距離 "+b.Distance+" auto "+b.Auto+")。";}},
                new Case{Name="[ (D smaller)",Keys=new[]{PlaneSinkKey.DistanceDown},Hold=2,Wait=3,Prepare=()=>{defaultCamera();demo.SetLayout(PlaneSinkLayout.Default);},
                    Check=(a,b)=>{if(b.OriginDistance<a.OriginDistance){return null;}return "D が小さくなりませんでした("+a.OriginDistance+" → "+b.OriginDistance+")。";}},
                new Case{Name="] (D larger)",Keys=new[]{PlaneSinkKey.DistanceUp},Hold=2,Wait=3,Prepare=()=>{defaultCamera();demo.SetLayout(PlaneSinkLayout.Default);},
                    Check=(a,b)=>{if(b.OriginDistance>a.OriginDistance){return null;}return "D が大きくなりませんでした("+a.OriginDistance+" → "+b.OriginDistance+")。";}},
            };
        }

        private static Case Quantity(string name,PlaneSinkKey[] keys,long expectedY,Action prepare){
            return new Case{Name=name,Keys=keys,Hold=2,Wait=3,Prepare=prepare,
                Check=(a,b)=>{
                    long x=b.X-a.X,y=b.Y-a.Y,z=b.Z-a.Z;
                    if(x==0&&y==expectedY&&z==0){return null;}
                    return "量が違います: ("+x+","+y+","+z+")、期待 (0,"+expectedY+",0)。";
                }};
        }

        private static Case Motion(string name,PlaneSinkKey key,float yaw,Func<Snapshot,Snapshot,bool> ok){
            return new Case{Name=name,Keys=new[]{key},Hold=20,Wait=3,
                Prepare=()=>{demo.AutoPlay=false;demo.SetState(PlaneSinkDemo.Area.Left,0,0,0,yaw,20f,0.35f);},
                Check=(a,b)=>{
                    if(ok(a,b)){return null;}
                    return "動きが違います: ("+(b.X-a.X)+","+(b.Y-a.Y)+","+(b.Z-a.Z)+")。";
                }};
        }

        private static Case Orbit(string name,PlaneSinkKey key,Func<Snapshot,Snapshot,bool> ok){
            return new Case{Name=name,Keys=new[]{key},Hold=20,Wait=3,
                Prepare=()=>{demo.AutoPlay=false;demo.SetState(PlaneSinkDemo.Area.Left,0,0,0,0f,20f,0.35f);},
                Check=(a,b)=>{
                    if(ok(a,b)){return null;}
                    return "camera の動きが違います(yaw "+a.Yaw+" → "+b.Yaw+"、pitch "+a.Pitch+" → "+b.Pitch+"、距離 "+a.Distance+" → "+b.Distance+")。";
                }};
        }

        private static void Tick(){
            try{
                if(EditorApplication.timeSinceStartup-started>600){throw new TimeoutException("PlaneSink play smoke timeout.");}
                if(!EditorApplication.isPlaying){return;}
                frames++;
                if(phase==0){
                    demo=UnityEngine.Object.FindAnyObjectByType<PlaneSinkDemo>();
                    if(demo==null){throw new InvalidOperationException("scene に PlaneSinkDemo がありません。");}
                    if(demo.IsInitialized||frames>600){
                        report.Add("initialized="+demo.IsInitialized+" after "+frames+" frames failure="+(demo.Failure??"(none)"));
                        if(!demo.IsInitialized){failures.Add("PlaneSinkDemo が初期化されませんでした: "+demo.Failure);phase=99;return;}
                        cameraLogical=demo.GetComponent<global::Omni.Logical.Transform>();
                        phase=1;frames=0;
                    }
                }else if(phase==1){
                    // batchmode には Game View が無く、camera は自動では描かれない(選択と生成は描画のたびに進む)ので、毎 frame 撮って描かせる。
                    if(frames!=240){Capture(320,240);}
                    if(frames==240){
                        Save("PlayStart",Capture());
                        demo.Keys=scripted;
                        caseIndex=0;caseFrame=0;phase=2;
                    }
                }else if(phase==2){
                    Capture(320,240);
                    RunCase();
                }
                if(phase==99){Finish(null);}
            }catch(Exception error){
                Finish(error);
            }
        }

        private static Case[] cases;

        private static void RunCase(){
            if(cases==null){cases=BuildCases();}
            if(caseIndex>=cases.Length){
                // demo 自身の状態(camera の回転とボールの位置)から計算した角度。描かれた画像でのボールの重心は PlaneSinkValidation が調べる。
                report.Add("camera aims at the ball (from the demo's own state): worst angle="+(worstCenterAngle*1e6).ToString("G3",CultureInfo.InvariantCulture)+" urad");
                phase=99;return;
            }
            Case item=cases[caseIndex];
            if(caseFrame==0){
                scripted.Clear();
                if(item.Prepare!=null){item.Prepare();}
                before=Take();
                foreach(PlaneSinkKey key in item.Keys){scripted.Tap(key);}
            }else if(caseFrame==item.Hold){
                scripted.Clear();
            }else if(caseFrame==item.Hold+item.Wait){
                Snapshot after=Take();
                string reason=item.Check(before,after);
                double angle=CenterAngle();
                worstCenterAngle=Math.Max(worstCenterAngle,angle);
                bool ok=reason==null&&angle<1e-4;
                string verdict="NG";
                if(ok){verdict="ok";}
                report.Add("key "+item.Name+": "+Describe(before,after)+" centerAngle="+(angle*1e6).ToString("G3",CultureInfo.InvariantCulture)+"urad "+verdict);
                if(reason!=null){failures.Add("キー "+item.Name+": "+reason);}
                if(angle>=1e-4){failures.Add("キー "+item.Name+": ボールが画面の中央から "+angle+" rad ずれています。");}
                caseIndex++;caseFrame=0;
                return;
            }
            caseFrame++;
        }

        private static string Describe(Snapshot a,Snapshot b){
            return "offset ("+a.X+","+a.Y+","+a.Z+")->("+b.X+","+b.Y+","+b.Z+") yaw "+a.Yaw.ToString("0.0",CultureInfo.InvariantCulture)+"->"+b.Yaw.ToString("0.0",CultureInfo.InvariantCulture)+
                " pitch "+a.Pitch.ToString("0.0",CultureInfo.InvariantCulture)+"->"+b.Pitch.ToString("0.0",CultureInfo.InvariantCulture)+
                " distance "+a.Distance.ToString("0.000",CultureInfo.InvariantCulture)+"->"+b.Distance.ToString("0.000",CultureInfo.InvariantCulture)+" area "+b.Area+" D "+b.OriginDistance.ToString("G3",CultureInfo.InvariantCulture);
        }

        // camera の前方と、camera からボールの中心への向きの角度(rad。ボールが画面の中央にあれば 0)。
        private static double CenterAngle(){
            if(!demo.TryGetBallCenter(out LogicalVector3 center)||!LogicalVector3.TrySubtract(in center,cameraLogical.LocalPosition,out LogicalVector3 difference)||!difference.TryToFloat3(out float3 toBall)){return double.NaN;}
            Vector3 forward=demo.CameraRotation*Vector3.forward;
            // Vector3.Angle は acos(正規化した内積)で、float では内積が 1 の 1 ulp 下に丸まると sqrt(2·2^-24) ≒ 345 µrad になる(向きが完全に揃っていても)。
            // 判定の閾値(100 µrad)より大きいので、yaw の値しだいで NG が出たり出なかったりした。atan2(|外積|, 内積)を倍精度で求める(角度が 0 に近くても精度が落ちない)。
            double3 f=new double3(forward.x,forward.y,forward.z);
            double3 t=new double3(toBall.x,toBall.y,toBall.z);
            return Math.Atan2(math.length(math.cross(f,t)),math.dot(f,t));
        }

        private static Texture2D Capture(int width=640,int height=480){
            Camera camera=Camera.main;
            if(camera==null){throw new InvalidOperationException("Main Camera がありません。");}
            return M0Validation.Capture(camera,width,height);
        }

        private static void Save(string name,Texture2D image){
            File.WriteAllBytes(Results+"/"+name+".png",image.EncodeToPNG());
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Record;
            if(demo!=null){demo.Keys=null;}
            cases=null;
            if(error!=null){failures.Add(error.ToString());}
            foreach(string message in errors){failures.Add("Unity logged error: "+message);}
            report.Add("UTC="+DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+" Unity="+Application.unityVersion+" GPU="+SystemInfo.graphicsDeviceName);
            if(failures.Count==0){report.Add("PASSED");}else{report.Add("FAILED");report.AddRange(failures);}
            Directory.CreateDirectory(Results);
            File.WriteAllLines(Results+"/Report.txt",report);
            int code=1;
            if(failures.Count==0){code=0;}
            EditorApplication.Exit(code);
        }
    }
}
