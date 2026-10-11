using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace VirtualGeometry.Omni.Editor{
    /// <summary>
    /// 検証用。読み戻した頂点のハッシュ(<c>RealMoonValidation</c> の <c>vertexHash=</c>)を、基準のファイルと自動で比べる。
    /// コマンドライン <c>-vertexHashBaseline &lt;file&gt;</c> と <c>-vertexHashMode record|compare|auto</c>(既定 auto)。
    /// - record: 今回の値でファイルを書く
    /// - compare: ファイルと比べる(ファイルが無ければ失敗)
    /// - auto: ファイルがあれば比べ、無ければ書く
    /// ハッシュは GPU とドライバーで変わるので、ファイルの先頭の環境(GPU 名、API、Unity の版、持ち上げの設定、データ、既定でない pool の大きさ)が違うときは、比べずに「環境違いのため未比較」と report に書く(失敗にはしない)。
    /// 指定が無ければ何もしない。
    /// </summary>
    internal static class VertexHashBaseline{
        private const string PathOption="-vertexHashBaseline";
        private const string ModeOption="-vertexHashMode";
        private const string EnvironmentPrefix="# environment=";

        private static readonly SortedDictionary<string,uint> current=new SortedDictionary<string,uint>(StringComparer.Ordinal);
        private static string path;
        private static string mode;
        private static string environment;
        private static bool enabled;

        /// <summary>検証の開始時に呼ぶ。environment は基準が通用する環境を表す文字列。</summary>
        public static void Begin(string environmentText){
            current.Clear();
            enabled=false;path=ReadValue(PathOption);
            if(string.IsNullOrEmpty(path)){return;}
            mode=ReadValue(ModeOption);
            if(string.IsNullOrEmpty(mode)){mode="auto";}
            environment=environmentText;
            enabled=true;
        }

        public static void Record(string name,uint hash){
            if(!enabled){return;}
            current[name]=hash;
        }

        /// <summary>検証の終了時に呼ぶ。比べた結果を report と failures に足し、record ならファイルを書く。</summary>
        public static void Complete(List<string> report,List<string> failures){
            if(!enabled){return;}
            enabled=false;
            bool exists=File.Exists(path);
            bool write=mode=="record"||(mode=="auto"&&!exists);
            bool compare=mode=="compare"||(mode=="auto"&&exists);
            if(compare){
                if(!exists){
                    failures.Add("vertexHash: 基準のファイルがありません(compare): "+path);
                }else{
                    CompareWith(report,failures);
                }
            }
            if(write){
                var lines=new List<string>{EnvironmentPrefix+environment};
                foreach(KeyValuePair<string,uint> pair in current){lines.Add(pair.Key+"\t"+pair.Value.ToString("X8",CultureInfo.InvariantCulture));}
                string directory=Path.GetDirectoryName(Path.GetFullPath(path));
                if(!string.IsNullOrEmpty(directory)){Directory.CreateDirectory(directory);}
                File.WriteAllLines(path,lines,new UTF8Encoding(false));
                report.Add("vertexHash: 基準を書きました("+current.Count+" 件): "+path);
            }
        }

        private static void CompareWith(List<string> report,List<string> failures){
            string[] lines=File.ReadAllLines(path,Encoding.UTF8);
            string baselineEnvironment="";
            var baseline=new Dictionary<string,uint>(StringComparer.Ordinal);
            foreach(string line in lines){
                if(line.StartsWith(EnvironmentPrefix,StringComparison.Ordinal)){baselineEnvironment=line.Substring(EnvironmentPrefix.Length);continue;}
                if(line.Length==0||line[0]=='#'){continue;}
                int tab=line.IndexOf('\t');
                if(tab<0){continue;}
                if(uint.TryParse(line.Substring(tab+1),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out uint value)){baseline[line.Substring(0,tab)]=value;}
            }
            if(baselineEnvironment!=environment){
                report.Add("vertexHash: 環境違いのため未比較(基準: "+baselineEnvironment+" / 今回: "+environment+")");
                return;
            }
            int matched=0;int different=0;int missing=0;
            foreach(KeyValuePair<string,uint> pair in current){
                if(!baseline.TryGetValue(pair.Key,out uint expected)){missing++;continue;}
                if(expected==pair.Value){matched++;continue;}
                different++;
                failures.Add("vertexHash: "+pair.Key+" が基準と違います(基準 "+expected.ToString("X8",CultureInfo.InvariantCulture)+"、今回 "+pair.Value.ToString("X8",CultureInfo.InvariantCulture)+")。");
            }
            report.Add("vertexHash: 基準と比べました(一致 "+matched+"、不一致 "+different+"、基準に無い "+missing+")");
        }

        private static string ReadValue(string option){
            string[] arguments=Environment.GetCommandLineArgs();
            for(int i=0;i<arguments.Length-1;i++){
                if(arguments[i]==option){return arguments[i+1];}
            }
            return null;
        }
    }
}
