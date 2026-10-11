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
                        phase=99;
                    }
                }
                if(phase==99){Finish(null);}
            }catch(Exception error){
                Finish(error);
            }
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
