using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VirtualizedGeometry.Editor;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// Play していない Edit Mode で、MoonTriple シーンが、Play 開始と同じ絵(原点のボールと camera の既定の視点)を Game camera に出すかを確かめる(batchmode)。
    /// シーンを開き(Play しない)、<see cref="MoonTripleDemo"/> の Edit Mode の更新(EditModeTick)が初期化するのを待ち、VG の Cell の生成が進むよう数十 frame 回して、Main Camera を撮って判定する。
    /// 判定: 画像が黒一色でない(明るい画素の割合)、ボールの赤い線(赤道の線)の画素が一定数以上。画像は TestResults/Planet/MoonTripleEdit に保存する。
    /// 実行: -batchmode -force-d3d12 -executeMethod VirtualGeometry.Omni.Editor.MoonTripleEditModeCheck.Run
    /// </summary>
    public static class MoonTripleEditModeCheck{
        private const string Results="TestResults/Planet/MoonTripleEdit";
        private const int Width=640;
        private const int Height=480;
        // 明るい(黒一色でない)画素の割合の下限と、赤い線の画素の数の下限。Play 開始の絵(ボールが画面の中心に大きく写る)の実測の約半分(回帰の検出用。仕様ではない)。
        private const double MinimumLitFraction=0.15;
        private const int MinimumRedPixels=200;
        private const int InitializeLimit=900;
        private const int SettleFrames=240;

        private static IEnumerator steps;
        private static double started;
        private static MoonTripleDemo demo;
        private static readonly List<string> report=new List<string>();
        private static readonly List<string> failures=new List<string>();
        private static readonly List<string> errors=new List<string>();

        public static void Run(){
            report.Clear();failures.Clear();errors.Clear();started=EditorApplication.timeSinceStartup;
            Directory.CreateDirectory(Results);
            Application.logMessageReceived+=Record;
            try{
                MoonBallSceneBuilder.UsePipeline();
                EditorSceneManager.OpenScene(MoonTripleSceneBuilder.ScenePath,OpenSceneMode.Single);
                steps=Steps();
                EditorApplication.update+=Tick;
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
                if(EditorApplication.timeSinceStartup-started>900){throw new TimeoutException("MoonTriple Edit Mode check timeout.");}
                // Edit Mode では Time.frameCount が進まず、VG の Cell の生成が止まるので、毎 tick プレイヤーループを回す(MoonTripleValidation と同じ)。
                EditorApplication.QueuePlayerLoopUpdate();
                if(!steps.MoveNext()){Finish(null);}
            }catch(Exception error){
                Finish(error);
            }
        }

        private static IEnumerator Steps(){
            report.Add("Application.isPlaying="+Application.isPlaying);
            demo=UnityEngine.Object.FindAnyObjectByType<MoonTripleDemo>();
            if(demo==null){throw new InvalidOperationException("scene に MoonTripleDemo がありません。");}
            Camera camera=Camera.main;
            if(camera==null){throw new InvalidOperationException("Main Camera がありません。");}
            // Edit Mode の更新(MoonTripleDemo.EditModeTick)が初期化するまで待つ。
            int waited=0;
            while(!demo.IsInitialized){
                if(waited++>InitializeLimit){throw new InvalidOperationException("Edit Mode で MoonTripleDemo が初期化されません: "+(demo.Failure??"(失敗の理由なし)"));}
                M0Validation.Capture(camera,Width/2,Height/2).Destroy();
                yield return null;
            }
            report.Add("initialized in Edit Mode after "+waited+" ticks, failure="+(demo.Failure??"(none)"));
            // VG の Cell の生成と、PlanetSurface の分割が進むよう、数十 frame 回す(毎 frame 撮って描画させる)。
            for(int i=0;i<SettleFrames;i++){
                M0Validation.Capture(camera,Width/2,Height/2).Destroy();
                yield return null;
            }
            Texture2D image=M0Validation.Capture(camera,Width,Height);
            File.WriteAllBytes(Results+"/EditMode.png",image.EncodeToPNG());
            Color32[] pixels=image.GetPixels32();
            int lit=0;int red=0;
            foreach(Color32 c in pixels){
                if(c.r+c.g+c.b>60){lit++;}
                if(c.r>200&&c.g<90&&c.b<90){red++;}
            }
            double litFraction=(double)lit/pixels.Length;
            UnityEngine.Object.DestroyImmediate(image);
            report.Add("camera position="+demo.CameraPosition+" selectedFace="+demo.SelectedFace+" scalePreset="+demo.ScalePreset);
            report.Add("lit pixel fraction="+litFraction.ToString("F3",CultureInfo.InvariantCulture)+" (min "+MinimumLitFraction.ToString("F2",CultureInfo.InvariantCulture)+") red pixels="+red+" (min "+MinimumRedPixels+")");
            if(litFraction<MinimumLitFraction){failures.Add("Edit Mode の Game camera の画像がほぼ黒です(明るい画素 "+litFraction.ToString("F3",CultureInfo.InvariantCulture)+")。");}
            if(red<MinimumRedPixels){failures.Add("ボールの赤い線が写っていません(赤い画素 "+red+")。");}
        }

        private static void Destroy(this Texture2D image){
            if(image!=null){UnityEngine.Object.DestroyImmediate(image);}
        }

        private static void Finish(Exception error){
            EditorApplication.update-=Tick;Application.logMessageReceived-=Record;
            if(error!=null){failures.Add(error.ToString());}
            foreach(string message in errors){failures.Add("Unity logged error: "+message);}
            report.Add("UTC="+DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+" Unity="+Application.unityVersion+" GPU="+SystemInfo.graphicsDeviceName+" API="+SystemInfo.graphicsDeviceType);
            if(failures.Count==0){report.Add("PASSED");}else{report.Add("FAILED");report.AddRange(failures);}
            Directory.CreateDirectory(Results);
            File.WriteAllLines(Results+"/Report.txt",report);
            EditorApplication.Exit(failures.Count==0?0:1);
        }
    }
}
