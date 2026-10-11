using System;
using System.Globalization;
using UnityEngine;
using VirtualGeometry.World;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// 検証用。コマンドラインの <c>-heightShiftThreshold &lt;m&gt;</c> で、Cell の原点を持ち上げる高さのしきい値を上書きする。
    /// 0 なら高さが 0 でない全ての Cell を持ち上げる(持ち上げの読み手の直し忘れを、振幅の小さい既存の検証でも H≠0 で通して見つけるため)。
    /// 大きな値(例 1e9)なら持ち上げない(持ち上げ前と頂点がビット単位で同じことを確かめるため)。指定が無ければ何もしない。
    /// 保存する scene(BuildScene)には適用しない(上書きが scene asset に焼き込まれるので)。メモリ上で作る scene の検証だけが、作った後に <see cref="ApplyToScene"/> か <see cref="Apply"/> を呼ぶ。
    /// </summary>
    internal static class HeightShiftOverride{
        private const string Option="-heightShiftThreshold";

        public static void Apply(PlanetTerrainSource source){
            if(source==null||!TryRead(out float threshold)){return;}
            source.HeightShiftThreshold=threshold;
        }

        /// <summary>開いている scene の全ての <see cref="PlanetTerrainSource"/>(無効なものも含む)に <see cref="Apply"/> する。</summary>
        public static void ApplyToScene(){
            if(!TryRead(out float _)){return;}
            foreach(PlanetTerrainSource source in UnityEngine.Object.FindObjectsByType<PlanetTerrainSource>(FindObjectsInactive.Include,FindObjectsSortMode.None)){
                Apply(source);
            }
        }

        /// <summary>指定があり、有限で 0 以上の値なら true(NaN と負の値は「指定なし」)。</summary>
        public static bool TryRead(out float threshold){
            threshold=0f;
            string[] arguments=Environment.GetCommandLineArgs();
            for(int i=0;i<arguments.Length-1;i++){
                if(arguments[i]==Option){
                    return float.TryParse(arguments[i+1],NumberStyles.Float,CultureInfo.InvariantCulture,out threshold)&&!float.IsNaN(threshold)&&threshold>=0f;
                }
            }
            return false;
        }

        /// <summary>report に残す文字列(指定が無いときは "default")。</summary>
        public static string Describe(){
            if(TryRead(out float threshold)){
                return "override="+threshold.ToString("G",CultureInfo.InvariantCulture);
            }
            return "default";
        }
    }
}
