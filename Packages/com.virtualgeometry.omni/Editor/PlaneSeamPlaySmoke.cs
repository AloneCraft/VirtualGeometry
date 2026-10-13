using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VirtualizedGeometry.Editor;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// PlaneSeam デモ scene を Play Mode で動かして確かめる(batchmode)。
    /// scene を開き、Play Mode に入り、<see cref="PlaneSeamDemo"/> が自分で初期化(Update)してから、キーを <see cref="IPlaneSeamKeys"/> の差し替えで送り、
    /// キーごとの量と向き(右の面の上げ下げ、camera の移動・向き、視点、初期化、D の切り替え)と、エラーが出ないことを確かめる。
    /// 焦点の無い batchmode では Input System の仮想キーボードのイベントが届かないので、実際のキーボードとマウスから Input System を通る経路は確かめない(PlaneSinkPlaySmoke と同じ)。
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.PlaneSeamPlaySmoke.Run
    /// </summary>
    public static class PlaneSeamPlaySmoke{
        private const string Results="TestResults/Planet/PlaneSeamPlay";
        private static int frames;
        private static int phase;
        private static int caseIndex;
        private static int caseFrame;
        private static PlaneSeamDemo demo;
        private static double started;
        private static Snapshot before;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();
        private static readonly ScriptedKeys scripted=new ScriptedKeys();

        private sealed class Snapshot{
            public long Y;
            public double3 Position;
            public float Yaw,Pitch;
            public double OriginDistance;
        }

        // 1 つの確認: 準備 → キーを押す(hold frame の間、押したまま)→ wait frame 待つ → 結果を調べる(失敗なら理由、成功なら null)。
        private sealed class Case{
            public string Name;
            public PlaneSeamKey[] Keys;
            public int Hold;
            public int Wait;
            public Action Prepare;
            public Func<Snapshot,Snapshot,string> Check;
        }

        /// <summary>押した frame だけ押されている入力元。Tap で押し、Clear で離す。PressedThisFrame は最初に聞かれたときだけ true。</summary>
        private sealed class ScriptedKeys:IPlaneSeamKeys{
            private readonly HashSet<PlaneSeamKey> held=new HashSet<PlaneSeamKey>();
            private readonly HashSet<PlaneSeamKey> fresh=new HashSet<PlaneSeamKey>();
            public Vector2 PendingLook;
            public float PendingZoom;

            public void Tap(PlaneSeamKey key){
                this.held.Add(key);this.fresh.Add(key);
            }

            public void Clear(){
                this.held.Clear();this.fresh.Clear();
            }

            public bool Held(PlaneSeamKey key){return this.held.Contains(key);}
            public bool PressedThisFrame(PlaneSeamKey key){return this.fresh.Remove(key);}

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
            frames=0;phase=0;caseIndex=0;caseFrame=0;demo=null;started=EditorApplication.timeSinceStartup;
            report.Clear();failures.Clear();errors.Clear();
            Directory.CreateDirectory(Results);
            Application.logMessageReceived+=Record;
            try{
                MoonBallSceneBuilder.UsePipeline();
                EditorSceneManager.OpenScene(PlaneSeamSceneBuilder.ScenePath,OpenSceneMode.Single);
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
            return new Snapshot{Y=demo.OffsetY,Position=demo.CameraPosition,Yaw=demo.CameraYaw,Pitch=demo.CameraPitch,OriginDistance=demo.Layout.OriginDistance};
        }

        private static Case[] BuildCases(){
            // 動かさずに camera を既定の視点(継ぎ目)に置く。
            Action defaultView=()=>{demo.SetState(0,new double3(-0.2,0.3,-0.8),0f,20f);};
            return new[]{
                // 右の面の上げ下げ(0.1mm 単位)。押した 1 回で、ちょうど 1 回分動く。
                Quantity("Up (raise 1mm)",new[]{PlaneSeamKey.Raise},10,defaultView),
                Quantity("Down (lower 1mm)",new[]{PlaneSeamKey.Lower},-10,defaultView),
                Quantity("Shift+Up (10mm)",new[]{PlaneSeamKey.Coarse,PlaneSeamKey.Raise},100,defaultView),
                Quantity("Shift+Down (-10mm)",new[]{PlaneSeamKey.Coarse,PlaneSeamKey.Lower},-100,defaultView),
                Quantity("Ctrl+Up (0.1mm)",new[]{PlaneSeamKey.Fine,PlaneSeamKey.Raise},1,defaultView),
                // Shift と Ctrl を同時に押したときは、Ctrl(0.1mm)が優先される。
                Quantity("Shift+Ctrl+Up (0.1mm)",new[]{PlaneSeamKey.Coarse,PlaneSeamKey.Fine,PlaneSeamKey.Raise},1,defaultView),
                // camera の水平の移動は yaw が基準(yaw 0 で前 = +Z、右 = +X)。上下は E、Q。
                Move("W (yaw 0: +Z)",PlaneSeamKey.Forward,0f,(a,b)=>b.Position.z>a.Position.z&&Math.Abs(b.Position.x-a.Position.x)<1e-9&&b.Position.y==a.Position.y),
                Move("S (yaw 0: -Z)",PlaneSeamKey.Back,0f,(a,b)=>b.Position.z<a.Position.z&&Math.Abs(b.Position.x-a.Position.x)<1e-9),
                Move("D (yaw 0: +X)",PlaneSeamKey.Right,0f,(a,b)=>b.Position.x>a.Position.x&&Math.Abs(b.Position.z-a.Position.z)<1e-9),
                Move("A (yaw 0: -X)",PlaneSeamKey.Left,0f,(a,b)=>b.Position.x<a.Position.x&&Math.Abs(b.Position.z-a.Position.z)<1e-9),
                Move("W (yaw 90: +X)",PlaneSeamKey.Forward,90f,(a,b)=>b.Position.x>a.Position.x&&Math.Abs(b.Position.z-a.Position.z)<1e-9),
                Move("E (up)",PlaneSeamKey.Up,0f,(a,b)=>b.Position.y>a.Position.y),
                Move("Q (down, stops at the minimum height)",PlaneSeamKey.Down,0f,(a,b)=>b.Position.y<a.Position.y&&b.Position.y>=PlaneSeamDemo.MinimumHeight-1e-9),
                // camera の向き
                Look("Left arrow (yaw -)",PlaneSeamKey.YawLeft,(a,b)=>b.Yaw<a.Yaw),
                Look("Right arrow (yaw +)",PlaneSeamKey.YawRight,(a,b)=>b.Yaw>a.Yaw),
                Look("PageUp (pitch -: look up)",PlaneSeamKey.PitchUp,(a,b)=>b.Pitch<a.Pitch),
                Look("PageDown (pitch +: look down)",PlaneSeamKey.PitchDown,(a,b)=>b.Pitch>a.Pitch),
                new Case{Name="Right-drag (yaw +2 deg)",Keys=new PlaneSeamKey[0],Hold=1,Wait=3,Prepare=()=>{defaultView();scripted.PendingLook=new Vector2(10,0);},
                    Check=(a,b)=>{if(Mathf.Abs((b.Yaw-a.Yaw)-2f)<0.01f){return null;}return "yaw が "+(b.Yaw-a.Yaw)+" 度動きました(期待 +2)。";}},
                new Case{Name="Wheel up (dolly forward)",Keys=new PlaneSeamKey[0],Hold=1,Wait=3,Prepare=()=>{defaultView();scripted.PendingZoom=1f;},
                    Check=(a,b)=>{if(b.Position.z>a.Position.z){return null;}return "前へ寄りませんでした。";}},
                // 視点・初期化・D の切り替え(視点は camera の位置が変わり、右の面の高さの差は変わらない)
                View("1 (seam view)",PlaneSeamKey.ViewSeam,(a,b)=>Math.Abs(b.Position.x+0.2)<1e-9),
                View("2 (left view)",PlaneSeamKey.ViewLeft,(a,b)=>Math.Abs(b.Position.x-(demo.Layout.LeftSpotX-0.2))<1e-9),
                View("3 (right view)",PlaneSeamKey.ViewRight,(a,b)=>Math.Abs(b.Position.x-(demo.Layout.RightSpotX-0.2))<1e-9),
                new Case{Name="R (reset)",Keys=new[]{PlaneSeamKey.Reset},Hold=2,Wait=3,Prepare=()=>{demo.SetState(250,new double3(5,1,2),33f,50f);},
                    Check=(a,b)=>{if(b.Y==0&&Math.Abs(b.Position.x+0.2)<1e-9&&Math.Abs(b.Position.y-0.3)<1e-9&&Math.Abs(b.Position.z+0.8)<1e-9){return null;}return "初期化されませんでした(δ "+b.Y+"、位置 "+b.Position+")。";}},
                new Case{Name="[ (D smaller)",Keys=new[]{PlaneSeamKey.DistanceDown},Hold=2,Wait=3,Prepare=()=>{defaultView();demo.SetLayout(PlaneSeamLayout.Default);},
                    Check=(a,b)=>{if(b.OriginDistance<a.OriginDistance){return null;}return "D が小さくなりませんでした("+a.OriginDistance+" → "+b.OriginDistance+")。";}},
                new Case{Name="] (D larger)",Keys=new[]{PlaneSeamKey.DistanceUp},Hold=2,Wait=3,Prepare=()=>{defaultView();demo.SetLayout(PlaneSeamLayout.Default);},
                    Check=(a,b)=>{if(b.OriginDistance>a.OriginDistance){return null;}return "D が大きくなりませんでした("+a.OriginDistance+" → "+b.OriginDistance+")。";}},
            };
        }

        private static Case Quantity(string name,PlaneSeamKey[] keys,long expectedY,Action prepare){
            return new Case{Name=name,Keys=keys,Hold=2,Wait=3,Prepare=prepare,
                Check=(a,b)=>{
                    if(b.Y-a.Y==expectedY&&b.Position.Equals(a.Position)){return null;}
                    return "量が違います: δ "+(b.Y-a.Y)+"、期待 "+expectedY+"。";
                }};
        }

        private static Case Move(string name,PlaneSeamKey key,float yaw,Func<Snapshot,Snapshot,bool> ok){
            return new Case{Name=name,Keys=new[]{key},Hold=20,Wait=3,
                Prepare=()=>{demo.SetState(0,new double3(-0.2,0.5,-0.8),yaw,20f);},
                Check=(a,b)=>{
                    if(ok(a,b)&&b.Y==a.Y){return null;}
                    return "動きが違います: "+a.Position+" → "+b.Position+"。";
                }};
        }

        private static Case Look(string name,PlaneSeamKey key,Func<Snapshot,Snapshot,bool> ok){
            return new Case{Name=name,Keys=new[]{key},Hold=20,Wait=3,
                Prepare=()=>{demo.SetState(0,new double3(-0.2,0.5,-0.8),0f,20f);},
                Check=(a,b)=>{
                    if(ok(a,b)){return null;}
                    return "camera の向きが違います(yaw "+a.Yaw+" → "+b.Yaw+"、pitch "+a.Pitch+" → "+b.Pitch+")。";
                }};
        }

        private static Case View(string name,PlaneSeamKey key,Func<Snapshot,Snapshot,bool> ok){
            return new Case{Name=name,Keys=new[]{key},Hold=2,Wait=3,
                Prepare=()=>{demo.SetState(30,new double3(9,2,9),0f,60f);},
                Check=(a,b)=>{
                    if(ok(a,b)&&b.Y==a.Y){return null;}
                    return "視点が違います: "+a.Position+" → "+b.Position+"(δ "+a.Y+" → "+b.Y+")。";
                }};
        }

        private static Case[] cases;

        private static void Tick(){
            try{
                if(EditorApplication.timeSinceStartup-started>600){throw new TimeoutException("PlaneSeam play smoke timeout.");}
                if(!EditorApplication.isPlaying){return;}
                frames++;
                if(phase==0){
                    demo=UnityEngine.Object.FindAnyObjectByType<PlaneSeamDemo>();
                    if(demo==null){throw new InvalidOperationException("scene に PlaneSeamDemo がありません。");}
                    if(demo.IsInitialized||frames>600){
                        report.Add("initialized="+demo.IsInitialized+" after "+frames+" frames failure="+(demo.Failure??"(none)"));
                        if(!demo.IsInitialized){failures.Add("PlaneSeamDemo が初期化されませんでした: "+demo.Failure);phase=99;return;}
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

        private static void RunCase(){
            if(cases==null){cases=BuildCases();}
            if(caseIndex>=cases.Length){
                phase=99;return;
            }
            Case item=cases[caseIndex];
            if(caseFrame==0){
                scripted.Clear();
                if(item.Prepare!=null){item.Prepare();}
                before=Take();
                foreach(PlaneSeamKey key in item.Keys){scripted.Tap(key);}
            }else if(caseFrame==item.Hold){
                scripted.Clear();
            }else if(caseFrame==item.Hold+item.Wait){
                Snapshot after=Take();
                string reason=item.Check(before,after);
                string verdict="NG";
                if(reason==null){verdict="ok";}
                report.Add("key "+item.Name+": δ "+before.Y+"->"+after.Y+" position "+Format(before.Position)+"->"+Format(after.Position)+" yaw "+before.Yaw.ToString("0.0",CultureInfo.InvariantCulture)+"->"+after.Yaw.ToString("0.0",CultureInfo.InvariantCulture)+
                    " pitch "+before.Pitch.ToString("0.0",CultureInfo.InvariantCulture)+"->"+after.Pitch.ToString("0.0",CultureInfo.InvariantCulture)+" D "+after.OriginDistance.ToString("G3",CultureInfo.InvariantCulture)+" "+verdict);
                if(reason!=null){failures.Add("キー "+item.Name+": "+reason);}
                caseIndex++;caseFrame=0;
                return;
            }
            caseFrame++;
        }

        private static string Format(double3 value){
            return "("+value.x.ToString("0.000",CultureInfo.InvariantCulture)+","+value.y.ToString("0.000",CultureInfo.InvariantCulture)+","+value.z.ToString("0.000",CultureInfo.InvariantCulture)+")";
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
