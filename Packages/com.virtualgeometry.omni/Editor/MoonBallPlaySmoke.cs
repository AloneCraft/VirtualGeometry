using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VirtualizedGeometry.Editor;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// 月とボールのデモ scene を Play Mode で動かして確かめる(batchmode)。
    /// scene を開き、Play Mode に入り、MoonBallDemo が自分で初期化して(Update)、自動の動きでボールが動き、エラーが出ないことを確かめる。
    /// キーは <see cref="IMoonBallKeys"/> を差し替えて送る(焦点の無い batchmode では、Input System の仮想キーボードに積んだイベントが届かなかったため)。
    /// そのため、キーの割り当て・量・Shift/Ctrl の効果は確かめるが、実際のキーボードから Input System を通る経路は確かめない。
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.MoonBallPlaySmoke.Run(月)/ RunGiant
    /// </summary>
    public static class MoonBallPlaySmoke{
        private static string scenePath;
        private static string results;
        private static int frames;
        private static int phase;
        private static MoonBallDemo demo;
        private static double started;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();
        private static long startY;
        private static readonly ScriptedKeys scripted=new ScriptedKeys();
        private static int keyIndex;
        private static int keyFrame;
        private static long beforeX,beforeY,beforeZ;

        // キー入力の確認: (表示名、押すキー、同時に押す修飾キー、期待する量(0.1mm 単位)の x、y、z)。
        private static readonly (string Name,MoonBallKey Key,MoonBallKey[] Modifiers,long X,long Y,long Z)[] KeyCases={
            ("Up",MoonBallKey.Up,new MoonBallKey[0],0,10,0),("Down",MoonBallKey.Down,new MoonBallKey[0],0,-10,0),
            ("Right",MoonBallKey.Right,new MoonBallKey[0],10,0,0),("Left",MoonBallKey.Left,new MoonBallKey[0],-10,0,0),
            ("PageUp",MoonBallKey.PageUp,new MoonBallKey[0],0,0,10),("PageDown",MoonBallKey.PageDown,new MoonBallKey[0],0,0,-10),
            ("Shift+Up",MoonBallKey.Up,new[]{MoonBallKey.Shift},0,100,0),("Ctrl+Up",MoonBallKey.Up,new[]{MoonBallKey.Ctrl},0,1,0),
            // Shift と Ctrl を同時に押したときは、Ctrl(0.1mm)が優先される。
            ("Shift+Ctrl+Up",MoonBallKey.Up,new[]{MoonBallKey.Shift,MoonBallKey.Ctrl},0,1,0),
        };

        /// <summary>押した frame だけ押されている入力元。Tap で押し、Clear で離す。PressedThisFrame は最初に聞かれたときだけ true。</summary>
        private sealed class ScriptedKeys:IMoonBallKeys{
            private readonly HashSet<MoonBallKey> held=new HashSet<MoonBallKey>();
            private readonly HashSet<MoonBallKey> fresh=new HashSet<MoonBallKey>();

            public void Tap(MoonBallKey key){
                this.held.Add(key);this.fresh.Add(key);
            }

            public void Clear(){
                this.held.Clear();this.fresh.Clear();
            }

            public bool Held(MoonBallKey key){
                return this.held.Contains(key);
            }

            public bool PressedThisFrame(MoonBallKey key){
                return this.fresh.Remove(key);
            }
        }

        public static void Run(){
            Begin(MoonBallSettings.Moon.ScenePath,"TestResults/Planet/MoonBallPlay");
        }

        public static void RunGiant(){
            Begin(MoonBallSettings.Giant.ScenePath,"TestResults/Planet/GiantBallPlay");
        }

        private static void Begin(string path,string output){
            scenePath=path;results=output;frames=0;phase=0;demo=null;started=EditorApplication.timeSinceStartup;
            report.Clear();failures.Clear();errors.Clear();
            Directory.CreateDirectory(results);
            Application.logMessageReceived+=Record;
            try{
                MoonBallSceneBuilder.UsePipeline();
                EditorSceneManager.OpenScene(scenePath,OpenSceneMode.Single);
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

        private static void Tick(){
            try{
                if(EditorApplication.timeSinceStartup-started>600){throw new TimeoutException("MoonBall play smoke timeout.");}
                if(!EditorApplication.isPlaying){return;}
                frames++;
                if(phase==0){
                    demo=UnityEngine.Object.FindFirstObjectByType<MoonBallDemo>();
                    if(demo==null){throw new InvalidOperationException("scene に MoonBallDemo がありません。");}
                    if(demo.IsInitialized||frames>600){
                        report.Add("initialized="+demo.IsInitialized+" after "+frames+" frames failure="+(demo.Failure??"(none)"));
                        if(!demo.IsInitialized){failures.Add("MoonBallDemo が初期化されませんでした: "+demo.Failure);phase=99;return;}
                        startY=demo.OffsetY;
                        phase=1;frames=0;
                    }
                }else if(phase==1){
                    // batchmode には Game View が無く、camera は自動では描かれない(選択と生成は描画のたびに進む)ので、毎 frame 撮って描かせる。
                    // 接触の視点が収束するまで待ち、撮影する。
                    if(frames!=240&&frames!=240+300){Capture(320,240);}
                    if(frames==240){
                        Save("PlayContact",Capture());
                        demo.AutoPlay=true;
                    }
                    if(frames==240+300){
                        report.Add("auto play: offset (x,y,z)=("+demo.OffsetX+","+demo.OffsetY+","+demo.OffsetZ+") [0.1mm units]");
                        if(demo.OffsetX==0&&demo.OffsetY==startY&&demo.OffsetZ==0){failures.Add("自動の動きでボールが動きませんでした。");}
                        Save("PlayAuto",Capture());
                        demo.AutoPlay=false;
                        demo.SetOffset(0,0,0);
                        demo.Keys=scripted;
                        keyIndex=0;keyFrame=0;phase=2;
                    }
                }else if(phase==2){
                    Capture(320,240);
                    RunKeyCase();
                }else if(phase==3){
                    Capture(320,240);
                    RunViewKeys();
                }
                if(phase==99){Finish(null);}
            }catch(Exception error){
                Finish(error);
            }
        }

        // 1 つのキーの確認: 押す → 離す → 量を比べる(1 回の押下で、ちょうど 1 回分動くこと)。
        private static void RunKeyCase(){
            if(keyIndex>=KeyCases.Length){phase=3;keyIndex=0;keyFrame=0;scripted.Clear();return;}
            var item=KeyCases[keyIndex];
            if(keyFrame==0){
                beforeX=demo.OffsetX;beforeY=demo.OffsetY;beforeZ=demo.OffsetZ;
                scripted.Tap(item.Key);
                foreach(MoonBallKey modifier in item.Modifiers){scripted.Tap(modifier);}
            }else if(keyFrame==2){
                scripted.Clear();
            }else if(keyFrame==5){
                long x=demo.OffsetX-beforeX,y=demo.OffsetY-beforeY,z=demo.OffsetZ-beforeZ;
                bool ok=x==item.X&&y==item.Y&&z==item.Z;
                report.Add("key "+item.Name+": moved (x,y,z)=("+x+","+y+","+z+") expected ("+item.X+","+item.Y+","+item.Z+") "+(ok?"ok":"NG"));
                if(!ok){failures.Add("キー "+item.Name+" の量が違います: ("+x+","+y+","+z+")、期待 ("+item.X+","+item.Y+","+item.Z+")。");}
                keyIndex++;keyFrame=0;
                return;
            }
            keyFrame++;
        }

        // 視点のキー(1、2、3)と Space、R の確認。
        private static void RunViewKeys(){
            var cases=new (string Name,MoonBallKey Key,Func<bool> Check)[]{
                ("1",MoonBallKey.Digit1,()=>demo.ViewName=="Contact"),
                ("2",MoonBallKey.Digit2,()=>demo.ViewName=="Orbit"),
                ("3",MoonBallKey.Digit3,()=>demo.ViewName=="Zoom"),
                ("Space",MoonBallKey.Space,()=>demo.AutoPlay),
                ("R",MoonBallKey.Reset,()=>!demo.AutoPlay&&demo.OffsetX==0&&demo.OffsetY==0&&demo.OffsetZ==0&&demo.ViewName=="Contact"),
            };
            if(keyIndex>=cases.Length){phase=99;return;}
            var item=cases[keyIndex];
            if(keyFrame==0){scripted.Tap(item.Key);}
            else if(keyFrame==2){scripted.Clear();}
            else if(keyFrame==5){
                bool ok=item.Check();
                report.Add("key "+item.Name+": view="+demo.ViewName+" auto="+demo.AutoPlay+" offset=("+demo.OffsetX+","+demo.OffsetY+","+demo.OffsetZ+") "+(ok?"ok":"NG"));
                if(!ok){failures.Add("キー "+item.Name+" の結果が違います(視点 "+demo.ViewName+"、自動 "+demo.AutoPlay+")。");}
                keyIndex++;keyFrame=0;
                return;
            }
            keyFrame++;
        }

        private static Texture2D Capture(int width=640,int height=480){
            Camera camera=Camera.main;
            if(camera==null){throw new InvalidOperationException("Main Camera がありません。");}
            return M0Validation.Capture(camera,width,height);
        }

        private static void Save(string name,Texture2D image){
            File.WriteAllBytes(results+"/"+name+".png",image.EncodeToPNG());
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Record;
            if(demo!=null){demo.Keys=null;}
            if(error!=null){failures.Add(error.ToString());}
            foreach(string message in errors){failures.Add("Unity logged error: "+message);}
            report.Add("UTC="+DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+" Unity="+Application.unityVersion+" GPU="+SystemInfo.graphicsDeviceName);
            if(failures.Count==0){report.Add("PASSED");}else{report.Add("FAILED");report.AddRange(failures);}
            File.WriteAllLines(results+"/Report.txt",report);
            int code=1;
            if(failures.Count==0){code=0;}
            EditorApplication.Exit(code);
        }
    }
}
