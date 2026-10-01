using System;
using System.Collections.Generic;
using System.Linq;

namespace VirtualizedGeometry.Editor{
    /// <summary>Playerへ収録するarchive集合とEditor専用native依存の禁止境界。</summary>
    public static class VirtualGeometryBuildInclusion{
        public static string[] DistinctArchives(IEnumerable<string> paths){return paths.Where(path=>!string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path=>path,StringComparer.Ordinal).ToArray();}
        public static bool IsForbiddenPlayerPath(string path){
            string normalized=path.Replace('\\','/');
            return normalized.IndexOf("meshoptimizer",StringComparison.OrdinalIgnoreCase)>=0||normalized.IndexOf("Native~/",StringComparison.OrdinalIgnoreCase)>=0;
        }
    }
}