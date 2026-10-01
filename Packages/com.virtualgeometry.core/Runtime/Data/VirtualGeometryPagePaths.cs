using System;
using System.IO;
using UnityEngine;

namespace VirtualizedGeometry{
    /// <summary>
    /// page archiveの置き場所を解決する。Editorではモデルのインポート時に生成した変換キャッシュ(Library/VirtualGeometry)を読み、
    /// Playerではbuild時に参照分だけ収録したStreamingAssets/VirtualGeometryを読む。相対パスはどちらも"VirtualGeometry/&lt;名前&gt;.vgpage"。
    /// </summary>
    public static class VirtualGeometryPagePaths{
        public const string Folder="VirtualGeometry";

        /// <summary>相対パスの基点。Editorはproject直下のLibrary、PlayerはStreamingAssets。</summary>
        public static string Root{
            get{
                if(Application.isEditor){return Path.GetFullPath(Path.Combine(Application.dataPath,"..","Library"));}
                return Path.GetFullPath(Application.streamingAssetsPath);
            }
        }

        /// <summary>相対パスを絶対パスへ解決する。基点の外を指すパスは拒否する。</summary>
        public static string Resolve(string relativePath){
            if(string.IsNullOrWhiteSpace(relativePath)){throw new InvalidDataException("Page path is empty.");}
            string root=Root;
            string fullPath=Path.GetFullPath(Path.Combine(root,relativePath));
            string prefix=root.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(!fullPath.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)){throw new InvalidDataException("Page path escapes "+root+".");}
            return fullPath;
        }
    }
}
