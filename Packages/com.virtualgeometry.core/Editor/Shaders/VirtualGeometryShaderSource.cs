using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VirtualizedGeometry.Editor{
    /// <summary>変換結果。失敗時はErrorに理由を入れ、Sourceはnullにする。</summary>
    public sealed class VirtualGeometryShaderConversion{
        public string Source;public string Error;public string ShaderName;
        public readonly List<string> Dependencies=new List<string>();
    }

    /// <summary>
    /// URPのShaderLabソースから、元の全passに「頂点入力だけをVGのbufferへ差し替えたforward pass」を1つ加えたソースを作る。
    /// fragment処理・keyword・Propertiesは元shaderのものをそのまま使うため、Materialの見た目は通常Meshと同じになる。
    /// 対象はUniversalPipelineのforward pass(UniversalForward/UniversalForwardOnly/LightModeなし)で、Deferred専用・BRP・HDRP・tessellation/geometry stageは扱わない。
    /// </summary>
    public static class VirtualGeometryShaderSource{
        public const string PassName=VirtualGeometryMaterials.PassName;
        public const string ShaderPrefix=VirtualGeometryMaterials.ShaderPrefix;
        private const string VertexInputInclude="Packages/com.virtualgeometry.core/Shaders/VirtualGeometryVertexInput.hlsl";

        private sealed class Block{public string Kind;public int Start;public int Open;public int End;public Block Parent;}
        private readonly struct Program{
            public readonly int Start;public readonly int End;public readonly string Kind;
            public Program(int start,int end,string kind){this.Start=start;this.End=end;this.Kind=kind;}
        }

        /// <param name="text">元のShaderLabソース(.shaderまたはShader Graphの生成コード)。</param>
        /// <param name="sourceAssetPath">相対includeの基準になるasset path。生成コードではnull。</param>
        /// <param name="resolvePhysical">"Assets/…"や"Packages/…"のasset pathを実ファイルのpathへ変換する。</param>
        public static VirtualGeometryShaderConversion Convert(string text,string sourceAssetPath,Func<string,string> resolvePhysical){
            var result=new VirtualGeometryShaderConversion();
            try{
                result.Source=ConvertCore(text,sourceAssetPath,resolvePhysical,result);
            }catch(ShaderConversionException error){
                result.Source=null;result.Error=error.Message;
            }
            return result;
        }

        private sealed class ShaderConversionException:Exception{
            public ShaderConversionException(string message):base(message){}
        }

        private static string ConvertCore(string text,string sourceAssetPath,Func<string,string> resolvePhysical,VirtualGeometryShaderConversion result){
            string sourceDirectory=null;
            if(!string.IsNullOrEmpty(sourceAssetPath)){sourceDirectory=Path.GetDirectoryName(sourceAssetPath).Replace('\\','/');}
            text=AbsolutizeIncludes(text,sourceDirectory);
            Match name=Regex.Match(text,"\\bShader\\s+\"([^\"]+)\"",RegexOptions.IgnoreCase);
            if(!name.Success){throw new ShaderConversionException("Shader名の宣言が見つかりません。");}
            List<Program> programs=FindPrograms(text);
            List<Block> blocks=FindBlocks(text,programs);
            Block pass=SelectForwardPass(text,blocks,programs);
            Program program=ProgramIn(pass,programs);
            if(program.Kind!="HLSLPROGRAM"){throw new ShaderConversionException("forward passがHLSLPROGRAMではありません(CGPROGRAMはBuilt-in Render Pipeline用のため非対応)。");}
            string programText=text.Substring(program.Start,program.End-program.Start);
            if(Regex.IsMatch(programText,"#pragma\\s+(geometry|hull|domain)\\b")){throw new ShaderConversionException("geometry/tessellation stageを使うshaderは非対応です。");}
            Match vertex=Regex.Match(programText,"#pragma\\s+vertex\\s+(\\w+)");
            if(!vertex.Success){throw new ShaderConversionException("forward passに#pragma vertexがありません。");}
            string vertexName=vertex.Groups[1].Value;

            // vertex関数とAttributes structは、pass本体・HLSLINCLUDE・再帰的なincludeのどこかにある。
            var searched=new StringBuilder();
            foreach(Program candidate in programs){
                if(candidate.Kind=="HLSLINCLUDE"){searched.Append(text,candidate.Start,candidate.End-candidate.Start).Append('\n');}
            }
            searched.Append(programText);
            var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string expanded=ExpandIncludes(searched.ToString(),resolvePhysical,visited,0);
            result.Dependencies.AddRange(visited);
            // 戻り値にsemanticを持つ形(float4 vert(appdata v):SV_POSITION)も受け付け、生成するVGVertexにも同じsemanticを付ける。
            Match function=Regex.Match(expanded,"\\b(\\w+)\\s+"+Regex.Escape(vertexName)+"\\s*\\(\\s*(?:in\\s+)?(\\w+)\\s+\\w+\\s*\\)\\s*(:\\s*\\w+\\s*)?\\{");
            if(!function.Success){throw new ShaderConversionException("vertex関数 "+vertexName+" を「戻り値 "+vertexName+"(構造体 引数)」の形で見つけられません。");}
            string returnType=function.Groups[1].Value;string attributesType=function.Groups[2].Value;string returnSemantic=function.Groups[3].Value.Trim();
            Match structure=Regex.Match(expanded,"\\bstruct\\s+"+Regex.Escape(attributesType)+"\\s*\\{(.*?)\\}\\s*;",RegexOptions.Singleline);
            if(!structure.Success){throw new ShaderConversionException("vertex入力struct "+attributesType+" の定義が見つかりません。");}
            string adapter=BuildAdapter(structure.Groups[1].Value,attributesType,returnType,returnSemantic,vertexName);

            string vgProgram=programText;
            vgProgram=Regex.Replace(vgProgram,"#pragma\\s+vertex\\s+\\w+","#pragma vertex VGVertex");
            vgProgram=Regex.Replace(vgProgram,"#pragma\\s+target\\s+[\\w\\.]+","#pragma target 4.5");
            if(!Regex.IsMatch(vgProgram,"#pragma\\s+target")){vgProgram="\n            #pragma target 4.5"+vgProgram;}
            vgProgram=vgProgram+"\n"+adapter+"\n            ";
            string passText=text.Substring(pass.Start,pass.End-pass.Start);
            int programOffset=program.Start-pass.Start;
            string header=passText.Substring(0,programOffset);
            string footer=passText.Substring(programOffset+(program.End-program.Start));
            header=RetagPass(header);
            string vgPass=header+vgProgram+footer;

            var output=new StringBuilder(text.Length+vgPass.Length+64);
            output.Append(text,0,pass.End);
            output.Append("\n\n        // Virtual Geometry: 元のforward passを頂点入力だけVG bufferへ差し替えて複製したpass。\n        ");
            output.Append(vgPass);
            output.Append(text,pass.End,text.Length-pass.End);
            string converted=output.ToString();
            result.ShaderName=ShaderPrefix+name.Groups[1].Value;
            converted=converted.Substring(0,name.Index)+"Shader \""+result.ShaderName+"\""+converted.Substring(name.Index+name.Length);
            return converted;
        }

        /// <summary>生成shaderは別のasset pathからcompileされるため、相対includeを元ファイル基準のasset pathへ直す。</summary>
        private static string AbsolutizeIncludes(string text,string sourceDirectory){
            if(sourceDirectory==null){return text;}
            return Regex.Replace(text,"(#include(?:_with_pragmas)?\\s+\")([^\"]+)(\")",match=>{
                string path=match.Groups[2].Value.Replace('\\','/');
                if(path.StartsWith("Assets/",StringComparison.Ordinal)||path.StartsWith("Packages/",StringComparison.Ordinal)){return match.Value;}
                string combined=CombineAssetPath(sourceDirectory,path);
                return match.Groups[1].Value+combined+match.Groups[3].Value;
            });
        }

        private static string CombineAssetPath(string directory,string relative){
            var parts=new List<string>(directory.Split('/'));
            foreach(string part in relative.Split('/')){
                if(part=="."||part.Length==0){continue;}
                if(part==".."){if(parts.Count>0){parts.RemoveAt(parts.Count-1);}continue;}
                parts.Add(part);
            }
            return string.Join("/",parts);
        }

        private static string ExpandIncludes(string text,Func<string,string> resolvePhysical,HashSet<string> visited,int depth){
            if(depth>32){return text;}
            var output=new StringBuilder(text);
            foreach(Match include in Regex.Matches(text,"#include(?:_with_pragmas)?\\s+\"([^\"]+)\"")){
                string assetPath=include.Groups[1].Value.Replace('\\','/');
                if(!visited.Add(assetPath)){continue;}
                string physical=resolvePhysical(assetPath);
                if(string.IsNullOrEmpty(physical)||!File.Exists(physical)){continue;}
                string included=File.ReadAllText(physical);
                string directory=Path.GetDirectoryName(assetPath).Replace('\\','/');
                included=AbsolutizeIncludes(included,directory);
                output.Append('\n').Append(ExpandIncludes(included,resolvePhysical,visited,depth+1));
            }
            return output.ToString();
        }

        /// <summary>struct本体を1行ずつ代入文へ変換する。前処理行は残し、field宣言はsemanticに応じたVG頂点値を入れる。</summary>
        private static string BuildAdapter(string body,string attributesType,string returnType,string returnSemantic,string vertexName){
            var code=new StringBuilder();
            code.Append("            #include \"").Append(VertexInputInclude).Append("\"\n");
            code.Append("            void VGFillAttributes(VGVertexData vg,out ").Append(attributesType).Append(" attributes)\n            {\n");
            code.Append("                attributes=(").Append(attributesType).Append(")0;\n");
            var field=new Regex("^\\s*(?:(?:linear|centroid|nointerpolation|noperspective|sample|precise)\\s+)*(\\w+)\\s+(\\w+)\\s*:\\s*(\\w+)\\s*;");
            foreach(string raw in body.Split('\n')){
                string line=StripComment(raw).Trim();
                if(line.Length==0){continue;}
                if(line.StartsWith("#",StringComparison.Ordinal)){code.Append(line).Append('\n');continue;}
                Match match=field.Match(line);
                if(!match.Success){continue;}
                string assignment=Assignment(match.Groups[1].Value,match.Groups[2].Value,match.Groups[3].Value);
                if(assignment!=null){code.Append("                ").Append(assignment).Append('\n');}
            }
            code.Append("            }\n");
            code.Append("            ").Append(returnType).Append(" VGVertex(uint vgVertexId:SV_VertexID)").Append(returnSemantic).Append("\n            {\n");
            code.Append("                ").Append(attributesType).Append(" attributes;\n");
            code.Append("                VGFillAttributes(VGLoadVertexData(vgVertexId),attributes);\n");
            code.Append("                return ").Append(vertexName).Append("(attributes);\n            }\n");
            return code.ToString();
        }

        private static string StripComment(string line){
            int comment=line.IndexOf("//",StringComparison.Ordinal);
            if(comment>=0){return line.Substring(0,comment);}
            return line;
        }

        private static string Assignment(string type,string name,string semantic){
            string upper=semantic.ToUpperInvariant();
            if(upper=="SV_VERTEXID"||upper=="VERTEXID_SEMANTIC"){return "attributes."+name+"=vg.vertexId;";}
            string value=null;
            if(upper=="POSITION"||upper=="POSITION0"){value="float4(vg.positionOS,1)";}
            else if(upper=="NORMAL"||upper=="NORMAL0"){value="float4(vg.normalOS,0)";}
            else if(upper=="TANGENT"||upper=="TANGENT0"){value="vg.tangentOS";}
            else if(upper=="TEXCOORD0"){value="float4(vg.uv0,0,0)";}
            else if(upper=="COLOR"||upper=="COLOR0"){value="float4(1,1,1,1)";}
            // UV1以降(lightmap等)、instance id、その他はVG頂点に存在しないため0のままにする。
            if(value==null){return null;}
            return "attributes."+name+"=("+type+")("+value+Swizzle(type)+");";
        }

        private static string Swizzle(string type){
            char last=type[type.Length-1];
            if(last=='2'){return ".xy";}
            if(last=='3'){return ".xyz";}
            if(last=='4'){return string.Empty;}
            return ".x";
        }

        private static string RetagPass(string header){
            const RegexOptions keyword=RegexOptions.IgnoreCase;
            if(Regex.IsMatch(header,"\\bName\\s+\"[^\"]*\"",keyword)){header=Regex.Replace(header,"\\bName\\s+\"[^\"]*\"","Name \""+PassName+"\"",keyword);}
            else{header=InsertAfterOpenBrace(header,"\n            Name \""+PassName+"\"");}
            if(Regex.IsMatch(header,"\"LightMode\"\\s*=\\s*\"[^\"]*\"")){header=Regex.Replace(header,"\"LightMode\"\\s*=\\s*\"[^\"]*\"","\"LightMode\"=\""+PassName+"\"");}
            else if(Regex.IsMatch(header,"\\bTags\\s*\\{",keyword)){header=Regex.Replace(header,"\\bTags\\s*\\{","Tags{\"LightMode\"=\""+PassName+"\" ",keyword);}
            else{header=InsertAfterOpenBrace(header,"\n            Tags{\"LightMode\"=\""+PassName+"\"}");}
            return header;
        }

        private static string InsertAfterOpenBrace(string text,string insert){
            int open=text.IndexOf('{');
            return text.Substring(0,open+1)+insert+text.Substring(open+1);
        }

        /// <summary>
        /// "RenderPipeline"="UniversalPipeline"のSubShaderを優先し、無ければRenderPipelineタグの無いSubShaderも対象にする(URPではタグは省略可能)。
        /// 別pipelineを指定したSubShader、Built-inのForwardBase等のLightModeは対象外。
        /// </summary>
        private static Block SelectForwardPass(string text,List<Block> blocks,List<Program> programs){
            Block tagged=FindForwardPass(text,blocks,programs,true,out bool anyTagged);
            if(tagged!=null){return tagged;}
            Block untagged=FindForwardPass(text,blocks,programs,false,out bool anyUntagged);
            if(untagged!=null){return untagged;}
            if(Regex.IsMatch(text,"#pragma\\s+surface\\b")){throw new ShaderConversionException("Surface Shader(#pragma surface)はBuilt-in Render Pipeline専用のため非対応です。");}
            if(!anyTagged&&!anyUntagged){throw new ShaderConversionException("URP用のSubShaderがありません(HDRP等、別のRender Pipeline用のshaderは非対応)。");}
            throw new ShaderConversionException("URPのforward pass(LightModeがUniversalForward / UniversalForwardOnly / なし)がありません(Deferred専用、Built-inのForwardBase等のshaderは非対応)。");
        }

        private static Block FindForwardPass(string text,List<Block> blocks,List<Program> programs,bool requireUniversalTag,out bool anySubShader){
            Block untagged=null;Block forwardOnly=null;anySubShader=false;
            foreach(Block block in blocks){
                if(block.Kind!="Pass"){continue;}
                Block subShader=block.Parent;
                while(subShader!=null&&subShader.Kind!="SubShader"){subShader=subShader.Parent;}
                if(subShader==null){continue;}
                string tags=HeaderText(text,subShader,programs,blocks)+HeaderText(text,block,programs,blocks);
                Match pipeline=Regex.Match(tags,"\"RenderPipeline\"\\s*=\\s*\"([^\"]*)\"");
                if(requireUniversalTag){if(!pipeline.Success||pipeline.Groups[1].Value!="UniversalPipeline"){continue;}}
                else if(pipeline.Success){continue;}
                anySubShader=true;
                string header=HeaderText(text,block,programs,blocks);
                Match lightMode=Regex.Match(header,"\"LightMode\"\\s*=\\s*\"([^\"]*)\"");
                if(!lightMode.Success){if(untagged==null){untagged=block;}continue;}
                string mode=lightMode.Groups[1].Value;
                if(mode=="UniversalForward"){return block;}
                if(mode=="UniversalForwardOnly"&&forwardOnly==null){forwardOnly=block;}
                if(mode=="SRPDefaultUnlit"&&untagged==null){untagged=block;}
            }
            if(forwardOnly!=null){return forwardOnly;}
            return untagged;
        }

        /// <summary>block自身の見出し部分(子blockとprogramを除いた文字列)。Tagsの判定に使う。</summary>
        private static string HeaderText(string text,Block block,List<Program> programs,List<Block> blocks){
            var output=new StringBuilder();int cursor=block.Open+1;
            var cuts=new List<KeyValuePair<int,int>>();
            foreach(Program program in programs){if(program.Start>block.Open&&program.End<=block.End){cuts.Add(new KeyValuePair<int,int>(program.Start,program.End));}}
            foreach(Block child in blocks){if(child.Parent==block&&child.Kind!="Tags"){cuts.Add(new KeyValuePair<int,int>(child.Start,child.End));}}
            cuts.Sort((left,right)=>left.Key.CompareTo(right.Key));
            foreach(var cut in cuts){
                if(cut.Key<cursor){continue;}
                output.Append(text,cursor,cut.Key-cursor);cursor=cut.Value;
            }
            if(cursor<block.End){output.Append(text,cursor,block.End-cursor);}
            return output.ToString();
        }

        private static Program ProgramIn(Block pass,List<Program> programs){
            foreach(Program program in programs){
                if(program.Start>pass.Open&&program.End<=pass.End&&program.Kind!="HLSLINCLUDE"&&program.Kind!="CGINCLUDE"){return program;}
            }
            throw new ShaderConversionException("forward passにshader programがありません。");
        }

        /// <summary>HLSLPROGRAM/CGPROGRAM/…INCLUDEの本体範囲(キーワード直後から終了キーワード直前まで)。</summary>
        private static List<Program> FindPrograms(string text){
            var result=new List<Program>();
            foreach(Match start in Regex.Matches(text,"\\b(HLSLPROGRAM|HLSLINCLUDE|CGPROGRAM|CGINCLUDE|GLSLPROGRAM)\\b")){
                if(IsInsideComment(text,start.Index)){continue;}
                if(result.Count>0&&start.Index<result[result.Count-1].End){continue;}
                string kind=start.Groups[1].Value;string terminator="ENDHLSL";
                if(kind.StartsWith("CG",StringComparison.Ordinal)){terminator="ENDCG";}
                if(kind.StartsWith("GLSL",StringComparison.Ordinal)){terminator="ENDGLSL";}
                Match end=new Regex("\\b"+terminator+"\\b").Match(text,start.Index+start.Length);
                if(!end.Success){throw new ShaderConversionException(kind+" に対応する "+terminator+" がありません。");}
                result.Add(new Program(start.Index+start.Length,end.Index,kind));
            }
            return result;
        }

        private static bool IsInsideComment(string text,int index){
            int lineStart=text.LastIndexOf('\n',Math.Max(0,index-1))+1;
            int comment=text.IndexOf("//",lineStart,StringComparison.Ordinal);
            if(comment>=0&&comment<index){return true;}
            int open=text.LastIndexOf("/*",index,StringComparison.Ordinal);
            if(open<0){return false;}
            int close=text.LastIndexOf("*/",index,StringComparison.Ordinal);
            return close<open;
        }

        /// <summary>ShaderLabのキーワードは大文字小文字を区別しない(URP付属shaderにも"Subshader"がある)ため、判定に使う種類名を正規化する。</summary>
        private static string CanonicalKind(string word){
            if(word==null){return string.Empty;}
            foreach(string known in new[]{"Shader","SubShader","Pass","Tags","Properties"}){
                if(string.Equals(word,known,StringComparison.OrdinalIgnoreCase)){return known;}
            }
            return word;
        }

        /// <summary>ShaderLabの{}構造を、comment・文字列・shader programを飛ばして走査する。</summary>
        private static List<Block> FindBlocks(string text,List<Program> programs){
            var result=new List<Block>();var stack=new Stack<Block>();
            int programIndex=0;string lastWord=null;int lastWordStart=0;
            for(int i=0;i<text.Length;i++){
                if(programIndex<programs.Count&&i==programs[programIndex].Start){i=programs[programIndex].End;programIndex++;continue;}
                char c=text[i];
                if(c=='/'&&i+1<text.Length&&text[i+1]=='/'){int end=text.IndexOf('\n',i);if(end<0){break;}i=end;continue;}
                if(c=='/'&&i+1<text.Length&&text[i+1]=='*'){int end=text.IndexOf("*/",i+2,StringComparison.Ordinal);if(end<0){break;}i=end+1;continue;}
                if(c=='"'){int end=text.IndexOf('"',i+1);if(end<0){break;}i=end;continue;}
                if(char.IsLetter(c)||c=='_'){
                    int start=i;while(i+1<text.Length&&(char.IsLetterOrDigit(text[i+1])||text[i+1]=='_')){i++;}
                    lastWord=text.Substring(start,i-start+1);lastWordStart=start;continue;
                }
                if(c=='{'){
                    Block parent=null;if(stack.Count>0){parent=stack.Peek();}
                    string kind=CanonicalKind(lastWord);int blockStart=lastWordStart;
                    if(lastWord==null){blockStart=i;}
                    var block=new Block{Kind=kind,Start=blockStart,Open=i,Parent=parent};stack.Push(block);lastWord=null;continue;
                }
                if(c=='}'){
                    if(stack.Count==0){throw new ShaderConversionException("ShaderLabの{}が対応していません。");}
                    Block block=stack.Pop();block.End=i+1;result.Add(block);lastWord=null;continue;
                }
                if(!char.IsWhiteSpace(c)){lastWord=null;}
            }
            if(stack.Count>0){throw new ShaderConversionException("ShaderLabの{}が閉じていません。");}
            result.Sort((left,right)=>left.Start.CompareTo(right.Start));
            return result;
        }
    }
}
