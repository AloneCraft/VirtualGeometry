using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using VirtualizedGeometry.Editor;

namespace PlanetSpike.Editor{
    /// <summary>Spike S1: Omni の Radial Lit を .vgshader で変換し、VG 用 pass を DX12 向けにコンパイルできるか調べる。</summary>
    internal static class S1VgShaderSpike{
        private const string VgShaderPath="Packages/com.virtualgeometry.omni/Shaders/RadialGlobalLit.vgshader";
        private const string SourcePath="Packages/com.omni.rendering/Runtime/Sharders/RadialGlobalLit.shader";
        private const string ReportPath="TestResults/Planet/S1VgShader.txt";
        private const string ConvertedPath="TestResults/Planet/S1RadialGlobalLit.converted.shader";

        public static void Run(){
            StringBuilder log=new StringBuilder();
            int errors=0;
            try{
                string text=File.ReadAllText(FileUtil.GetPhysicalPath(SourcePath));
                VirtualGeometryShaderConversion conversion=VirtualGeometryShaderSource.Convert(text,SourcePath,Resolve);
                log.AppendLine("convert.error="+(conversion.Error??"(none)"));
                log.AppendLine("convert.shaderName="+conversion.ShaderName);
                foreach(string dependency in conversion.Dependencies){log.AppendLine("convert.dependency="+dependency);}
                Directory.CreateDirectory("TestResults/Planet");
                if(conversion.Source!=null){File.WriteAllText(ConvertedPath,conversion.Source);}else{errors++;}
                AssetDatabase.ImportAsset(VgShaderPath,ImportAssetOptions.ForceUpdate|ImportAssetOptions.ForceSynchronousImport);
                Shader shader=AssetDatabase.LoadAssetAtPath<Shader>(VgShaderPath);
                if(shader==null){
                    log.AppendLine("shader=null");
                    errors++;
                }else{
                    log.AppendLine("shader.name="+shader.name+" passCount="+shader.passCount);
                    foreach(ShaderMessage message in ShaderUtil.GetShaderMessages(shader)){
                        log.AppendLine("message "+message.severity+" "+message.message+" ("+message.file+":"+message.line+") platform="+message.platform);
                        if(message.severity==ShaderCompilerMessageSeverity.Error){errors++;}
                    }
                    errors+=CompileVgPasses(shader,log);
                }
            }catch(Exception exception){
                log.AppendLine(exception.ToString());
                errors++;
            }
            log.AppendLine("errors="+errors);
            Directory.CreateDirectory("TestResults/Planet");
            File.WriteAllText(ReportPath,log.ToString());
            EditorApplication.Exit(errors==0?0:1);
        }

        private static int CompileVgPasses(Shader shader,StringBuilder log){
            int errors=0;
            ShaderData data=ShaderUtil.GetShaderData(shader);
            for(int subshader=0;subshader<data.SubshaderCount;subshader++){
                ShaderData.Subshader sub=data.GetSubshader(subshader);
                for(int index=0;index<sub.PassCount;index++){
                    ShaderData.Pass pass=sub.GetPass(index);
                    string lightMode=pass.FindTagValue(new ShaderTagId("LightMode")).name;
                    log.AppendLine("pass "+subshader+"/"+index+" name="+pass.Name+" LightMode="+lightMode);
                    if(lightMode!=VirtualGeometryShaderSource.PassName){continue;}
                    foreach(ShaderType type in new[]{ShaderType.Vertex,ShaderType.Fragment}){
                        ShaderData.VariantCompileInfo result=pass.CompileVariant(type,Array.Empty<string>(),ShaderCompilerPlatform.D3D,BuildTarget.StandaloneWindows64,true);
                        int bytes=0;
                        if(result.ShaderData!=null){bytes=result.ShaderData.Length;}
                        log.AppendLine("  "+type+" success="+result.Success+" bytes="+bytes);
                        foreach(ShaderMessage message in result.Messages){
                            log.AppendLine("    "+message.severity+" "+message.message+" ("+message.file+":"+message.line+")");
                            if(message.severity==ShaderCompilerMessageSeverity.Error){errors++;}
                        }
                        if(!result.Success){errors++;}
                    }
                }
            }
            return errors;
        }

        private static string Resolve(string assetPath){
            try{
                return FileUtil.GetPhysicalPath(assetPath);
            }catch(Exception){
                return null;
            }
        }
    }
}
