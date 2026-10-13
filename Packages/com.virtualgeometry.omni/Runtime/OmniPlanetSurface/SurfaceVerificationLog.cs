using System;
using UnityEngine;

namespace Omni.PlanetSurface{
    /// <summary>惑星表面の通常・エラー・検証ログへ、解析タグを保持した日本時間を付けます。</summary>
    public static class SurfaceVerificationLog{
        /// <summary>時刻の正本は渡された瞬間です。端末のローカル設定に依存せずJSTで表記します。</summary>
        public static string Format(string message,DateTimeOffset instant){
            if(message==null){throw new ArgumentNullException(nameof(message));}
            return FormattableString.Invariant($"[{instant.ToOffset(TimeSpan.FromHours(9)):yyyy-MM-dd HH:mm:ss.fff} JST] {message}");
        }
        public static void Info(string message){Debug.Log(Format(message,DateTimeOffset.UtcNow));}
        public static void Error(string message){Debug.LogError(Format(message,DateTimeOffset.UtcNow));}
    }
}
