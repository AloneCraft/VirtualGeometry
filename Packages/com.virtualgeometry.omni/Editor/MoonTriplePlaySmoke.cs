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
    /// MoonTriple デモ scene を Play Mode で動かして確かめる(batchmode)。
    /// scene を開き、Play Mode に入り、<see cref="MoonTripleDemo"/> が自分で初期化(Update)してから、キーを <see cref="IMoonTripleKeys"/> の差し替えで送り、
    /// キーごとの量(ボールの x・y・z、Shift の 10 倍)、原点へのテレポート、面の選択、面の高さ(1 mm・Shift の 10 mm)、半分埋める、論理ニアクリップ、初期化を確かめる。
    /// 焦点の無い batchmode では Input System の仮想キーボードのイベントが届かないので、実際のキーボードとマウスは確かめない(PlaneSinkPlaySmoke と同じ)。
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.MoonTriplePlaySmoke.Run
    /// </summary>
    public static class MoonTriplePlaySmoke{
        private const string Results="TestResults/Planet/MoonTriplePlay";
        // 1 つのケース: 押す(frame 0)→ 離す(frame 1)→ 結果を見る(frame 4)。
        private const int CheckFrame=4;
        private const int InitializeLimit=600;
        private const int StartFrames=300;
        private const double NearTolerance=0.01;

        private static int frames;
        private static int phase;
        private static int caseIndex;
        private static int caseFrame;
        private static MoonTripleDemo demo;
        private static Case[] cases;
        private static double started;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();
        private static readonly ScriptedKeys scripted=new ScriptedKeys();
        // 「シーンを再読み込みしない」設定で Play する(Edit Mode で作った面の準備が、Play に持ち越される場合の確認)。
        private static bool noSceneReload;

        private sealed class Case{
            public string Name;
            public Action Press;
            public Func<string> Check;
        }

        /// <summary>押した frame だけ押されている入力元。Tap で押し、Clear で離す。PressedThisFrame は最初に聞かれたときだけ true。</summary>
        private sealed class ScriptedKeys:IMoonTripleKeys{
            private readonly HashSet<MoonTripleKey> held=new HashSet<MoonTripleKey>();
            private readonly HashSet<MoonTripleKey> fresh=new HashSet<MoonTripleKey>();

            public void Tap(MoonTripleKey key){
                this.held.Add(key);this.fresh.Add(key);
            }

            public void Clear(){
                this.held.Clear();this.fresh.Clear();
            }

            public bool Held(MoonTripleKey key){return this.held.Contains(key);}
            public bool PressedThisFrame(MoonTripleKey key){return this.fresh.Remove(key);}
            public Vector2 LookDelta(){return Vector2.zero;}
            public float ZoomDelta(){return 0f;}
        }

        public static void RunNoSceneReload(){
            noSceneReload=true;
            Run();
        }

        public static void Run(){
            frames=0;phase=0;caseIndex=0;caseFrame=0;demo=null;cases=null;started=EditorApplication.timeSinceStartup;
            report.Clear();failures.Clear();errors.Clear();
            Directory.CreateDirectory(Results);
            Application.logMessageReceived+=Record;
            try{
                MoonBallSceneBuilder.UsePipeline();
                EditorSceneManager.OpenScene(MoonTripleSceneBuilder.ScenePath,OpenSceneMode.Single);
                // domain reload を止めて、static の状態を Play Mode の間も保つ。シーンは再読み込みする(実際に Play を押したときと同じ)。
                EditorSettings.enterPlayModeOptionsEnabled=true;
                EditorSettings.enterPlayModeOptions=EnterPlayModeOptions.DisableDomainReload;
                if(noSceneReload){EditorSettings.enterPlayModeOptions=EnterPlayModeOptions.DisableDomainReload|EnterPlayModeOptions.DisableSceneReload;}
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

        private static void Tick(){
            try{
                if(EditorApplication.timeSinceStartup-started>1200){throw new TimeoutException("MoonTriple play smoke timeout.");}
                if(!EditorApplication.isPlaying){return;}
                frames++;
                if(phase==0){
                    demo=UnityEngine.Object.FindAnyObjectByType<MoonTripleDemo>();
                    if(demo==null){throw new InvalidOperationException("scene に MoonTripleDemo がありません。");}
                    if(demo.IsInitialized||frames>InitializeLimit){
                        report.Add("initialized="+demo.IsInitialized+" after "+frames+" frames failure="+(demo.Failure??"(none)"));
                        if(!demo.IsInitialized){failures.Add("MoonTripleDemo が初期化されませんでした: "+demo.Failure);phase=99;return;}
                        phase=1;frames=0;
                    }
                }else if(phase==1){
                    // batchmode には Game View が無く、camera は自動では描かれないので、毎 frame 撮って描かせる(VG の面の Cell の生成が進む)。
                    Snap();
                    if(frames==StartFrames){
                        Save("PlayStart",Capture());
                        demo.Keys=scripted;
                        cases=BuildCases();
                        caseIndex=0;caseFrame=0;phase=2;
                    }
                }else if(phase==2){
                    Snap();
                    RunCase();
                }
                if(phase==99){Finish(null);}
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void RunCase(){
            if(caseIndex>=cases.Length){phase=99;return;}
            Case current=cases[caseIndex];
            if(caseFrame==0){current.Press();}
            else if(caseFrame==1){scripted.Clear();}
            else if(caseFrame==CheckFrame){
                string problem=current.Check();
                report.Add((problem==null?"ok   ":"FAIL ")+current.Name+(problem==null?"":" : "+problem));
                if(problem!=null){failures.Add(current.Name+": "+problem);}
                caseIndex++;caseFrame=-1;
            }
            caseFrame++;
        }

        private static Case[] BuildCases(){
            var list=new List<Case>();
            list.Add(new Case{Name="初期状態: 面 0 が選ばれ、ずれは 0、論理 near は通常",Press=()=>{},Check=()=>{
                if(demo.SelectedFace!=0){return "選んだ面 "+demo.SelectedFace;}
                for(int f=0;f<4;f++){if(demo.FaceOffsetUnits(f)!=0){return "面 "+f+" の高さが 0 でない";}}
                if(demo.OffsetX!=0||demo.OffsetY!=0||demo.OffsetZ!=0){return "ボールが原点に無い";}
                return Near(demo.LogicalNearMeters,MoonTripleDemo.DefaultNearMeters);
            }});
            list.Add(new Case{Name="x +1mm(→)",Press=()=>scripted.Tap(MoonTripleKey.BallXPlus),Check=()=>Expect(demo.OffsetX,10,"x")});
            list.Add(new Case{Name="y +1mm(↑)",Press=()=>scripted.Tap(MoonTripleKey.BallUp),Check=()=>Expect(demo.OffsetY,10,"y")});
            list.Add(new Case{Name="z +1mm(PageUp)",Press=()=>scripted.Tap(MoonTripleKey.BallZPlus),Check=()=>Expect(demo.OffsetZ,10,"z")});
            list.Add(new Case{Name="Shift で 10 倍(x +10mm)",Press=()=>{scripted.Tap(MoonTripleKey.Coarse);scripted.Tap(MoonTripleKey.BallXPlus);},Check=()=>Expect(demo.OffsetX,110,"x")});
            list.Add(new Case{Name="[1] 原点へ(ボールの位置 0、camera は既定の向き)",Press=()=>scripted.Tap(MoonTripleKey.Corner),Check=()=>{
                if(demo.OffsetX!=0||demo.OffsetY!=0||demo.OffsetZ!=0){return "ボールが原点に無い";}
                if(Math.Abs(demo.CameraYaw-180f)>1e-3f){return "yaw "+demo.CameraYaw;}
                return null;
            }});
            list.Add(new Case{Name="[2] 選ぶ面を次へ(面 0 → 1)",Press=()=>scripted.Tap(MoonTripleKey.SelectFace),Check=()=>demo.SelectedFace==1?null:"選んだ面 "+demo.SelectedFace});
            list.Add(new Case{Name="[2] 選ぶ面を次へ(面 1 → 2)",Press=()=>scripted.Tap(MoonTripleKey.SelectFace),Check=()=>demo.SelectedFace==2?null:"選んだ面 "+demo.SelectedFace});
            list.Add(new Case{Name="[ ] 選んだ面(2)の高さ +1mm、他の面は動かない",Press=()=>scripted.Tap(MoonTripleKey.FaceUp),Check=()=>{
                if(demo.FaceOffsetUnits(2)!=10){return "面 2 の高さ "+demo.FaceOffsetUnits(2);}
                for(int f=0;f<4;f++){if(f!=2&&demo.FaceOffsetUnits(f)!=0){return "面 "+f+" も動いた";}}
                return null;
            }});
            list.Add(new Case{Name="[ ] Shift で 10 mm(面 2 を +100)",Press=()=>{scripted.Tap(MoonTripleKey.Coarse);scripted.Tap(MoonTripleKey.FaceUp);},Check=()=>Expect(demo.FaceOffsetUnits(2),110,"面 2 の高さ")});
            list.Add(new Case{Name="[ ] 面 2 の高さを戻す(−100 → 10)",Press=()=>{scripted.Tap(MoonTripleKey.Coarse);scripted.Tap(MoonTripleKey.FaceDown);},Check=()=>Expect(demo.FaceOffsetUnits(2),10,"面 2 の高さ")});
            list.Add(new Case{Name="[2] 面を 3(手動分割)へ",Press=()=>{scripted.Tap(MoonTripleKey.SelectFace);},Check=()=>demo.SelectedFace==3?null:"選んだ面 "+demo.SelectedFace});
            list.Add(new Case{Name="[ ] 面 3(手動分割)を +1mm",Press=()=>scripted.Tap(MoonTripleKey.FaceUp),Check=()=>Expect(demo.FaceOffsetUnits(3),10,"面 3 の高さ")});
            list.Add(new Case{Name="[5] 選んだ面(3)の高さに、ボールの中心を置く(半分埋まる)",Press=()=>scripted.Tap(MoonTripleKey.Bury),Check=()=>Expect(demo.OffsetY,demo.FaceOffsetUnits(3),"ボールの y")});
            // 見ている点へ埋める: 真上近く(pitch 89.9)から (300 m, −300 m) を見て「3」。交点はほぼ真下の点になり、ボールは選んだ面(3)の高さで、その点に来る。
            list.Add(new Case{Name="[3] 見ている点(300 m, −300 m)へ、選んだ面(3)の高さで埋める",Press=()=>{demo.PlaceCamera(new double3(300,2,-300),180f,89.9f);scripted.Tap(MoonTripleKey.BuryAim);},Check=()=>{
                if(Math.Abs(demo.OffsetX-3000000)>500){return "x = "+demo.OffsetX+"(期待 3000000 ± 500)";}
                if(Math.Abs(demo.OffsetZ+3000000)>500){return "z = "+demo.OffsetZ+"(期待 -3000000 ± 500)";}
                if(demo.OffsetY!=demo.FaceOffsetUnits(3)){return "y = "+demo.OffsetY+"(期待 面 3 の高さ "+demo.FaceOffsetUnits(3)+")";}
                return null;
            }});
            list.Add(new Case{Name="論理ニアクリップ 7(1 mm)",Press=()=>scripted.Tap(MoonTripleKey.ScaleFiner),Check=()=>{
                if(demo.ScalePreset!=1){return "プリセット "+demo.ScalePreset;}
                return Near(demo.LogicalNearMeters,1e-3);
            }});
            list.Add(new Case{Name="論理ニアクリップ 8(通常へ戻す)",Press=()=>scripted.Tap(MoonTripleKey.ScaleCoarser),Check=()=>demo.ScalePreset==0?null:"プリセット "+demo.ScalePreset});
            list.Add(new Case{Name="R 初期化(ボール・面の高さ・選択・論理 near が元に戻る)",Press=()=>scripted.Tap(MoonTripleKey.Reset),Check=()=>{
                if(demo.SelectedFace!=0){return "選んだ面 "+demo.SelectedFace;}
                for(int f=0;f<4;f++){if(demo.FaceOffsetUnits(f)!=0){return "面 "+f+" の高さが 0 でない";}}
                if(demo.OffsetX!=0||demo.OffsetY!=0||demo.OffsetZ!=0){return "ボールが原点に無い";}
                if(demo.ScalePreset!=0){return "プリセット "+demo.ScalePreset;}
                return null;
            }});
            list.Add(new Case{Name="[4] ドリブル開始(ドリブル中になり、ボールの高さは面の高さから 1 m の間)",Press=()=>scripted.Tap(MoonTripleKey.Dribble),Check=()=>{
                if(!demo.IsDribbling){return "ドリブル中になっていない";}
                if(demo.OffsetY<0||demo.OffsetY>10000+10){return "ボールの高さ "+demo.OffsetY+"(0〜10000 の範囲外。単位 0.1 mm)";}
                return null;
            }});
            list.Add(new Case{Name="ドリブルの速さ: 0.3 はそのまま、下限 0.05・上限 1 に収まる",Press=()=>{},Check=()=>{
                demo.DribbleSpeed=0.3f;
                if(Math.Abs(demo.DribbleSpeed-0.3f)>1e-6f){return "0.3 を入れて "+demo.DribbleSpeed;}
                demo.DribbleSpeed=0.001f;
                if(Math.Abs(demo.DribbleSpeed-MoonTripleDemo.MinimumDribbleSpeed)>1e-6f){return "0.001 を入れて "+demo.DribbleSpeed+"(下限 "+MoonTripleDemo.MinimumDribbleSpeed+")";}
                demo.DribbleSpeed=5f;
                if(Math.Abs(demo.DribbleSpeed-1f)>1e-6f){return "5 を入れて "+demo.DribbleSpeed+"(上限 1)";}
                return null;
            }});
            list.Add(new Case{Name="[1] 原点へ(ドリブルが止まる)",Press=()=>scripted.Tap(MoonTripleKey.Corner),Check=()=>{
                if(demo.IsDribbling){return "ドリブルが止まっていない";}
                if(demo.OffsetX!=0||demo.OffsetY!=0||demo.OffsetZ!=0){return "ボールが原点に無い";}
                return null;
            }});
            list.Add(new Case{Name="HUD: 4 面の呼び名がすべて出る",Press=()=>{},Check=()=>{
                string hud=demo.BuildHudText();
                foreach(string name in MoonTripleDemo.FaceNames){if(!hud.Contains(name)){return "HUD に "+name+" が無い";}}
                return null;
            }});
            return list.ToArray();
        }

        private static string Expect(long actual,long expected,string what){
            if(actual==expected){return null;}
            return what+" = "+actual+"(期待 "+expected+")";
        }

        private static string Near(double actual,double expected){
            if(double.IsNaN(actual)){return "論理 near を読めません";}
            if(Math.Abs(actual-expected)<=expected*NearTolerance){return null;}
            return "論理 near = "+actual.ToString("G4",CultureInfo.InvariantCulture)+"(期待 "+expected.ToString("G4",CultureInfo.InvariantCulture)+")";
        }

        private static Texture2D Capture(){
            Camera camera=Camera.main;
            if(camera==null){return null;}
            return M0Validation.Capture(camera,640,480);
        }

        private static void Save(string name,Texture2D image){
            if(image==null){return;}
            File.WriteAllBytes(Results+"/"+name+".png",image.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(image);
        }

        // 撮って捨てる(描画を進めるため。VG の面の Cell の生成は、描画のたびに進む)。
        private static void Snap(){
            Texture2D image=Capture();
            if(image!=null){UnityEngine.Object.DestroyImmediate(image);}
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Record;
            if(demo!=null){demo.Keys=null;}
            if(error!=null){failures.Add(error.ToString());}
            foreach(string message in errors){failures.Add("Unity logged error: "+message);}
            report.Add("UTC="+DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+" Unity="+Application.unityVersion+" GPU="+SystemInfo.graphicsDeviceName);
            if(failures.Count==0){report.Add("PASSED");}else{report.Add("FAILED");report.AddRange(failures);}
            Directory.CreateDirectory(Results);
            File.WriteAllLines(Results+"/Report.txt",report);
            EditorApplication.Exit(failures.Count==0?0:1);
        }
    }
}
