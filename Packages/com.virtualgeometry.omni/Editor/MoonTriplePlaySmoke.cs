using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using Unity.Mathematics;
using UnityEditor.SceneManagement;
using UnityEngine;
using VirtualGeometry.World;
using VirtualizedGeometry.Editor;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// MoonTriple デモ scene を Play Mode で動かして確かめる(batchmode)。
    /// scene を開き、Play Mode に入り、<see cref="MoonTripleDemo"/> が自分で初期化(Update)してから、キーを <see cref="IMoonTripleKeys"/> の差し替えで送り、
    /// キーごとの量と向き(ボールの x・y・z、周回、距離、テレポート、隙間、初期化)と、IMGUI のボタンと同じ入口(<see cref="MoonTripleDemo.TeleportTo"/>、<see cref="MoonTripleDemo.AdjustGap"/>)、エラーが出ないことを確かめる。
    /// テレポートは移動先の Cell の先読みを待つので、移るまでの frame 数も記録する。
    /// 焦点の無い batchmode では Input System の仮想キーボードのイベントが届かないので、実際のキーボードとマウス、IMGUI のボタンのクリックは確かめない(PlaneSinkPlaySmoke と同じ)。
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.MoonTriplePlaySmoke.Run
    /// </summary>
    public static class MoonTriplePlaySmoke{
        private const string Results="TestResults/Planet/MoonTriplePlay";
        private static int frames;
        private static int phase;
        private static int caseIndex;
        private static int caseFrame;
        private static MoonTripleDemo demo;
        private static double started;
        private static Snapshot before;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();
        private static readonly ScriptedKeys scripted=new ScriptedKeys();

        private sealed class Snapshot{
            public int Contact;
            public long X,Y,Z;
            public long Gap01,Gap12;
            public float Yaw,Pitch;
            public double3 Position;
            public bool Pending;
        }

        // 1 つの確認: 準備 → キーを押す(hold frame の間、押したまま)→ wait frame 待つ → 結果を調べる(失敗なら理由、成功なら null)。
        private sealed class Case{
            public string Name;
            public MoonTripleKey[] Keys;
            public int Hold;
            public int Wait;
            public Action Prepare;
            public Func<Snapshot,Snapshot,string> Check;
        }

        /// <summary>押した frame だけ押されている入力元。Tap で押し、Clear で離す。PressedThisFrame は最初に聞かれたときだけ true。</summary>
        private sealed class ScriptedKeys:IMoonTripleKeys{
            private readonly HashSet<MoonTripleKey> held=new HashSet<MoonTripleKey>();
            private readonly HashSet<MoonTripleKey> fresh=new HashSet<MoonTripleKey>();
            public Vector2 PendingLook;
            public float PendingZoom;

            public void Tap(MoonTripleKey key){
                this.held.Add(key);this.fresh.Add(key);
            }

            public void Clear(){
                this.held.Clear();this.fresh.Clear();
            }

            public bool Held(MoonTripleKey key){return this.held.Contains(key);}
            public bool PressedThisFrame(MoonTripleKey key){return this.fresh.Remove(key);}

            public Vector2 LookDelta(){
                Vector2 value=this.PendingLook;this.PendingLook=Vector2.zero;
                return value;
            }

            public float ZoomDelta(){
                float value=this.PendingZoom;this.PendingZoom=0f;
                return value;
            }
        }

        // 「シーンを再読み込みしない」設定(Enter Play Mode Options の Reload Scene 無効)で Play する。Edit Mode で作った分割処理(CosmicPlanetSurface の隠れた PlanetSurfaceLod)が、
        // Play に入る時の OnDisable で終了したまま残る場合(手動分割した月が細分化されない)の再現と、直しの確認に使う。
        private static bool noSceneReload;

        public static void RunNoSceneReload(){
            noSceneReload=true;
            Run();
        }

        public static void Run(){
            frames=0;phase=0;caseIndex=0;caseFrame=0;demo=null;started=EditorApplication.timeSinceStartup;
            report.Clear();failures.Clear();errors.Clear();sweepCrops.Clear();
            Directory.CreateDirectory(Results);
            Application.logMessageReceived+=Record;
            try{
                MoonBallSceneBuilder.UsePipeline();
                EditorSceneManager.OpenScene(MoonTripleSceneBuilder.ScenePath,OpenSceneMode.Single);
                // domain reload を止めて、static の状態を Play Mode の間も保つ。シーンは再読み込みする(実際に Play を押したときと同じ。
                // 再読み込みしないと、Edit Mode で作った PlanetSurface の分割処理が、Play に入る時の無効化で終了したまま残り、月 B が更新されない)。
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

        private static Snapshot Take(){
            return new Snapshot{Contact=demo.ContactIndex,X=demo.OffsetX,Y=demo.OffsetY,Z=demo.OffsetZ,Gap01=demo.Gap01Units,Gap12=demo.Gap12Units,Yaw=demo.CameraYaw,Pitch=demo.CameraPitch,Position=demo.CameraPosition,Pending=demo.IsPending};
        }

        private static long Touch(int contact){
            return (long)Math.Round(demo.Layout.TouchDistance(contact)/MoonTripleDemo.UnitMeters);
        }

        private static Case[] BuildCases(){
            var list=new List<Case>(BuildBaseCases());
            list.AddRange(BuildScaleCases());
            list.AddRange(BuildBuryCases());
            return list.ToArray();
        }

        // 「ボールの半分を埋める」: 接触点ちょうど(z = 0)で押しても、接触点から 1,500 m へ移って埋まる(以前は、接触点では上下の面の高さが同じで、y が動かなかった)。
        // 下の月は 15° 見下ろし、上の月は 15° 見上げる。上の月では、光を下から当てる。HUD の呼び名(分割しない面 / VG の面 / 手動分割した面)に、古い呼び名が残っていない。
        private static Case[] BuildBuryCases(){
            var cases=new List<Case>();
            long lateralUnits=(long)Math.Round(MoonTripleDemo.BuryLateralMeters/MoonTripleDemo.UnitMeters);
            foreach(int contactIndex in new[]{0,1}){
                int contact=contactIndex;
                foreach(bool lowerMoon in new[]{true,false}){
                    bool lower=lowerMoon;
                    string moon;
                    if(lower){moon=MoonTripleDemo.LowerSurfaceName(contact);}else{moon=MoonTripleDemo.UpperSurfaceName(contact);}
                    MoonTripleKey key;
                    if(lower){key=MoonTripleKey.BuryLower;}else{key=MoonTripleKey.BuryUpper;}
                    cases.Add(new Case{Name="Bury half into the "+(lower?"lower":"upper")+" moon ("+moon+") from the exact contact point "+contact,Keys=new[]{key},Hold=2,Wait=1000,
                        Prepare=()=>{demo.SetGaps(0,0);demo.SetState(contact,0,0,0,180f,10f,0.35f);},
                        Check=(a,b)=>{
                            double surface;
                            if(lower){surface=demo.Layout.LowerSurfaceY(contact,MoonTripleDemo.BuryLateralMeters);}else{surface=demo.Layout.UpperSurfaceY(contact,MoonTripleDemo.BuryLateralMeters);}
                            long expectedY=(long)Math.Round((surface-demo.Layout.ContactY(contact))/MoonTripleDemo.UnitMeters);
                            float expectedPitch;
                            if(lower){expectedPitch=15f;}else{expectedPitch=-15f;}
                            double distance=math.length(b.Position-demo.BallCenterDouble());
                            Vector4 light=Shader.GetGlobalVector("_CosmicKeyLight");
                            bool lightOk;
                            if(lower){lightOk=light.y>0;}else{lightOk=light.y<0;}
                            if(!b.Pending&&b.Contact==contact&&b.X==0&&b.Z==lateralUnits&&b.Y==expectedY&&b.Yaw==180f&&b.Pitch==expectedPitch&&Math.Abs(distance-MoonTripleDemo.FrameDistance)<1e-3&&lightOk){return null;}
                            return "半分を埋められませんでした(pending "+b.Pending+"、接触点 "+b.Contact+"、位置 ("+b.X+","+b.Y+","+b.Z+") 期待 (0,"+expectedY+","+lateralUnits+")、yaw "+b.Yaw+"、pitch "+b.Pitch+" 期待 "+expectedPitch+"、camera までの距離 "+distance.ToString("G4",CultureInfo.InvariantCulture)+"、光の y "+light.y+")。";
                        }});
                }
            }
            // ボタンの入口(同じメソッド)。接触点ちょうどから、キーを使わずに呼ぶ。
            cases.Add(new Case{Name="Bury half (button entry, upper moon of contact 0)",Keys=new MoonTripleKey[0],Hold=1,Wait=1000,
                Prepare=()=>{demo.SetGaps(0,0);demo.SetState(0,0,0,0,180f,10f,0.35f);demo.BuryBall(false);},
                Check=(a,b)=>{
                    double surface=demo.Layout.UpperSurfaceY(0,MoonTripleDemo.BuryLateralMeters);
                    long expectedY=(long)Math.Round((surface-demo.Layout.ContactY(0))/MoonTripleDemo.UnitMeters);
                    if(!b.Pending&&b.Z==lateralUnits&&b.Y==expectedY){return null;}
                    return "ボタンの入口で、半分を埋められませんでした(pending "+b.Pending+"、位置 ("+b.X+","+b.Y+","+b.Z+") 期待 y "+expectedY+" z "+lateralUnits+")。";
                }});
            // HUD の呼び名: 古い呼び名(普通の月、拡大した月、PlanetSurface、方式の番号、月 A・月 B。球と誤解される「月」全般)が残っていない。今の接触点の面の呼び名が出る。
            foreach(int contactIndex in new[]{0,1}){
                int contact=contactIndex;
                cases.Add(new Case{Name="HUD names at contact "+contact,Keys=new MoonTripleKey[0],Hold=1,Wait=3,
                    Prepare=()=>{demo.SetGaps(0,0);demo.SetState(contact,0,0,Touch(contact),180f,10f,0.35f);},
                    Check=(a,b)=>{
                        string hud=demo.BuildHudText();
                        foreach(string forbidden in new[]{"普通の月","拡大した月","手動分割した月","VG の月","PlanetSurface","方式","月 A","月 B","月"}){
                            if(hud.Contains(forbidden)){return "HUD に、古い呼び名「"+forbidden+"」が残っています。";}
                        }
                        foreach(string required in new[]{MoonTripleDemo.LowerSurfaceName(contact),MoonTripleDemo.UpperSurfaceName(contact)}){
                            if(!hud.Contains(required)){return "HUD に、今の接触点の面の呼び名「"+required+"」が出ていません。";}
                        }
                        return null;
                    }});
            }
            // ボールの点光源: 面のシェーダーへ渡す値(Omni の面 = ボールの中心のカメラからの相対位置、VG の面 = ボールの中心 − 基準点)が、今の状態と合っている。原子の周りでは消える。
            foreach(int contactIndex in new[]{0,1}){
                int contact=contactIndex;
                cases.Add(new Case{Name="Ball light globals at contact "+contact,Keys=new MoonTripleKey[0],Hold=1,Wait=3,
                    Prepare=()=>{demo.SetGaps(0,0);demo.SetState(contact,30,20,Touch(contact),180f,10f,0.35f);},
                    Check=(a,b)=>{
                        Vector4 omni=Shader.GetGlobalVector("_CosmicBallLight");
                        Vector4 vg=Shader.GetGlobalVector("_PlanetBallLight");
                        double3 relative=demo.BallCenterDouble()-demo.CameraPosition;
                        double referenceY=demo.Layout.CenterA+(contact==1?demo.Layout.Radius:-demo.Layout.Radius);
                        double3 ball=demo.BallCenterDouble();
                        double error=Math.Max(Math.Max(Math.Abs(omni.x-relative.x),Math.Abs(omni.y-relative.y)),Math.Abs(omni.z-relative.z));
                        double vgError=Math.Max(Math.Max(Math.Abs(vg.x-ball.x),Math.Abs(vg.y-(ball.y-referenceY))),Math.Abs(vg.z-ball.z));
                        Vector4 keyOmni=Shader.GetGlobalVector("_CosmicKeyLight");Vector4 keyVg=Shader.GetGlobalVector("_PlanetKeyLight");
                        if(keyOmni!=keyVg){return "主光源が、Omni の面(_CosmicKeyLight "+keyOmni+")と VG の面(_PlanetKeyLight "+keyVg+")で違います。";}
                        if(error<1e-3&&vgError<1e-3&&omni.w==MoonTripleDemo.BallLightIntensity&&vg.w==MoonTripleDemo.BallLightIntensity){return null;}
                        return "ボールの点光源の値が合っていません(Omni の面 "+omni+" 期待 "+relative+"、VG の面 "+vg+")。";
                    }});
            }
            return cases.ToArray();
        }

        // 論理ニアクリップのプリセット、キー 7・8、原子へのテレポートと戻り、スケールに連動した飛ぶ量。
        private static Case[] BuildScaleCases(){
            Action defaultView=()=>{demo.SetGaps(0,0);demo.SetState(0,0,0,Touch(0),180f,10f,0.35f);demo.SetScalePreset(0);};
            const double atomDistance=MoonTripleDemo.AtomFrameRadii*MoonTripleLayout.AtomRadius;
            var cases=new List<Case>();
            for(int index=1;index<MoonTripleDemo.ScaleNearMeters.Length;index++){
                int preset=index;
                cases.Add(new Case{Name="Scale preset "+MoonTripleDemo.ScaleNames[preset],Keys=new MoonTripleKey[0],Hold=1,Wait=3,
                    Prepare=()=>{defaultView();demo.SetScalePreset(preset);},
                    Check=(a,b)=>ReadbackCheck(preset)});
            }
            cases.Add(new Case{Name="7 (scale finer)",Keys=new[]{MoonTripleKey.ScaleFiner},Hold=2,Wait=3,Prepare=defaultView,
                Check=(a,b)=>{if(demo.ScalePreset==1){return null;}return "7 でプリセットが 1 つ細かくなりませんでした(プリセット "+demo.ScalePreset+")。";}});
            cases.Add(new Case{Name="8 (scale coarser)",Keys=new[]{MoonTripleKey.ScaleCoarser},Hold=2,Wait=3,Prepare=()=>{defaultView();demo.SetScalePreset(2);},
                Check=(a,b)=>{if(demo.ScalePreset==1){return null;}return "8 でプリセットが 1 つ粗くなりませんでした(プリセット "+demo.ScalePreset+")。";}});
            // 9: 原子へテレポート(先読みを待つ)。移った後は、論理 near が最小のプリセット、原子の中心から半径の 4 倍の距離。
            cases.Add(new Case{Name="9 (teleport to atom)",Keys=new[]{MoonTripleKey.TeleportAtom},Hold=2,Wait=1000,Prepare=defaultView,
                Check=(a,b)=>{
                    double distance=demo.DistanceToAtom;
                    if(!b.Pending&&demo.IsAtomView&&demo.ScalePreset==MoonTripleDemo.ScaleNearMeters.Length-1&&Math.Abs(distance-atomDistance)<1e-3*atomDistance){return null;}
                    return "原子へテレポートできませんでした(pending "+b.Pending+"、atomView "+demo.IsAtomView+"、プリセット "+demo.ScalePreset+"、距離 "+distance.ToString("G4",CultureInfo.InvariantCulture)+" m、期待 "+atomDistance.ToString("G4",CultureInfo.InvariantCulture)+" m)。";
                }});
            // 原子の近くでは、飛ぶ量が小さい(スケールに連動)。W を押している間に、原子へ向かって、通常の 3e-10 倍ほどの小さい量だけ進む。
            cases.Add(new Case{Name="W (fly scaled at the atom)",Keys=new[]{MoonTripleKey.FlyForward},Hold=20,Wait=3,
                Prepare=()=>{defaultView();demo.SetScalePreset(MoonTripleDemo.ScaleNearMeters.Length-1);demo.FrameAtom(180f,0f,atomDistance);},
                Check=(a,b)=>{
                    double3 d=b.Position-a.Position;
                    double along=math.dot(d,new double3(0,0,-1));
                    double across=math.length(d-new double3(0,0,-1)*along);
                    if(along>1e-14&&along<1e-9&&across<0.1*along){return null;}
                    return "原子の近くで、飛ぶ量がスケールに連動していません(移動 "+Format(d)+"、-z 方向の成分 "+along.ToString("G4",CultureInfo.InvariantCulture)+" m、期待 1e-14〜1e-9 m)。";
                }});
            // 1: 原子の周りからボールの側へ戻る。論理 near が通常に戻る。
            cases.Add(new Case{Name="1 (return from the atom)",Keys=new[]{MoonTripleKey.Contact0},Hold=2,Wait=1000,
                Prepare=()=>{defaultView();demo.SetScalePreset(MoonTripleDemo.ScaleNearMeters.Length-1);demo.FrameAtom(180f,10f,atomDistance);},
                Check=(a,b)=>{
                    if(!b.Pending&&!demo.IsAtomView&&demo.ScalePreset==0&&b.Contact==0){return null;}
                    return "原子の周りから戻れませんでした(pending "+b.Pending+"、atomView "+demo.IsAtomView+"、プリセット "+demo.ScalePreset+"、接触点 "+b.Contact+")。";
                }});
            return cases.ToArray();
        }

        // SetViewScale が実際に効いたか(論理 near と log2 M)を Omni から読み戻して確かめる(MoonBallValidation.StepsViewScale と同じ見方)。
        private static string ReadbackCheck(int preset){
            double near=MoonTripleDemo.ScaleNearMeters[preset];
            double read=demo.LogicalNearMeters;
            float log2=demo.LogicalLog2ViewScale;
            // 期待値は、camera の設定の Near(定数 0.3)から計算する(Camera.nearClipPlane は、描画の途中の値が読めることがあり、同じ値から期待値を出すと食い違いを見逃す)。
            double expectedLog2=Math.Log(MoonTripleDemo.DefaultCameraNearMeters/near,2.0);
            if(demo.CameraNearRewritten){return "Camera.nearClipPlane が設定の Near(0.3)と違います("+demo.CameraNearPlane.ToString("G4",CultureInfo.InvariantCulture)+")。描画の途中の値が戻っていません。";}
            report.Add("  scale readback preset "+preset+": nearClip="+read.ToString("G4",CultureInfo.InvariantCulture)+" (expected "+near.ToString("G4",CultureInfo.InvariantCulture)+") log2ViewScale="+log2.ToString("G4",CultureInfo.InvariantCulture)+" (expected "+expectedLog2.ToString("G4",CultureInfo.InvariantCulture)+")");
            if(double.IsNaN(read)||Math.Abs(read-near)>1e-3*near){return "論理 near が反映されていません(読み戻し "+read+" m、期待 "+near+" m)。";}
            if(Math.Abs(log2-expectedLog2)>1e-3+1e-4*Math.Abs(expectedLog2)){return "log2 M が反映されていません(読み戻し "+log2+"、期待 "+expectedLog2+")。";}
            return null;
        }

        private static Case[] BuildBaseCases(){
            // 動かさずに、接触点 0 のボールが両側の面に接する距離の既定の視点に置く。
            Action defaultView=()=>{demo.SetGaps(0,0);demo.SetState(0,0,0,Touch(0),180f,10f,0.35f);};
            return new[]{
                // ボールの動き(0.1mm 単位)。押した 1 回で、ちょうど 1 回分動く。Shift で 10 倍、Ctrl で 1/10、同時なら Ctrl が優先。
                Quantity("Left (x -1mm)",new[]{MoonTripleKey.BallXMinus},-10,0,0,defaultView),
                Quantity("Right (x +1mm)",new[]{MoonTripleKey.BallXPlus},10,0,0,defaultView),
                Quantity("Up (y +1mm, toward the upper moon)",new[]{MoonTripleKey.BallUp},0,10,0,defaultView),
                Quantity("Down (y -1mm, toward the lower moon)",new[]{MoonTripleKey.BallDown},0,-10,0,defaultView),
                Quantity("PageUp (z +1mm)",new[]{MoonTripleKey.BallZPlus},0,0,10,defaultView),
                Quantity("PageDown (z -1mm)",new[]{MoonTripleKey.BallZMinus},0,0,-10,defaultView),
                Quantity("Shift+Right (10mm)",new[]{MoonTripleKey.Coarse,MoonTripleKey.BallXPlus},100,0,0,defaultView),
                Quantity("Ctrl+Right (0.1mm)",new[]{MoonTripleKey.Fine,MoonTripleKey.BallXPlus},1,0,0,defaultView),
                Quantity("Shift+Ctrl+Right (0.1mm)",new[]{MoonTripleKey.Coarse,MoonTripleKey.Fine,MoonTripleKey.BallXPlus},1,0,0,defaultView),
                // camera の周回と距離
                // 飛ぶ(向きは yaw 0、pitch 0 で、前方 = +Z、右 = +X、上 = +Y)。ボールは動かない。
                Fly("W (forward)",MoonTripleKey.FlyForward,new double3(0,0,1)),
                Fly("S (back)",MoonTripleKey.FlyBack,new double3(0,0,-1)),
                Fly("D (right)",MoonTripleKey.FlyRight,new double3(1,0,0)),
                Fly("A (left)",MoonTripleKey.FlyLeft,new double3(-1,0,0)),
                Fly("E (up)",MoonTripleKey.FlyUp,new double3(0,1,0)),
                Fly("Q (down)",MoonTripleKey.FlyDown,new double3(0,-1,0)),
                Fly("Home (forward)",MoonTripleKey.ZoomIn,new double3(0,0,1)),
                Fly("End (back)",MoonTripleKey.ZoomOut,new double3(0,0,-1)),
                // pitch 45 度で下を向いて W: 前方は下向きの成分を持つ。
                new Case{Name="W (pitched 45 deg down: forward goes down)",Keys=new[]{MoonTripleKey.FlyForward},Hold=20,Wait=3,
                    Prepare=()=>{demo.SetState(0,0,0,Touch(0),0f,45f,0.35f);},
                    Check=(a,b)=>{
                        double c=Math.Cos(Math.PI/4),sn=Math.Sin(Math.PI/4);
                        return Along(a,b,new double3(0,-sn,c));
                    }},
                // 向きを変える: 右ドラッグ、ホイール(前方へ)。
                new Case{Name="Wheel up (forward)",Keys=new MoonTripleKey[0],Hold=1,Wait=3,Prepare=()=>{demo.SetState(0,0,0,Touch(0),0f,0f,0.35f);scripted.PendingZoom=1f;},
                    Check=(a,b)=>Along(a,b,new double3(0,0,1))},
                // C: 離れた camera をボールに向け直す(向きは保つ、距離は FrameDistance)。
                new Case{Name="C (frame the ball)",Keys=new[]{MoonTripleKey.Frame},Hold=2,Wait=3,Prepare=()=>{demo.SetState(0,0,0,Touch(0),30f,20f,0.35f);demo.FrameBall(30f,20f,5f);},
                    Check=(a,b)=>{
                        double distance=math.length(b.Position-demo.BallCenterDouble());
                        if(Math.Abs(distance-0.35)<1e-3&&b.Yaw==30f&&b.Pitch==20f&&b.X==a.X&&b.Y==a.Y&&b.Z==a.Z){return null;}
                        return "C でボールに向け直せませんでした(距離 "+distance+"、yaw "+b.Yaw+"、pitch "+b.Pitch+")。";
                    }},
                new Case{Name="Right-drag (yaw +2 deg)",Keys=new MoonTripleKey[0],Hold=1,Wait=3,Prepare=()=>{defaultView();scripted.PendingLook=new Vector2(10,0);},
                    Check=(a,b)=>{if(Mathf.Abs((b.Yaw-a.Yaw)-2f)<0.01f){return null;}return "yaw が "+(b.Yaw-a.Yaw)+" 度動きました(期待 +2)。";}},
                // テレポート(先読みを待ってから移る)と隙間
                Teleport("2 (teleport to contact 1)",MoonTripleKey.Contact1,1,true,defaultView),
                Teleport("1 (teleport to contact 0)",MoonTripleKey.Contact0,0,true,()=>{demo.SetState(1,500,300,Touch(1),90f,40f,2f);}),
                Teleport("4 (exact contact point)",MoonTripleKey.Exact,0,false,defaultView),
                Teleport("3 (touch ring)",MoonTripleKey.Touch,0,true,()=>{demo.SetState(0,0,0,0,180f,10f,0.35f);}),
                new Case{Name="] (gap +1mm)",Keys=new[]{MoonTripleKey.GapUp},Hold=2,Wait=3,Prepare=defaultView,
                    Check=(a,b)=>{if(b.Gap01-a.Gap01==10&&b.Gap12==a.Gap12){return null;}return "隙間が違います(接触点 0: "+a.Gap01+" → "+b.Gap01+"、接触点 1: "+a.Gap12+" → "+b.Gap12+")。";}},
                new Case{Name="[ (gap -1mm)",Keys=new[]{MoonTripleKey.GapDown},Hold=2,Wait=3,Prepare=defaultView,
                    Check=(a,b)=>{if(b.Gap01-a.Gap01==-10&&b.Gap12==a.Gap12){return null;}return "隙間が違います(接触点 0: "+a.Gap01+" → "+b.Gap01+")。";}},
                new Case{Name="Button path: AdjustGap(contact 1, +10mm)",Keys=new MoonTripleKey[0],Hold=1,Wait=3,Prepare=()=>{demo.SetGaps(0,0);demo.SetState(1,0,0,Touch(1),180f,10f,0.35f);demo.AdjustGap(1,100);},
                    Check=(a,b)=>{if(b.Gap12==100&&b.Gap01==0){return null;}return "ボタンの入口で隙間が変わりませんでした("+b.Gap01+"、"+b.Gap12+")。";}},
                new Case{Name="R (reset to touch ring)",Keys=new[]{MoonTripleKey.Reset},Hold=2,Wait=5,Prepare=()=>{demo.SetGaps(0,0);demo.SetState(1,300,200,400,33f,50f,2f);},
                    Check=(a,b)=>{if(!b.Pending&&b.X==0&&b.Y==0&&b.Z==Touch(b.Contact)&&b.Yaw==180f&&Math.Abs(math.length(b.Position-demo.BallCenterDouble())-0.35)<1e-3){return null;}return "初期化されませんでした("+b.X+","+b.Y+","+b.Z+" yaw "+b.Yaw+" 位置 "+b.Position+" pending "+b.Pending+")。";}},
            };
        }

        private static Case Quantity(string name,MoonTripleKey[] keys,long expectedX,long expectedY,long expectedZ,Action prepare){
            return new Case{Name=name,Keys=keys,Hold=2,Wait=3,Prepare=prepare,
                Check=(a,b)=>{
                    long x=b.X-a.X,y=b.Y-a.Y,z=b.Z-a.Z;
                    if(x==expectedX&&y==expectedY&&z==expectedZ){return null;}
                    return "量が違います: ("+x+","+y+","+z+")、期待 ("+expectedX+","+expectedY+","+expectedZ+")。";
                }};
        }

        // 飛ぶ: 押している間に、camera が direction の向きへ動く(ボールは動かない)。
        private static Case Fly(string name,MoonTripleKey key,double3 direction){
            return new Case{Name=name,Keys=new[]{key},Hold=20,Wait=3,
                Prepare=()=>{demo.SetState(0,0,0,Touch(0),0f,0f,0.35f);},
                Check=(a,b)=>{
                    if(b.X!=a.X||b.Y!=a.Y||b.Z!=a.Z){return "飛ぶ操作でボールが動きました("+(b.X-a.X)+","+(b.Y-a.Y)+","+(b.Z-a.Z)+")。";}
                    return Along(a,b,direction);
                }};
        }

        // camera の動き d が direction の向きに進んでいる(進んだ量が十分あり、横ずれが小さい)ことを確かめる。
        private static string Along(Snapshot a,Snapshot b,double3 direction){
            double3 d=b.Position-a.Position;
            double along=math.dot(d,direction);
            double across=math.length(d-direction*along);
            if(along>1e-3&&across<0.05*along){return null;}
            return "camera の動きが違います(移動 "+Format(d)+"、期待の向き "+Format(direction)+")。";
        }

        // テレポート: キーを押すと先読みを待ち(IsPending)、移った後は接触点 index・ボールの z(touch なら両側の面に接する距離、でなければ 0)・既定の camera になる。待つのは最大 900 frame なので長く待つ。
        private static Case Teleport(string name,MoonTripleKey key,int index,bool touchRing,Action prepare){
            return new Case{Name=name,Keys=new[]{key},Hold=2,Wait=1000,Prepare=prepare,
                Check=(a,b)=>{
                    long expectedZ=0;
                    if(touchRing){expectedZ=Touch(index);}
                    if(!b.Pending&&b.Contact==index&&b.X==0&&b.Y==0&&b.Z==expectedZ&&b.Yaw==180f&&Math.Abs(math.length(b.Position-demo.BallCenterDouble())-0.35)<1e-3){return null;}
                    return "テレポートできませんでした(接触点 "+b.Contact+" 期待 "+index+"、位置 ("+b.X+","+b.Y+","+b.Z+") 期待 z="+expectedZ+"、pending "+b.Pending+")。";
                }};
        }

        private static Case[] cases;
        private static int teleportWaited;

        private static void Tick(){
            try{
                if(EditorApplication.timeSinceStartup-started>2400){throw new TimeoutException("MoonTriple play smoke timeout.");}
                if(!EditorApplication.isPlaying){return;}
                frames++;
                if(phase==0){
                    demo=UnityEngine.Object.FindAnyObjectByType<MoonTripleDemo>();
                    if(demo==null){throw new InvalidOperationException("scene に MoonTripleDemo がありません。");}
                    if(demo.IsInitialized||frames>600){
                        report.Add("initialized="+demo.IsInitialized+" after "+frames+" frames failure="+(demo.Failure??"(none)"));
                        if(!demo.IsInitialized){failures.Add("MoonTripleDemo が初期化されませんでした: "+demo.Failure);phase=99;return;}
                        phase=1;frames=0;
                    }
                }else if(phase==1){
                    // batchmode には Game View が無く、camera は自動では描かれない(選択と生成は描画のたびに進む)ので、毎 frame 撮って描かせる。
                    if(frames!=300){Capture(320,240);}
                    if(frames==300){
                        Save("PlayStart",Capture());
                        demo.Keys=scripted;
                        caseIndex=0;caseFrame=0;phase=2;
                        // 近道(デバッグ用。環境変数 MOONTRIPLE_QUICK=1): キーのケースを飛ばして、手動分割した月の細分化の確認(phase 3)だけを行う。
                        if(Environment.GetEnvironmentVariable("MOONTRIPLE_QUICK")=="1"){phase=3;refineFrame=0;refineStable=0;}
                    }
                }else if(phase==2){
                    Capture(320,240);
                    RunCase();
                }else if(phase==3){
                    Capture(320,240);
                    RefinePatch();
                }else if(phase==4){
                    Capture(320,240);
                    BuryCaptures();
                }else if(phase==5){
                    Capture(320,240);
                    SweepCaptures();
                }else if(phase==6){
                    Capture(320,240);
                    FineSweep();
                }else if(phase==7){
                    Capture(320,240);
                    ScaleSilhouette();
                }
                if(phase==99){Finish(null);}
            }catch(Exception error){
                Finish(error);
            }
        }

        private static void RunCase(){
            if(cases==null){cases=BuildCases();}
            if(caseIndex>=cases.Length){
                phase=3;refineFrame=0;refineStable=0;return;
            }
            Case item=cases[caseIndex];
            if(caseFrame==0){
                scripted.Clear();
                if(item.Prepare!=null){item.Prepare();}
                before=Take();
                teleportWaited=0;
                foreach(MoonTripleKey key in item.Keys){scripted.Tap(key);}
            }else if(caseFrame==item.Hold){
                scripted.Clear();
            }else if(caseFrame>item.Hold){
                // 待つ frame は、テレポート待ちが終わるまで(終わっていれば Wait を待たずに調べる)。
                bool waiting=demo.IsPending;
                if(waiting){teleportWaited++;}
                if(caseFrame>=item.Hold+item.Wait||(!waiting&&caseFrame>=item.Hold+5)){
                    Snapshot after=Take();
                    string reason=item.Check(before,after);
                    string verdict="NG";
                    if(reason==null){verdict="ok";}
                    report.Add("key "+item.Name+": contact "+before.Contact+"->"+after.Contact+" offset ("+before.X+","+before.Y+","+before.Z+")->("+after.X+","+after.Y+","+after.Z+") gap ("+before.Gap01+","+before.Gap12+")->("+after.Gap01+","+after.Gap12+")"+
                        " yaw "+before.Yaw.ToString("0.0",CultureInfo.InvariantCulture)+"->"+after.Yaw.ToString("0.0",CultureInfo.InvariantCulture)+" position "+Format(before.Position)+"->"+Format(after.Position)+
                        " teleportWaitedFrames="+teleportWaited+" "+verdict);
                    if(reason!=null){failures.Add("キー "+item.Name+": "+reason);}
                    caseIndex++;caseFrame=0;
                    return;
                }
            }
            caseFrame++;
        }

        // PlanetSurface の月 B(方式 3)の細分化: 接触点 1 の 1,500 m の位置へ移り、月 B の下面にボールの半分を埋めて見上げ、階層が要求の階層に届くまでの経過を記録する。
        // Play Mode は時間が進むので、Edit Mode(検証のバッチ)と違って細分化が進むかを確かめる。60 frame 続けて届いていれば完了、3000 frame で届かなければ失敗。
        private static int refineFrame;
        private static int refineStable;

        private static void RefinePatch(){
            if(refineFrame==0){
                scripted.Clear();
                demo.SetGaps(0,0);
                demo.SetState(1,0,0,(long)Math.Round(1500/MoonTripleDemo.UnitMeters),180f,-15f,1.5f);
                demo.BuryBall(false);
                demo.FrameBall(180f,-15f,1.5f);
                demo.Apply();
                // 月 B の下面は太陽光の反対側を向いて暗いので、光を下から当てる。
                demo.SetKeyLightFromBelow(true);
            }
            global::Omni.PlanetSurface.PlanetSurfaceDiagnostics patch=demo.MoonB.Lod.Diagnostics;
            if(refineFrame%30==0){
                // 更新が始まらない原因の切り分け: 視点と月 B の位置が取れるか、分割処理が動いているか。
                var cameraLogical=demo.GetComponent<global::Omni.Logical.Transform>();
                var bodyLogical=demo.MoonB.GetComponent<global::Omni.Logical.Transform>();
                bool cameraOk=cameraLogical.TryGetWorld(out _,out _,out _);
                bool bodyOk=bodyLogical.TryGetWorld(out _,out _,out _);
                report.Add("debug frame "+refineFrame+": camera TryGetWorld="+cameraOk+" body TryGetWorld="+bodyOk+" lod.enabled="+demo.MoonB.Lod.enabled+" lod.activeAndEnabled="+demo.MoonB.Lod.isActiveAndEnabled+
                    " lastUpdateMs="+demo.MoonB.Lod.LastUpdateMilliseconds.ToString("F3",CultureInfo.InvariantCulture)+" lod.hideFlags="+demo.MoonB.Lod.hideFlags);
            }
            if(refineFrame%120==0){
                report.Add("PlanetSurface moon B refine frame "+refineFrame+": deepestLevel="+patch.DeepestLevel+" desiredLevel="+patch.DesiredLevel+" leaves="+patch.LeafCount+" renderers="+patch.RendererCount+" altitude="+patch.ViewerAltitudeMeters.ToString("F3",CultureInfo.InvariantCulture)+" m");
            }
            bool reached=patch.DeepestLevel>=patch.DesiredLevel;
            if(reached){refineStable++;}else{refineStable=0;}
            refineFrame++;
            if(refineStable>=60||refineFrame>=3000){
                report.Add("PlanetSurface moon B refine END frame "+refineFrame+": deepestLevel="+patch.DeepestLevel+" desiredLevel="+patch.DesiredLevel+" leaves="+patch.LeafCount+" reached="+reached);
                Save("PatchRefined",Capture());
                if(!reached){failures.Add("PlanetSurface の月 B の細分化が、"+refineFrame+" frame で要求の階層に届きませんでした(最深 "+patch.DeepestLevel+" / 要求 "+patch.DesiredLevel+")。");}
                phase=4;buryIndex=0;buryFrame=0;
                if(Environment.GetEnvironmentVariable("MOONTRIPLE_QUICK")=="1"){phase=99;}
            }
        }

        // 3 つの方式の月に、ボールの半分を埋めた画像を撮る(Play Mode。細分化が進んでから)。赤い線(ボールの中心の高さ)と面の境界のずれが、面の高さの誤差。
        // 接触点から 1,500 m の位置(月どうしの隙間が約 1.3 m で、ボールと camera が入る)。下の月に埋めるときは 15 度見下ろし、上の月に埋めるときは 15 度見上げる(光は下から)。
        private sealed class BuryView{
            public string Name;
            public int Contact;
            public bool LowerMoon;
        }

        private static readonly BuryView[] buryViews={
            new BuryView{Name="1_SingleSphere_PlainMoon",Contact=0,LowerMoon=true},
            new BuryView{Name="2_VG_MoonA",Contact=1,LowerMoon=true},
            new BuryView{Name="3_PlanetSurface_MoonB",Contact=1,LowerMoon=false},
        };
        private static int buryIndex;
        private static int buryFrame;
        private const int BuryMinimumFrames=300;
        private const int BuryMaximumFrames=3000;
        private const string BuryFolder="TestResults/Planet/MoonTripleBury";

        // ボールの半分を view の月に埋めて、camera を distance だけ離して(下の月は 15 度見下ろし、上の月は 15 度見上げ)置く。上の月の下面は太陽光の反対側を向いて暗いので、光を下から当てる。
        private static void PlaceBuryView(BuryView view,float distance,float angle=15f,float yaw=180f){
            scripted.Clear();
            demo.SetGaps(0,0);
            demo.SetState(view.Contact,0,0,(long)Math.Round(1500/MoonTripleDemo.UnitMeters),yaw,0f,distance);
            demo.BuryBall(view.LowerMoon);
            float pitch=angle;
            if(!view.LowerMoon){pitch=-angle;}
            demo.FrameBall(yaw,pitch,distance);
            demo.Apply();
            demo.SetKeyLightFromBelow(!view.LowerMoon);
        }

        // 細分化と VG の Cell の生成が済んだか(または待ちすぎたか)。
        private static bool BuryReady(BuryView view,int frame){
            global::Omni.PlanetSurface.PlanetSurfaceDiagnostics patch=demo.MoonB.Lod.Diagnostics;
            bool patchReady=view.Contact==0||patch.DeepestLevel>=patch.DesiredLevel;
            bool vgReady=demo.MoonA.PendingGeneration==0;
            return frame>=BuryMaximumFrames||(frame>=BuryMinimumFrames&&patchReady&&vgReady);
        }

        private static void BuryCaptures(){
            if(buryIndex>=buryViews.Length){phase=5;sweepView=0;sweepStep=0;sweepFrame=0;sweepReady=false;return;}
            BuryView view=buryViews[buryIndex];
            if(buryFrame==0){PlaceBuryView(view,1.5f);}
            global::Omni.PlanetSurface.PlanetSurfaceDiagnostics patch=demo.MoonB.Lod.Diagnostics;
            buryFrame++;
            if(BuryReady(view,buryFrame)){
                Directory.CreateDirectory(BuryFolder);
                File.WriteAllBytes(BuryFolder+"/"+view.Name+".png",Capture().EncodeToPNG());
                report.Add("bury view "+view.Name+": frames="+buryFrame+" patch deepest="+patch.DeepestLevel+"/"+patch.DesiredLevel+" vgPending="+demo.MoonA.PendingGeneration+" saved="+BuryFolder+"/"+view.Name+".png");
                buryIndex++;buryFrame=0;
            }
        }

        // 掃引: 3 つの方式のそれぞれで、ボールを半分埋めた位置から y を動かして撮る(camera はボールの中心から 0.35 m)。
        // ボールの切れ目の周りだけを切り出し、1 枚のシートにまとめる(行 = 方式 1・2・3、列 = y のずれ)。赤い線と面の境界のずれが、y のずれに合わせて動く。
        private static readonly int[] sweepOffsetsMm={-5,-2,0,2,5};
        private const int SweepSettleFrames=40;
        private const int CropWidth=240;
        private const int CropHeight=160;
        private static int sweepView;
        private static int sweepStep;
        private static int sweepFrame;
        private static bool sweepReady;
        private static long sweepBaseY;
        private static readonly List<Color32[]> sweepCrops=new List<Color32[]>();

        private static void SweepCaptures(){
            if(sweepView>=buryViews.Length){
                BuildSweepSheet();
                phase=6;fineView=0;fineStep=0;fineFrame=0;fineReady=false;fineSavedFov=0f;return;
            }
            BuryView view=buryViews[sweepView];
            if(!sweepReady){
                // 半分を埋めた配置にして、細分化と Cell の生成が済むまで待つ。
                if(sweepFrame==0){PlaceBuryView(view,1.5f);}
                sweepFrame++;
                if(BuryReady(view,sweepFrame)){
                    sweepReady=true;sweepBaseY=demo.OffsetY;sweepStep=0;sweepFrame=0;
                }
                return;
            }
            if(sweepFrame==0){
                long y=sweepBaseY+sweepOffsetsMm[sweepStep]*10L;
                float pitch=15f;
                if(!view.LowerMoon){pitch=-15f;}
                demo.SetState(view.Contact,0,y,(long)Math.Round(1500/MoonTripleDemo.UnitMeters),180f,pitch,0.35f);
                demo.Apply();
            }
            sweepFrame++;
            if(sweepFrame<SweepSettleFrames){return;}
            Texture2D image=Capture();
            Directory.CreateDirectory(BuryFolder);
            string offsetText=sweepOffsetsMm[sweepStep].ToString("+0;-0;0",CultureInfo.InvariantCulture);
            File.WriteAllBytes(BuryFolder+"/Sweep_"+view.Name+"_y"+offsetText+"mm.png",image.EncodeToPNG());
            // ボールの中心は画像の中心に写る。その周り(CropWidth × CropHeight)を切り出す。
            Color32[] pixels=image.GetPixels32();
            var crop=new Color32[CropWidth*CropHeight];
            int x0=(image.width-CropWidth)/2;int y0=(image.height-CropHeight)/2;
            for(int y=0;y<CropHeight;y++){
                for(int x=0;x<CropWidth;x++){crop[y*CropWidth+x]=pixels[(y0+y)*image.width+(x0+x)];}
            }
            sweepCrops.Add(crop);
            UnityEngine.Object.DestroyImmediate(image);
            report.Add("sweep "+view.Name+" y"+offsetText+"mm: saved");
            sweepStep++;sweepFrame=0;
            if(sweepStep>=sweepOffsetsMm.Length){sweepView++;sweepReady=false;}
        }

        // 細かい掃引(mm 未満): 方式 2(VG)と方式 3(PlanetSurface)で、camera の画角を 14 度に絞り、ボールを 0.5 mm ずつ動かして、赤い線の見える太さを画素より細かい精度で測る。
        // 太さ t は、ボールの y のずれ o に対して直線 t = a + s·o で変わる(線が面から出る量に比例)。面がボールの中心を通る(t が全体 T の半分)ときの o が、面の高さの理想からの誤差:
        // e = (T/2 − a)/s(正 = 面が理想より高い)。T は、ボールを面から 8 mm 離した画像の太さ。
        private static readonly int[] fineOffsets={-20,-15,-10,-5,0,5,10,15,20};
        private const int FineReferenceUnits=80;
        // 画角 5 度(0.35 m の距離で、縦に約 ±15 mm)。camera はボールの手前の赤道の点を狙うので、角度を変えても線が視野から外れない。1280×960 で撮り、1 画素は約 0.03 mm。
        private const float FineFov=5f;
        private const int FineWidth=1280;
        private const int FineHeight=960;
        private static int fineView;
        private static int fineStep;
        private static int fineFrame;
        private static bool fineReady;
        private static long fineBase;
        private static float fineSavedFov;
        private static float fineAngle=8f;
        private static readonly List<double> fineValues=new List<double>();
        // 赤い線の重みの閾値を変えた値(同じ画像から)。閾値で結果がどれだけ動くか = 測り方の偏りの見積もり。
        private static readonly List<double> fineValuesAlt=new List<double>();

        private static void FineSweep(){
            if(fineView>=2){
                if(fineSavedFov>0f){Camera.main.fieldOfView=fineSavedFov;}
                phase=7;scaleIndex=0;scaleFrame=0;scaleOriginalAtom=null;scaleOriginalMoonA=null;
                return;
            }
            if(fineSavedFov==0f){
                fineSavedFov=Camera.main.fieldOfView;Camera.main.fieldOfView=FineFov;
                // 見下ろす(見上げる)角度(度)。既定は 8 度。画角 14 度では、15 度だとボールの手前の赤道の線が画像の端で切れるので使えない。環境変数 MOONTRIPLE_FINE_ANGLE で変えられる。
                fineAngle=8f;
                string text=Environment.GetEnvironmentVariable("MOONTRIPLE_FINE_ANGLE");
                if(!string.IsNullOrEmpty(text)){fineAngle=float.Parse(text,CultureInfo.InvariantCulture);}
                report.Add("fine sweep angle="+fineAngle.ToString("F1",CultureInfo.InvariantCulture)+" deg, fov="+FineFov+" deg");
            }
            BuryView view=buryViews[1+fineView];
            if(!fineReady){
                if(fineFrame==0){PlaceBuryView(view,0.35f,fineAngle,270f);}
                fineFrame++;
                if(BuryReady(view,fineFrame)){fineReady=true;fineBase=demo.OffsetY;fineStep=0;fineFrame=0;fineValues.Clear();fineValuesAlt.Clear();}
                return;
            }
            int count=fineOffsets.Length+1;
            if(fineFrame==0){
                long o;
                if(fineStep<fineOffsets.Length){o=fineOffsets[fineStep];}
                else if(view.LowerMoon){o=FineReferenceUnits;}
                else{o=-FineReferenceUnits;}
                float pitch=fineAngle;
                if(!view.LowerMoon){pitch=-fineAngle;}
                // camera はボールの +x 側(yaw 270)に置き、向きをボールの +x 側の赤道の点へ向ける(ボールの中心ではなく)。
                // 接触点から離れる向き(z)には、球の面が傾く(1,500 m で L/R ≒ 8.6e-4。ボールの半径 0.11 m で約 0.095 mm の高さの差)ので、z 方向の点では測らない。x 方向には傾きが無い。
                demo.SetState(view.Contact,0,fineBase+o,(long)Math.Round(1500/MoonTripleDemo.UnitMeters),270f,pitch,0.35f);
                double3 ball=demo.BallCenterDouble();
                double3 cameraPosition=demo.CameraPosition;
                double3 target=ball+new double3(MoonTripleLayout.BallRadius,0,0);
                double3 forward=math.normalize(target-cameraPosition);
                float aimYaw=(float)(Math.Atan2(forward.x,forward.z)*180.0/Math.PI);
                float aimPitch=(float)(-Math.Asin(forward.y)*180.0/Math.PI);
                demo.PlaceCamera(cameraPosition,aimYaw,aimPitch);
            }
            fineFrame++;
            if(fineFrame<SweepSettleFrames){return;}
            Texture2D image=Capture(FineWidth,FineHeight);
            fineValues.Add(RedThickness(image,0.25,0.65));
            fineValuesAlt.Add(RedThickness(image,0.45,0.45));
            if(fineStep==fineOffsets.Length||fineStep==fineOffsets.Length/2){
                Directory.CreateDirectory(BuryFolder);
                File.WriteAllBytes(BuryFolder+"/Fine_"+view.Name+"_step"+fineStep+".png",image.EncodeToPNG());
            }
            UnityEngine.Object.DestroyImmediate(image);
            fineStep++;fineFrame=0;
            if(fineStep>=count){
                ReportFine(view,fineValues,"weights 0.25/0.65");
                ReportFine(view,fineValuesAlt,"weights 0.45/0.45 (stricter red)");
                fineView++;fineReady=false;fineFrame=0;
            }
        }

        // 画像の中心の列(中心の左右 4 列を平均)にある、赤い画素の太さ(画素。縁は赤さに応じた重みで数える)。赤 = r が g、b の平均より十分大きい。
        // threshold と span: 赤さ(r − (g+b)/2)/255 を、(赤さ − threshold)/span で 0〜1 の重みにする。
        private static double RedThickness(Texture2D image,double threshold,double span){
            Color32[] pixels=image.GetPixels32();
            int width=image.width;int height=image.height;int centerX=width/2;
            double sum=0;int columns=0;
            for(int x=centerX-4;x<=centerX+4;x++){
                for(int y=0;y<height;y++){
                    Color32 c=pixels[y*width+x];
                    double redness=(c.r-(c.g+c.b)*0.5)/255.0;
                    double weight=(redness-threshold)/span;
                    if(weight<0){weight=0;}
                    if(weight>1){weight=1;}
                    sum+=weight;
                }
                columns++;
            }
            return sum/columns;
        }

        private static void ReportFine(BuryView view,List<double> fineValues,string label){
            int n=fineOffsets.Length;
            double full=fineValues[n];
            var line=new System.Text.StringBuilder();
            for(int i=0;i<n;i++){line.Append(" ").Append((fineOffsets[i]*0.1).ToString("+0.0;-0.0;0.0",CultureInfo.InvariantCulture)).Append("mm:").Append(fineValues[i].ToString("F1",CultureInfo.InvariantCulture));}
            // 全体の 10〜90% の点だけで、直線 t = a + s·o(o は 0.1 mm 単位)に最小二乗で合わせる。
            double sumO=0,sumT=0,sumOO=0,sumOT=0;int used=0;
            for(int i=0;i<n;i++){
                double t=fineValues[i];
                if(t<0.1*full||t>0.9*full){continue;}
                double o=fineOffsets[i];
                sumO+=o;sumT+=t;sumOO+=o*o;sumOT+=o*t;used++;
            }
            if(used<3){
                report.Add("fine sweep "+view.Name+" ["+label+"]: T="+full.ToString("F1",CultureInfo.InvariantCulture)+" px; thickness (px):"+line+" -> not enough points on the slope ("+used+")");
                failures.Add("細かい掃引("+view.Name+")で、線の太さが変化する範囲の点が足りません("+used+")。");
                return;
            }
            double denominator=used*sumOO-sumO*sumO;
            double slope=(used*sumOT-sumO*sumT)/denominator;
            double intercept=(sumT-slope*sumO)/used;
            double errorUnits=(full*0.5-intercept)/slope;
            double residual=0;
            for(int i=0;i<n;i++){
                double t=fineValues[i];
                if(t<0.1*full||t>0.9*full){continue;}
                double d=t-(intercept+slope*fineOffsets[i]);
                residual+=d*d;
            }
            residual=Math.Sqrt(residual/used);
            report.Add("fine sweep "+view.Name+" ["+label+"]: T="+full.ToString("F1",CultureInfo.InvariantCulture)+" px; thickness (px):"+line+
                " | fit over "+used+" points: slope="+(slope*10).ToString("F2",CultureInfo.InvariantCulture)+" px/mm, rms residual="+residual.ToString("F2",CultureInfo.InvariantCulture)+" px"+
                " | surface height error vs ideal = "+(errorUnits*0.1).ToString("+0.000;-0.000;0.000",CultureInfo.InvariantCulture)+" mm (positive = higher than ideal)");
        }

        // 切り出しを 1 枚のシートにまとめる(行 = 方式 1・2・3 を上から、列 = y のずれ)。
        private static void BuildSweepSheet(){
            int columns=sweepOffsetsMm.Length;int rows=buryViews.Length;
            if(sweepCrops.Count!=columns*rows){failures.Add("掃引の画像の数が違います("+sweepCrops.Count+"、期待 "+(columns*rows)+")。");return;}
            var sheet=new Texture2D(columns*CropWidth,rows*CropHeight,TextureFormat.RGBA32,false);
            var pixels=new Color32[sheet.width*sheet.height];
            for(int v=0;v<rows;v++){
                // テクスチャは下から上へ並ぶので、方式 1 を一番上にするため、行を反転する。
                int row=rows-1-v;
                for(int s=0;s<columns;s++){
                    Color32[] crop=sweepCrops[v*columns+s];
                    for(int y=0;y<CropHeight;y++){
                        for(int x=0;x<CropWidth;x++){
                            pixels[(row*CropHeight+y)*sheet.width+s*CropWidth+x]=crop[y*CropWidth+x];
                        }
                    }
                }
            }
            sheet.SetPixels32(pixels);sheet.Apply();
            Directory.CreateDirectory(BuryFolder);
            File.WriteAllBytes(BuryFolder+"/Sweep_Sheet.png",sheet.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(sheet);
            report.Add("sweep sheet: "+BuryFolder+"/Sweep_Sheet.png (rows = 1 single sphere, 2 VG, 3 PlanetSurface; columns = y "+string.Join(", ",sweepOffsetsMm)+" mm)");
            sweepCrops.Clear();
        }

        // 「同じ処理」の比較: 原子(VG の球、半径 1e-10 m)と VG の月 A(半径 1,737.4 km)を、同じ見かけの大きさ(中心から半径の 4 倍の距離、画角 50 度の中に球の見かけの半径 14.5 度)で撮り、
        // 単色(原子 = 黄、月 A = 緑)の円を、倍精度の光線で求めた円と比べる。論理 near は、原子が 10 pm、月 A が通常(0.03 m)。どちらも同じ PlanetTerrainSource の Cell 選択と描画。
        // 解析の円と、描画の円が、1 画素の帯の外で違う画素の数を報告する(許す差 ScaleMismatchTolerance)。
        private const int ScaleMismatchTolerance=50;
        private const string ScaleFolder="TestResults/Planet/MoonTripleScale";
        private static int scaleIndex;
        private static int scaleFrame;
        private static Material scaleOriginalAtom;
        private static Material scaleOriginalMoonA;

        private static Material FlatCopy(Material source,Color tint){
            var material=new Material(source);material.SetFloat("_FlatColor",1);material.SetColor("_FlatTint",tint);
            return material;
        }

        private static void ScaleSilhouette(){
            if(scaleIndex>=2){
                if(scaleOriginalAtom!=null){demo.Atom.Material=scaleOriginalAtom;}
                if(scaleOriginalMoonA!=null){demo.MoonA.Material=scaleOriginalMoonA;}
                MoonTripleValidation.SetVisible(demo.PlainMoon.GetComponent<MeshRenderer>(),true);
                demo.SetScalePreset(0);
                phase=99;return;
            }
            bool isAtom=scaleIndex==0;
            PlanetTerrainSource source;double radius;double3 center;string name;
            if(isAtom){source=demo.Atom;radius=MoonTripleLayout.AtomRadius;center=demo.Layout.AtomCenter;name="atom";}
            else{source=demo.MoonA;radius=demo.Layout.Radius;center=new double3(0,demo.Layout.CenterA,0);name="moonA";}
            if(scaleFrame==0){
                if(scaleOriginalAtom==null){
                    // 単色にした複製に差し替える(元の材質の asset は書き換えない)。
                    scaleOriginalAtom=demo.Atom.Material;demo.Atom.Material=FlatCopy(scaleOriginalAtom,new Color(1f,1f,0.1f,1));
                    scaleOriginalMoonA=demo.MoonA.Material;demo.MoonA.Material=FlatCopy(scaleOriginalMoonA,new Color(0.1f,1f,0.1f,1));
                    // 普通の月(方式 1)は非表示にする。近距離で描画が壊れ(深度も含む)、原子の前に黒い点が出るため。VG の月 A と原子(どちらも VG の処理)だけで比べる。
                    MoonTripleValidation.SetVisible(demo.PlainMoon.GetComponent<MeshRenderer>(),false);
                }
                demo.SetGaps(0,0);
                demo.SetState(0,0,0,Touch(0),180f,0f,1.5f);
                demo.SetScalePreset(isAtom?MoonTripleDemo.ScaleNearMeters.Length-1:0);
                // 球の中心から +z に半径の 4 倍、中心(-z 方向)を向く(yaw 180、pitch 0)。
                demo.PlaceCamera(center+new double3(0,0,4*radius),180f,0f);
            }
            scaleFrame++;
            bool ready=source.PendingGeneration==0&&scaleFrame>=300;
            if(!ready&&scaleFrame<BuryMaximumFrames){return;}
            Texture2D image=Capture();
            Directory.CreateDirectory(ScaleFolder);
            File.WriteAllBytes(ScaleFolder+"/Silhouette_"+name+".png",image.EncodeToPNG());
            double3 cameraPosition=demo.CameraPosition;
            Quaternion rotation=demo.CameraRotation;
            Color32[] pixels=image.GetPixels32();
            int width=image.width;int height=image.height;
            bool isTargetPixel(Color32 c){
                if(isAtom){return c.r>200&&c.g>200&&c.b<90;}
                return c.g>200&&c.r<90&&c.b<90;
            }
            var analytic=new bool[pixels.Length];
            var rendered=new bool[pixels.Length];
            int analyticCount=0;int renderedCount=0;
            for(int y=0;y<height;y++){
                for(int x=0;x<width;x++){
                    int i=y*width+x;
                    double3 direction=ScaleRay(rotation,x,y,width,height);
                    double t=SphereHit(center,radius,cameraPosition,direction);
                    bool hit=t<double.MaxValue;
                    // 月 A は、普通の月と月 B の球が手前にあるときは、見えない(原子では、その 2 つの月は面までの距離が桁違いに遠く、解析の桁が足りないので比べない)。
                    if(hit&&!isAtom){
                        MoonTripleLayout layout=demo.Layout;
                        if(SphereHit(new double3(0,layout.CenterPlain,0),layout.Radius,cameraPosition,direction)<t){hit=false;}
                        if(SphereHit(new double3(0,layout.CenterB,0),layout.Radius,cameraPosition,direction)<t){hit=false;}
                    }
                    analytic[i]=hit;rendered[i]=isTargetPixel(pixels[i]);
                    if(hit){analyticCount++;}
                    if(rendered[i]){renderedCount++;}
                }
            }
            bool[] band=EdgeBand(analytic,width,height);
            int mismatch=0;
            for(int i=0;i<pixels.Length;i++){
                if(rendered[i]!=analytic[i]&&!band[i]){mismatch++;}
            }
            UnityEngine.Object.DestroyImmediate(image);
            report.Add("scale silhouette "+name+": radius="+radius.ToString("G5",CultureInfo.InvariantCulture)+" m camera distance="+(4*radius).ToString("G5",CultureInfo.InvariantCulture)+" m logical near="+demo.LogicalNearMeters.ToString("G4",CultureInfo.InvariantCulture)+
                " m preset="+demo.ScalePreset+" | analytic disk pixels="+analyticCount+" rendered disk pixels="+renderedCount+" mismatch(outside 1px band)="+mismatch+" frames="+scaleFrame+" pending="+source.PendingGeneration);
            if(mismatch>ScaleMismatchTolerance){failures.Add("円の比較("+name+"): 解析と 1 画素の帯の外で "+mismatch+" 画素違います(許す差 "+ScaleMismatchTolerance+")。");}
            if(analyticCount<1000){failures.Add("円の比較("+name+"): 解析の円の画素が少なすぎます("+analyticCount+")。");}
            scaleIndex++;scaleFrame=0;
        }

        private static double3 ScaleRay(Quaternion rotation,int x,int y,int width,int height){
            Camera camera=Camera.main;
            float tangent=Mathf.Tan(camera.fieldOfView*0.5f*Mathf.Deg2Rad);
            float aspect=(float)width/height;
            Vector3 local=new Vector3(((x+0.5f)/width*2-1)*tangent*aspect,((y+0.5f)/height*2-1)*tangent,1);
            Vector3 direction=(rotation*local).normalized;
            return new double3(direction.x,direction.y,direction.z);
        }

        // 光線が球の外側から当たる最初の距離(当たらなければ double.MaxValue)。中心 − camera の差(倍精度)で解く。
        private static double SphereHit(double3 center,double radius,double3 origin,double3 direction){
            double3 relative=center-origin;
            double b=math.dot(direction,relative);
            double c=math.dot(relative,relative)-radius*radius;
            double discriminant=b*b-c;
            if(c>0&&b>0&&discriminant>=0){return c/(b+math.sqrt(discriminant));}
            return double.MaxValue;
        }

        // 値が変わる境から 1 画素以内の画素(4 近傍の境から 3×3)。
        private static bool[] EdgeBand(bool[] mask,int width,int height){
            var band=new bool[mask.Length];
            for(int y=0;y<height;y++){
                for(int x=0;x<width;x++){
                    bool value=mask[y*width+x];
                    bool edge=false;
                    if(x>0&&mask[y*width+x-1]!=value){edge=true;}
                    if(x<width-1&&mask[y*width+x+1]!=value){edge=true;}
                    if(y>0&&mask[(y-1)*width+x]!=value){edge=true;}
                    if(y<height-1&&mask[(y+1)*width+x]!=value){edge=true;}
                    if(!edge){continue;}
                    for(int dy=-1;dy<=1;dy++){
                        for(int dx=-1;dx<=1;dx++){
                            int px=x+dx;int py=y+dy;
                            if(px>=0&&px<width&&py>=0&&py<height){band[py*width+px]=true;}
                        }
                    }
                }
            }
            return band;
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
