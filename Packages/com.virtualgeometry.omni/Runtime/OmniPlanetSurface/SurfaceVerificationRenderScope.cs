using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Omni.PlanetSurface{
    /// <summary>
    /// 月面専用検証シーンが存続する間だけ、等倍描画のURP複製を所有します。
    /// 地形プロファイルとは独立した計測条件です。共有アセットを変更・保存しません。
    /// </summary>
    public sealed class SurfaceVerificationRenderScope:IDisposable{
        private readonly int qualityLevel;
        private readonly RenderPipelineAsset previous;
        private UniversalRenderPipelineAsset owned;

        public SurfaceVerificationRenderScope(){
            this.qualityLevel=QualitySettings.GetQualityLevel();
            this.previous=QualitySettings.renderPipeline;
            RenderPipelineAsset source=this.previous;
            if(source==null){source=GraphicsSettings.defaultRenderPipeline;}
            if(!(source is UniversalRenderPipelineAsset urp)){
                throw new InvalidOperationException("月面の描画解像度検証にはURPが必要です。");
            }
            this.owned=UnityEngine.Object.Instantiate(urp);
            this.owned.name=urp.name+" (Surface Native Resolution)";
            this.owned.hideFlags=HideFlags.HideAndDontSave;
            this.owned.renderScale=1f;
            QualitySettings.renderPipeline=this.owned;
        }

        /// <summary>別の所有者による変更を保持し、検証開始時の品質レベルだけを復元します。</summary>
        public void Dispose(){
            if(this.owned==null){return;}
            if(QualitySettings.GetRenderPipelineAssetAt(this.qualityLevel)==this.owned){
                int currentLevel=QualitySettings.GetQualityLevel();
                try{
                    // Unityの公開setterは現在の品質レベル用です。同一呼出し内で元レベルへ戻します。
                    if(currentLevel!=this.qualityLevel){QualitySettings.SetQualityLevel(this.qualityLevel,false);}
                    QualitySettings.renderPipeline=this.previous;
                }finally{
                    if(currentLevel!=this.qualityLevel){QualitySettings.SetQualityLevel(currentLevel,false);}
                }
            }
            if(Application.isPlaying){UnityEngine.Object.Destroy(this.owned);}
            else{UnityEngine.Object.DestroyImmediate(this.owned);}
            this.owned=null;
        }
    }
}
