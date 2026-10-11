using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace VirtualGeometry.Omni{
    /// <summary><see cref="PlaneSinkDemo"/> が読むキー。</summary>
    public enum PlaneSinkKey{
        /// <summary>ボールを 1mm 浮かせる / 沈める(押している間は繰り返す)。</summary>
        Raise,Sink,
        /// <summary>camera の向きを基準に水平に動く。</summary>
        Forward,Back,Left,Right,
        /// <summary>camera の周回(yaw、pitch)と距離。</summary>
        YawLeft,YawRight,PitchUp,PitchDown,ZoomIn,ZoomOut,
        /// <summary>量の修飾(Shift で 10 倍、Ctrl で 1/10)。</summary>
        Coarse,Fine,
        /// <summary>左(単純拡大)/ 右(VG)の試験点へ移る、全体を見る。</summary>
        AreaLeft,AreaRight,Overview,
        /// <summary>自動で沈める、初期化、左の面の原点までの距離 D を小さく / 大きく。</summary>
        Auto,Reset,DistanceDown,DistanceUp,
    }

    /// <summary>
    /// <see cref="PlaneSinkDemo"/> のキーの入力元。既定は Input System のキーボードとマウス(<see cref="PlaneSinkKeyboard"/>)。
    /// 焦点の無い batchmode では仮想のキーボードのイベントが届かないので、検証ではこれを差し替えて、キーの割り当てと量を確かめる(<see cref="IMoonBallKeys"/> と同じ)。
    /// </summary>
    public interface IPlaneSinkKeys{
        bool Held(PlaneSinkKey key);
        bool PressedThisFrame(PlaneSinkKey key);
        /// <summary>右ドラッグの移動量(画素)。無ければ 0。</summary>
        Vector2 LookDelta();
        /// <summary>ホイールの量(上が正)。無ければ 0。</summary>
        float ZoomDelta();
    }

    /// <summary>Input System のキーボードとマウスを読む。無いときは何も押されていない。</summary>
    public sealed class PlaneSinkKeyboard:IPlaneSinkKeys{
        public static readonly PlaneSinkKeyboard Instance=new PlaneSinkKeyboard();

        public bool Held(PlaneSinkKey key){
            Keyboard keyboard=Keyboard.current;
            if(keyboard==null){return false;}
            switch(key){
                case PlaneSinkKey.Coarse:return keyboard.leftShiftKey.isPressed||keyboard.rightShiftKey.isPressed;
                case PlaneSinkKey.Fine:return keyboard.leftCtrlKey.isPressed||keyboard.rightCtrlKey.isPressed;
            }
            KeyControl control=Control(keyboard,key);
            return control!=null&&control.isPressed;
        }

        public bool PressedThisFrame(PlaneSinkKey key){
            Keyboard keyboard=Keyboard.current;
            if(keyboard==null){return false;}
            switch(key){
                case PlaneSinkKey.Coarse:return keyboard.leftShiftKey.wasPressedThisFrame||keyboard.rightShiftKey.wasPressedThisFrame;
                case PlaneSinkKey.Fine:return keyboard.leftCtrlKey.wasPressedThisFrame||keyboard.rightCtrlKey.wasPressedThisFrame;
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

        private static KeyControl Control(Keyboard keyboard,PlaneSinkKey key){
            switch(key){
                case PlaneSinkKey.Raise:return keyboard.upArrowKey;
                case PlaneSinkKey.Sink:return keyboard.downArrowKey;
                case PlaneSinkKey.Forward:return keyboard.wKey;
                case PlaneSinkKey.Back:return keyboard.sKey;
                case PlaneSinkKey.Left:return keyboard.aKey;
                case PlaneSinkKey.Right:return keyboard.dKey;
                case PlaneSinkKey.YawLeft:return keyboard.leftArrowKey;
                case PlaneSinkKey.YawRight:return keyboard.rightArrowKey;
                case PlaneSinkKey.PitchUp:return keyboard.pageUpKey;
                case PlaneSinkKey.PitchDown:return keyboard.pageDownKey;
                case PlaneSinkKey.ZoomIn:return keyboard.homeKey;
                case PlaneSinkKey.ZoomOut:return keyboard.endKey;
                case PlaneSinkKey.AreaLeft:return keyboard.digit1Key;
                case PlaneSinkKey.AreaRight:return keyboard.digit2Key;
                case PlaneSinkKey.Overview:return keyboard.digit3Key;
                case PlaneSinkKey.Auto:return keyboard.spaceKey;
                case PlaneSinkKey.Reset:return keyboard.rKey;
                case PlaneSinkKey.DistanceDown:return keyboard.leftBracketKey;
                case PlaneSinkKey.DistanceUp:return keyboard.rightBracketKey;
            }
            return null;
        }
    }
}
