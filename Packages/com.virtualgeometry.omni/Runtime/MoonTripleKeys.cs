using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace VirtualGeometry.Omni{
    /// <summary><see cref="MoonTripleDemo"/> が読むキー。</summary>
    public enum MoonTripleKey{
        /// <summary>ボールを動かす(押している間は繰り返す): x、上下 y(上の月 / 下の月へ)、手前奥 z。</summary>
        BallXMinus,BallXPlus,BallUp,BallDown,BallZMinus,BallZPlus,
        /// <summary>camera を飛ばす(向きに対して前後・左右、上下は世界の上下)。Home・End は前後(向いている方向へ)。</summary>
        FlyForward,FlyBack,FlyLeft,FlyRight,FlyUp,FlyDown,ZoomIn,ZoomOut,
        /// <summary>camera をボールに向け直す(今の向きを保ち、ボールから既定の距離に置く)。</summary>
        Frame,
        /// <summary>量の修飾(Shift で 10 倍、Ctrl で 1/10)。</summary>
        Coarse,Fine,
        /// <summary>接触点 0(普通 - VG)・接触点 1(VG - VG)へテレポート、ボールが両側の面に接する距離へ、接触点そのものへ。</summary>
        Contact0,Contact1,Touch,Exact,
        /// <summary>ボールの中心を、今の接触点の下の月 / 上の月の理想の面に置く(ボールの半分がその月に埋まる)。</summary>
        BuryLower,BuryUpper,
        /// <summary>論理ニアクリップのプリセットを、一段細かく(7)/ 粗く(8)。原子サイズの VG の球へテレポート(9)。</summary>
        ScaleFiner,ScaleCoarser,TeleportAtom,
        /// <summary>今の接触点の隙間を小さく / 大きく(1mm。Shift で 10mm、Ctrl で 0.1mm)、初期化。</summary>
        GapDown,GapUp,Reset,
    }

    /// <summary>
    /// <see cref="MoonTripleDemo"/> のキーの入力元。既定は Input System のキーボードとマウス(<see cref="MoonTripleKeyboard"/>)。
    /// 焦点の無い batchmode では仮想のキーボードのイベントが届かないので、検証ではこれを差し替えて、キーの割り当てと量を確かめる(<see cref="IPlaneSinkKeys"/> と同じ)。
    /// </summary>
    public interface IMoonTripleKeys{
        bool Held(MoonTripleKey key);
        bool PressedThisFrame(MoonTripleKey key);
        /// <summary>右ドラッグの移動量(画素)。無ければ 0。</summary>
        Vector2 LookDelta();
        /// <summary>ホイールの量(上が正)。無ければ 0。</summary>
        float ZoomDelta();
    }

    /// <summary>Input System のキーボードとマウスを読む。無いときは何も押されていない。</summary>
    public sealed class MoonTripleKeyboard:IMoonTripleKeys{
        public static readonly MoonTripleKeyboard Instance=new MoonTripleKeyboard();

        public bool Held(MoonTripleKey key){
            Keyboard keyboard=Keyboard.current;
            if(keyboard==null){return false;}
            switch(key){
                case MoonTripleKey.Coarse:return keyboard.leftShiftKey.isPressed||keyboard.rightShiftKey.isPressed;
                case MoonTripleKey.Fine:return keyboard.leftCtrlKey.isPressed||keyboard.rightCtrlKey.isPressed;
            }
            KeyControl control=Control(keyboard,key);
            return control!=null&&control.isPressed;
        }

        public bool PressedThisFrame(MoonTripleKey key){
            Keyboard keyboard=Keyboard.current;
            if(keyboard==null){return false;}
            switch(key){
                case MoonTripleKey.Coarse:return keyboard.leftShiftKey.wasPressedThisFrame||keyboard.rightShiftKey.wasPressedThisFrame;
                case MoonTripleKey.Fine:return keyboard.leftCtrlKey.wasPressedThisFrame||keyboard.rightCtrlKey.wasPressedThisFrame;
            }
            KeyControl control=Control(keyboard,key);
            return control!=null&&control.wasPressedThisFrame;
        }

        public Vector2 LookDelta(){
            Mouse mouse=Mouse.current;
            if(mouse==null||!mouse.rightButton.isPressed){return Vector2.zero;}
            return mouse.delta.ReadValue();
        }

        public float ZoomDelta(){
            Mouse mouse=Mouse.current;
            if(mouse==null){return 0f;}
            return mouse.scroll.ReadValue().y/120f;
        }

        private static KeyControl Control(Keyboard keyboard,MoonTripleKey key){
            switch(key){
                case MoonTripleKey.BallXMinus:return keyboard.leftArrowKey;
                case MoonTripleKey.BallXPlus:return keyboard.rightArrowKey;
                case MoonTripleKey.BallUp:return keyboard.upArrowKey;
                case MoonTripleKey.BallDown:return keyboard.downArrowKey;
                case MoonTripleKey.BallZMinus:return keyboard.pageDownKey;
                case MoonTripleKey.BallZPlus:return keyboard.pageUpKey;
                case MoonTripleKey.FlyForward:return keyboard.wKey;
                case MoonTripleKey.FlyBack:return keyboard.sKey;
                case MoonTripleKey.FlyLeft:return keyboard.aKey;
                case MoonTripleKey.FlyRight:return keyboard.dKey;
                case MoonTripleKey.FlyUp:return keyboard.eKey;
                case MoonTripleKey.FlyDown:return keyboard.qKey;
                case MoonTripleKey.Frame:return keyboard.cKey;
                case MoonTripleKey.ZoomIn:return keyboard.homeKey;
                case MoonTripleKey.ZoomOut:return keyboard.endKey;
                case MoonTripleKey.Contact0:return keyboard.digit1Key;
                case MoonTripleKey.Contact1:return keyboard.digit2Key;
                case MoonTripleKey.Touch:return keyboard.digit3Key;
                case MoonTripleKey.Exact:return keyboard.digit4Key;
                case MoonTripleKey.BuryLower:return keyboard.digit5Key;
                case MoonTripleKey.BuryUpper:return keyboard.digit6Key;
                case MoonTripleKey.ScaleFiner:return keyboard.digit7Key;
                case MoonTripleKey.ScaleCoarser:return keyboard.digit8Key;
                case MoonTripleKey.TeleportAtom:return keyboard.digit9Key;
                case MoonTripleKey.GapDown:return keyboard.leftBracketKey;
                case MoonTripleKey.GapUp:return keyboard.rightBracketKey;
                case MoonTripleKey.Reset:return keyboard.rKey;
            }
            return null;
        }
    }
}
