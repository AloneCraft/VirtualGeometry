using System;
using System.Collections.Generic;
using System.Reflection;

namespace VirtualGeometry.VirtualObjects{
    /// <summary>
    /// 表現の種類の一覧。<see cref="VirtualObjectRepresentationAttribute"/> を付けたクラス(コンストラクタが (in RepresentationRequest))を id で作る。
    /// テストや実行時の差し替えは <see cref="Register"/> で行う(属性より優先)。
    /// </summary>
    public static class VirtualObjectRepresentations{
        /// <summary>何も描かない帯。</summary>
        public const string None="none";
        /// <summary>子のノードで見せる帯(ノード自身は描かず、番号ごとの子が表現を選ぶ)。</summary>
        public const string Children="children";

        private sealed class Entry{
            public Func<RepresentationRequest,IVirtualObjectRepresentation> Create;
            public Type SettingsType;
            public Type ImplementationType;
        }

        private static readonly Dictionary<string,Entry> registered=new Dictionary<string,Entry>();
        private static Dictionary<string,Entry> discovered;

        /// <summary>id の表現を登録する(属性で見つかるものより優先)。</summary>
        public static void Register(string id,Func<RepresentationRequest,IVirtualObjectRepresentation> create,Type settingsType=null){
            registered[id]=new Entry{Create=create,SettingsType=settingsType};
        }

        public static void Unregister(string id){
            registered.Remove(id);
        }

        /// <summary>id の表現があるか("none" と "children" は常にある)。</summary>
        public static bool Exists(string id){
            if(id==None||id==Children){return true;}
            return Find(id)!=null;
        }

        /// <summary>id の表現の設定の型(無ければ null)。</summary>
        public static Type SettingsType(string id){
            Entry entry=Find(id);
            if(entry==null){return null;}
            return entry.SettingsType;
        }

        /// <summary>見つかった表現の id の一覧(Editor の選択肢と Validator に使う)。</summary>
        public static IEnumerable<string> Ids(){
            EnsureDiscovered();
            var ids=new HashSet<string>{None,Children};
            foreach(string id in discovered.Keys){ids.Add(id);}
            foreach(string id in registered.Keys){ids.Add(id);}
            return ids;
        }

        public static IVirtualObjectRepresentation Create(in RepresentationRequest request){
            string id=request.Band.Representation;
            if(id==None){return new NoneRepresentation(in request);}
            Entry entry=Find(id);
            if(entry==null){throw new InvalidOperationException("表現 '"+id+"' が見つかりません。");}
            return entry.Create(request);
        }

        private static Entry Find(string id){
            if(id==null){return null;}
            if(registered.TryGetValue(id,out Entry entry)){return entry;}
            EnsureDiscovered();
            if(discovered.TryGetValue(id,out entry)){return entry;}
            return null;
        }

        private static void EnsureDiscovered(){
            if(discovered!=null){return;}
            discovered=new Dictionary<string,Entry>();
            foreach(Assembly assembly in UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies()){
                Type[] types;
                try{
                    types=assembly.GetTypes();
                }catch(ReflectionTypeLoadException error){
                    types=error.Types;
                }
                foreach(Type type in types){
                    if(type==null||type.IsAbstract){continue;}
                    var attribute=type.GetCustomAttribute<VirtualObjectRepresentationAttribute>();
                    if(attribute==null||!typeof(IVirtualObjectRepresentation).IsAssignableFrom(type)){continue;}
                    ConstructorInfo constructor=type.GetConstructor(new[]{typeof(RepresentationRequest).MakeByRefType()});
                    if(constructor==null){continue;}
                    discovered[attribute.Id]=new Entry{
                        Create=request=>(IVirtualObjectRepresentation)constructor.Invoke(new object[]{request}),
                        SettingsType=attribute.SettingsType,
                        ImplementationType=type,
                    };
                }
            }
        }
    }

    /// <summary>何も描かない表現(常に Ready、見えている扱い)。</summary>
    public sealed class NoneRepresentation:VirtualObjectRepresentationBase{
        public NoneRepresentation(in RepresentationRequest request):base(in request){
        }

        protected override void OnBeginPrepare(VirtualObjectContext context,System.Threading.CancellationToken cancellation){
        }

        protected override void OnActivate(VirtualObjectContext context){
        }

        protected override void OnDeactivate(VirtualObjectContext context){
        }

        protected override void OnRelease(VirtualObjectContext context){
        }
    }
}
