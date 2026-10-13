using System;
using System.IO;

namespace Omni.PlanetSurface{
    /// <summary>検証用の5種類の診断描画を、1フレームにつき1種類だけ取り出します。</summary>
    internal sealed class SurfaceDiagnosticCaptureSequence{
        private const int LastMode=5;
        private const int IdleFramesBetweenCaptures=16;
        private string sourcePath;
        private int nextMode,idleFrames;

        internal bool IsActive=>this.nextMode!=0;

        /// <summary>進行中の列を上書きせず、新しい通常画像から診断列を開始します。</summary>
        internal bool TryBegin(string path){
            if(string.IsNullOrWhiteSpace(path)||!Path.IsPathRooted(path)){
                throw new ArgumentException("通常画像の絶対パスが必要です。",nameof(path));
            }
            if(this.IsActive){return false;}
            this.sourcePath=path;this.nextMode=1;this.idleFrames=0;return true;
        }

        /// <summary>GPU作業を確実に解放する16フレームを空け、5枚目を返した後は自動的に完了します。</summary>
        internal bool TryTake(out int mode,out string path){
            mode=0;path=null;
            if(!this.IsActive){return false;}
            if(this.idleFrames>0){this.idleFrames--;return false;}
            mode=this.nextMode;
            path=Path.Combine(Path.GetDirectoryName(this.sourcePath),
                Path.GetFileNameWithoutExtension(this.sourcePath)+$"-diagnostic-{mode}.png");
            this.nextMode++;
            if(this.nextMode>LastMode){this.nextMode=0;this.sourcePath=null;this.idleFrames=0;}
            else{this.idleFrames=IdleFramesBetweenCaptures;}
            return true;
        }

        internal void Cancel(){this.nextMode=0;this.sourcePath=null;this.idleFrames=0;}
    }
}
