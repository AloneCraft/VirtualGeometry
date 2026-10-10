using System.Collections.Generic;
using UnityEngine;

namespace VirtualizedGeometry{
    /// <summary>
    /// RendererのMaterialから、VG描画に使うMaterialとpassを決める。
    /// Materialのshaderが.vgshader由来でVG passを持てばそのまま使う。持たなければ「VirtualGeometry/元shader名」のshader
    /// (同梱のURP標準分、または利用者が作った.vgshader)を探し、内部Materialへpropertyとkeywordを写して使う。
    /// </summary>
    public static class VirtualGeometryMaterials{
        /// <summary>.vgshaderが追加するVG passのName/LightMode。URPはこのLightModeを通常Meshに描かない。</summary>
        public const string PassName="VirtualGeometryForward";
        /// <summary>.vgshaderが生成するshader名の接頭辞。</summary>
        public const string ShaderPrefix="VirtualGeometry/";
        private const string BundledResources="VirtualGeometryShaders";
        private static readonly Dictionary<Material,Material> derived=new Dictionary<Material,Material>();
        private static bool bundledLoaded;

        /// <summary>VG描画に使うMaterialとpass index。描けない場合は理由を返す。</summary>
        public static bool TryResolve(Material source,out Material material,out int pass,out string reason){
            material=null;pass=-1;reason=null;
            if(source==null){reason="Materialがありません。";return false;}
            if(source.renderQueue>2500){reason="半透明のMaterial("+source.name+")はVirtual Geometryでは描けません。";return false;}
            pass=FindVirtualPass(source);
            if(pass>=0){material=source;return true;}
            Shader shader=FindVirtualShader(source.shader);
            if(shader==null){
                reason="shader \""+source.shader.name+"\" に対応するVirtual Geometry Shaderがありません。shaderを右クリック→「Virtual Geometry Shaderを作成」で作り、Materialのshaderを「"+ShaderPrefix+source.shader.name+"」にしてください。";
                return false;
            }
            if(!derived.TryGetValue(source,out material)||material==null||material.shader!=shader){
                if(material!=null){DestroyObject(material);}
                material=new Material(shader){name=source.name+" (Virtual Geometry)",hideFlags=HideFlags.HideAndDontSave};derived[source]=material;
            }
            // 元Materialの編集をそのまま反映するため、描画ごとに値とkeywordを写す。
            material.CopyPropertiesFromMaterial(source);material.shaderKeywords=source.shaderKeywords;material.renderQueue=source.renderQueue;
            pass=material.FindPass(PassName);
            if(pass<0){reason="shader \""+shader.name+"\" にVG passがありません。";return false;}
            return true;
        }

        /// <summary>描画可否の判定だけを行う。内部Materialは作らない。</summary>
        public static bool IsSupported(Material source,out string reason){
            reason=null;
            if(source==null){reason="Materialがありません。";return false;}
            if(source.renderQueue>2500){reason="半透明のMaterial("+source.name+")はVirtual Geometryでは描けません。";return false;}
            if(FindVirtualPass(source)>=0){return true;}
            if(FindVirtualShader(source.shader)!=null){return true;}
            reason="shader \""+source.shader.name+"\" に対応するVirtual Geometry Shaderがありません。shaderを右クリック→「Virtual Geometry Shaderを作成」で作り、Materialのshaderを「"+ShaderPrefix+source.shader.name+"」にしてください。";
            return false;
        }

        /// <summary>不透明で、対応するVirtual Geometry Shaderがまだ無いMaterialか(.vgshaderを作れば描けるもの)。</summary>
        public static bool NeedsVirtualShader(Material source){
            if(source==null||source.renderQueue>2500||source.FindPass(PassName)>=0){return false;}
            return FindVirtualShader(source.shader)==null;
        }

        private static readonly Dictionary<Shader,Shader> virtualShaders=new Dictionary<Shader,Shader>();
        // FindPassは名前の文字列比較になり、描画判定は部品×slot×cameraごとに毎frame呼ばれるため、shaderごとに結果を覚える。
        // shaderの編集で番号が変わりうるので、覚えた値は一定のframeで捨てて引き直す。
        private const int PassCacheFrames=120;
        private static readonly Dictionary<Shader,KeyValuePair<int,int>> virtualPasses=new Dictionary<Shader,KeyValuePair<int,int>>();
        private static int FindVirtualPass(Material source){
            Shader shader=source.shader;
            if(shader==null){return -1;}
            int frame=Time.frameCount;
            if(virtualPasses.TryGetValue(shader,out KeyValuePair<int,int> cached)&&frame-cached.Value<PassCacheFrames&&frame>=cached.Value){return cached.Key;}
            int pass=source.FindPass(PassName);
            virtualPasses[shader]=new KeyValuePair<int,int>(pass,frame);
            return pass;
        }

        // 描画判定はcamera・instance・slotごとに毎frame呼ばれるため、見つかった対応shaderを覚える。
        // 見つからない結果は覚えない(Editorで後から.vgshaderを作ったときにすぐ反映するため)。削除されたshaderはnull比較で捨てる。
        private static Shader FindVirtualShader(Shader source){
            if(source==null){return null;}
            if(virtualShaders.TryGetValue(source,out Shader cached)&&cached!=null){return cached;}
            if(!bundledLoaded){
                // 同梱の.vgshaderはResources配下にあり、Player buildへ含まれる。読み込んでおくとShader.Findで見つかる。
                Resources.LoadAll<Shader>(BundledResources);bundledLoaded=true;
            }
            Shader found=Shader.Find(ShaderPrefix+source.name);
            if(found!=null){virtualShaders[source]=found;}else{virtualShaders.Remove(source);}
            return found;
        }

        private static void DestroyObject(Object target){
            if(Application.isPlaying){Object.Destroy(target);}else{Object.DestroyImmediate(target);}
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Reset(){
            foreach(Material material in derived.Values){if(material!=null){DestroyObject(material);}}
            derived.Clear();virtualShaders.Clear();virtualPasses.Clear();bundledLoaded=false;
        }
    }
}
