using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace VirtualGeometry.Omni{
    /// <summary><see cref="PlaneSeamDemo"/> が読むキー。</summary>
    public enum PlaneSeamKey{
        /// <summary>右の面(VG)を 1mm 上げる / 下げる(押している間は繰り返す)。</summary>
        Raise,Lower,
        /// <summary>camera を向きを基準に水平に動かす(W A S D)と、上下(E Q)。</summary>
        Forward,Back,Left,Right,Up,Down,
        /// <summary>camera の向き(yaw、pitch)。</summary>
        YawLeft,YawRight,PitchUp,PitchDown,
        /// <summary>量の修飾(Shift で 10 倍、Ctrl で 1/10)。</summary>
        Coarse,Fine,
        /// <summary>視点: 継ぎ目の近く、左の面の上、右の面の上。</summary>
        ViewSeam,ViewLeft,ViewRight,
        /// <summary>初期化、左の面の原点までの距離 D を小さく / 大きく。</summary>
        Reset,DistanceDown,DistanceUp,
    }

    /// <summary>
    /// <see cref="PlaneSeamDemo"/> のキーの入力元。既定は Input System のキーボードとマウス(<see cref="PlaneSeamKeyboard"/>)。
    /// 焦点の無い batchmode では仮想のキーボードのイベントが届かないので、検証ではこれを差し替えて、キーの割り当てと量を確かめる(<see cref="IPlaneSinkKeys"/> と同じ)。
    /// </summary>
    public interface IPlaneSeamKeys{
        bool Held(PlaneSeamKey key);
        bool PressedThisFrame(PlaneSeamKey key);
        /// <summary>右ドラッグの移動量(画素)。無ければ 0。</summary>
        Vector2 LookDelta();
        /// <summary>ホイールの量(上が正)。無ければ 0。</summary>
        float ZoomDelta();
    }

    /// <summary>Input System のキーボードとマウスを読む。無いときは何も押されていない。</summary>
    public sealed class PlaneSeamKeyboard:IPlaneSeamKeys{
        public static readonly PlaneSeamKeyboard Instance=new PlaneSeamKeyboard();

        public bool Held(PlaneSeamKey key){
            Keyboard keyboard=Keyboard.current;
            if(keyboard==null){return false;}
            switch(key){
                case PlaneSeamKey.Coarse:return keyboard.leftShiftKey.isPressed||keyboard.rightShiftKey.isPressed;
                case PlaneSeamKey.Fine:return keyboard.leftCtrlKey.isPressed||keyboard.rightCtrlKey.isPressed;
            }
            KeyControl control=Control(keyboard,key);
            return control!=null&&control.isPressed;
        }

        public bool PressedThisFrame(PlaneSeamKey key){
            Keyboard keyboard=Keyboard.current;
            if(keyboard==null){return false;}
            switch(key){
                case PlaneSeamKey.Coarse:return keyboard.leftShiftKey.wasPressedThisFrame||keyboard.rightShiftKey.wasPressedThisFrame;
                case PlaneSeamKey.Fine:return keyboard.leftCtrlKey.wasPressedThisFrame||keyboard.rightCtrlKey.wasPressedThisFrame;
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

        private static KeyControl Control(Keyboard keyboard,PlaneSeamKey key){
            switch(key){
                case PlaneSeamKey.Raise:return keyboard.upArrowKey;
                case PlaneSeamKey.Lower:return keyboard.downArrowKey;
                case PlaneSeamKey.Forward:return keyboard.wKey;
                case PlaneSeamKey.Back:return keyboard.sKey;
                case PlaneSeamKey.Left:return keyboard.aKey;
                case PlaneSeamKey.Right:return keyboard.dKey;
                case PlaneSeamKey.Up:return keyboard.eKey;
                case PlaneSeamKey.Down:return keyboard.qKey;
                case PlaneSeamKey.YawLeft:return keyboard.leftArrowKey;
                case PlaneSeamKey.YawRight:return keyboard.rightArrowKey;
                case PlaneSeamKey.PitchUp:return keyboard.pageUpKey;
                case PlaneSeamKey.PitchDown:return keyboard.pageDownKey;
                case PlaneSeamKey.ViewSeam:return keyboard.digit1Key;
                case PlaneSeamKey.ViewLeft:return keyboard.digit2Key;
                case PlaneSeamKey.ViewRight:return keyboard.digit3Key;
                case PlaneSeamKey.Reset:return keyboard.rKey;
                case PlaneSeamKey.DistanceDown:return keyboard.leftBracketKey;
                case PlaneSeamKey.DistanceUp:return keyboard.rightBracketKey;
            }
            return null;
        }
    }
}
