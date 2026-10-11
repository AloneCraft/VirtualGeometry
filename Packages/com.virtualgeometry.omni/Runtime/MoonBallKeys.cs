using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace VirtualGeometry.Omni{
    /// <summary><see cref="MoonBallDemo"/> が読むキー。</summary>
    public enum MoonBallKey{
        Up,Down,Left,Right,PageUp,PageDown,
        Shift,Ctrl,
        Space,Digit1,Digit2,Digit3,Reset,
    }

    /// <summary>
    /// <see cref="MoonBallDemo"/> のキーの入力元。既定は Input System のキーボード(<see cref="MoonBallKeyboard"/>)。
    /// 焦点の無い batchmode では仮想のキーボードのイベントが Input System に届かないので、検証ではこれを差し替えて、キーの割り当てと量を確かめる。
    /// </summary>
    public interface IMoonBallKeys{
        /// <summary>押されている間 true。</summary>
        bool Held(MoonBallKey key);
        /// <summary>押された frame だけ true。</summary>
        bool PressedThisFrame(MoonBallKey key);
    }

    /// <summary>Input System のキーボードを読む。キーボードが無いときは何も押されていない。</summary>
    public sealed class MoonBallKeyboard:IMoonBallKeys{
        public static readonly MoonBallKeyboard Instance=new MoonBallKeyboard();

        public bool Held(MoonBallKey key){
            Keyboard keyboard=Keyboard.current;
            if(keyboard==null){return false;}
            switch(key){
                case MoonBallKey.Shift:return keyboard.leftShiftKey.isPressed||keyboard.rightShiftKey.isPressed;
                case MoonBallKey.Ctrl:return keyboard.leftCtrlKey.isPressed||keyboard.rightCtrlKey.isPressed;
            }
            KeyControl control=Control(keyboard,key);
            return control!=null&&control.isPressed;
        }

        public bool PressedThisFrame(MoonBallKey key){
            Keyboard keyboard=Keyboard.current;
            if(keyboard==null){return false;}
            switch(key){
                case MoonBallKey.Shift:return keyboard.leftShiftKey.wasPressedThisFrame||keyboard.rightShiftKey.wasPressedThisFrame;
                case MoonBallKey.Ctrl:return keyboard.leftCtrlKey.wasPressedThisFrame||keyboard.rightCtrlKey.wasPressedThisFrame;
            }
            KeyControl control=Control(keyboard,key);
            return control!=null&&control.wasPressedThisFrame;
        }

        private static KeyControl Control(Keyboard keyboard,MoonBallKey key){
            switch(key){
                case MoonBallKey.Up:return keyboard.upArrowKey;
                case MoonBallKey.Down:return keyboard.downArrowKey;
                case MoonBallKey.Left:return keyboard.leftArrowKey;
                case MoonBallKey.Right:return keyboard.rightArrowKey;
                case MoonBallKey.PageUp:return keyboard.pageUpKey;
                case MoonBallKey.PageDown:return keyboard.pageDownKey;
                case MoonBallKey.Space:return keyboard.spaceKey;
                case MoonBallKey.Digit1:return keyboard.digit1Key;
                case MoonBallKey.Digit2:return keyboard.digit2Key;
                case MoonBallKey.Digit3:return keyboard.digit3Key;
                case MoonBallKey.Reset:return keyboard.rKey;
            }
            return null;
        }
    }
}
