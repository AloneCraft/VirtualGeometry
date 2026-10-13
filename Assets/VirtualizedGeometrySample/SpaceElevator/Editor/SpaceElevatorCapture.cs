using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualizedGeometry.Samples.SpaceElevator.Editor{
    /// <summary>
    /// Blender のカメラ設定(se_ground_anchor.py の setup_world_and_camera)を Unity のカメラへ写し、表示どおりの sRGB 画像を撮る。
    /// 参照画像「地上が、宇宙へのはじまり。」(1448×1086)と同じ構図の SE_GA_Camera と、配置確認用の SE_GA_Camera_Aerial を扱う。
    /// 使用先: SpaceElevatorValidation(撮影・参照画像との横並び)、SpaceElevatorSceneBuilder(SpaceElevator.unity の Main Camera)。
    /// </summary>
    public static class SpaceElevatorCapture{
        /// <summary>Blender のカメラ1台分の設定。位置・注視は Blender のワールド座標(m、Z上)。</summary>
        public sealed class BlenderCamera{
            public string Name;
            /// <summary>カメラ位置(Blender ワールド座標)。</summary>
            public Vector3 Location;
            /// <summary>視線方向(Blender ワールド座標、正規化不要)。</summary>
            public Vector3 Forward;
            /// <summary>上方向(Blender ワールド座標)。</summary>
            public Vector3 Up;
            /// <summary>焦点距離(mm)。</summary>
            public float Lens;
            /// <summary>センサー幅(mm)。sensor_fit AUTO で横長の画像なので幅に合わせる。</summary>
            public float SensorWidth;
            /// <summary>Blender の shift_y。センサーの長辺(ここでは幅)を1とする単位。</summary>
            public float ShiftY;
            public float Near;
            public float Far;
            /// <summary>撮影解像度。SE_GA_Camera は参照画像と同じ 1448×1086。</summary>
            public int Width;
            public int Height;
        }

        /// <summary>参照画像と同じ正面構図(se_ground_anchor.py:1069-1079。cam.location=(0,-430,3.4)、rotation=(90°,0,0) で +Y を向く)。</summary>
        public static readonly BlenderCamera Hero=new BlenderCamera{
            Name="SE_GA_Camera",Location=new Vector3(0f,-430f,3.4f),Forward=new Vector3(0f,1f,0f),Up=new Vector3(0f,0f,1f),
            Lens=24f,SensorWidth=36f,ShiftY=0.2f,Near=0.5f,Far=120000f,Width=1448,Height=1086
        };

        /// <summary>配置確認用の俯瞰(se_ground_anchor.py:1081-1090。(0,0,110) を注視)。</summary>
        public static readonly BlenderCamera Aerial=new BlenderCamera{
            Name="SE_GA_Camera_Aerial",Location=new Vector3(-620f,-760f,420f),Forward=new Vector3(620f,760f,-310f),Up=new Vector3(0f,0f,1f),
            Lens=28f,SensorWidth=36f,ShiftY=0f,Near=1f,Far=120000f,Width=1920,Height=1440
        };

        /// <summary>
        /// Blender のカメラ設定を Unity の Physical Camera へ写す。prefab を原点・無回転に置いた座標系を前提にする。
        /// </summary>
        /// <param name="camera">設定先の Unity カメラ。</param>
        /// <param name="source">Blender 側のカメラ設定。</param>
        public static void Apply(Camera camera,BlenderCamera source){
            Vector3 position=SpaceElevatorPatternSpace.BlenderToWorld(source.Location);
            Vector3 forward=SpaceElevatorPatternSpace.BlenderToWorld(source.Forward).normalized;
            Vector3 up=SpaceElevatorPatternSpace.BlenderToWorld(source.Up).normalized;
            camera.transform.SetPositionAndRotation(position,Quaternion.LookRotation(forward,up));
            camera.usePhysicalProperties=true;
            camera.focalLength=source.Lens;
            float sensorHeight=source.SensorWidth*source.Height/source.Width;
            camera.sensorSize=new Vector2(source.SensorWidth,sensorHeight);
            camera.gateFit=Camera.GateFitMode.Horizontal;
            // Blender の shift は長辺(幅)基準、Unity の lensShift は各軸のセンサー長基準。
            camera.lensShift=new Vector2(0f,source.ShiftY*source.SensorWidth/sensorHeight);
            camera.nearClipPlane=source.Near;
            camera.farClipPlane=source.Far;
        }

        /// <summary>
        /// カメラを指定解像度で描き、画面表示と同じ sRGB の画像として返す。後処理(トーンマップ・ブルーム)を含む。
        /// M0Validation.Capture は Linear の RT を読むため PNG が暗くなる。参照画像と並べる比較ではこちらを使う。
        /// </summary>
        /// <param name="camera">撮影するカメラ。targetTexture は一時的に差し替えて元に戻す。</param>
        /// <param name="width">幅(px)。</param>
        /// <param name="height">高さ(px)。</param>
        public static Texture2D CaptureSrgb(Camera camera,int width,int height){
            RenderTexture target=RenderTexture.GetTemporary(width,height,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB,1);
            RenderTexture previous=RenderTexture.active;
            RenderTexture oldTarget=camera.targetTexture;
            try{
                camera.targetTexture=target;
                var request=new RenderPipeline.StandardRequest{destination=target};
                if(!RenderPipeline.SupportsRenderRequest(camera,request)){
                    throw new InvalidOperationException("Active pipeline does not support StandardRequest.");
                }
                RenderPipeline.SubmitRenderRequest(camera,request);
                RenderTexture.active=target;
                var image=new Texture2D(width,height,TextureFormat.RGBA32,false,false);
                image.ReadPixels(new Rect(0,0,width,height),0,0);
                image.Apply();
                return image;
            }finally{
                camera.targetTexture=oldTarget;
                RenderTexture.active=previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        /// <summary>画像を PNG で保存する。フォルダが無ければ作る。</summary>
        /// <param name="image">保存する画像。</param>
        /// <param name="path">保存先(project 相対、例: TestResults/SpaceElevator/before/Hero.png)。</param>
        public static void SavePng(Texture2D image,string path){
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path,image.EncodeToPNG());
        }
    }
}
