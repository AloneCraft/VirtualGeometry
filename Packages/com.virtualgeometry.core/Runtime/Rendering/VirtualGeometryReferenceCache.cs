using System;
using System.Collections.Generic;

namespace VirtualizedGeometry{
    /// <summary>共有GPU資源の所有権を参照数とentry世代で管理し、reset後の古いleaseを無効化する。</summary>
    public sealed class VirtualGeometryReferenceCache<T>:IDisposable where T:class,IDisposable{
        internal sealed class Entry{public T Value;public int References;public bool Alive=true;}
        public sealed class Lease:IDisposable{
            private VirtualGeometryReferenceCache<T> owner;private Entry entry;private readonly string key;
            internal Lease(VirtualGeometryReferenceCache<T> owner,string key,Entry entry){this.owner=owner;this.key=key;this.entry=entry;}
            public bool IsValid=>this.owner!=null&&this.entry.Alive;
            public T Value=>this.IsValid?this.entry.Value:throw new ObjectDisposedException(nameof(Lease));
            public void Dispose(){
                if(this.owner==null){return;}
                var cache=this.owner;this.owner=null;
                if(!this.entry.Alive){return;}
                if(--this.entry.References==0){this.entry.Alive=false;cache.entries.Remove(this.key);this.entry.Value.Dispose();}
            }
        }
        private readonly Dictionary<string,Entry> entries=new Dictionary<string,Entry>(StringComparer.Ordinal);
        public int ResourceCount=>this.entries.Count;
        public Lease Acquire(string key,Func<T> factory){
            if(string.IsNullOrEmpty(key)){throw new ArgumentException("Resource key is required.",nameof(key));}
            if(!this.entries.TryGetValue(key,out Entry entry)){entry=new Entry{Value=factory()??throw new InvalidOperationException("Resource factory returned null.")};this.entries.Add(key,entry);}
            entry.References++;return new Lease(this,key,entry);
        }
        public void Reset(){foreach(Entry entry in this.entries.Values){entry.Alive=false;entry.Value.Dispose();}this.entries.Clear();}
        public void Dispose(){this.Reset();}
    }

    /// <summary>同じgeometryを共有しても、instanceとcameraの選択履歴は別keyで保持する。</summary>
    public readonly struct VirtualGeometrySelectionKey:IEquatable<VirtualGeometrySelectionKey>{
        public readonly int Instance;public readonly int Camera;
        public VirtualGeometrySelectionKey(int instance,int camera){this.Instance=instance;this.Camera=camera;}
        public bool Equals(VirtualGeometrySelectionKey other)=>this.Instance==other.Instance&&this.Camera==other.Camera;
        public override bool Equals(object value)=>value is VirtualGeometrySelectionKey other&&this.Equals(other);
        public override int GetHashCode()=>unchecked(this.Instance*397^this.Camera);
    }

    /// <summary>カメラ毎のfallback判断。専用geometryと通常Proxyの可視描画を排他的にする。</summary>
    public readonly struct VirtualGeometryRenderDecision{
        public readonly bool UseVirtual;public bool UseProxy=>!this.UseVirtual;public readonly string Reason;
        private VirtualGeometryRenderDecision(bool useVirtual,string reason){this.UseVirtual=useVirtual;this.Reason=reason;}
        public static VirtualGeometryRenderDecision Resolve(string unsupportedReason,bool rootReady,bool forceFallback,bool rejected){
            if(forceFallback){return new VirtualGeometryRenderDecision(false,"通常Proxy表示が選択されています。");}
            if(!string.IsNullOrEmpty(unsupportedReason)){return new VirtualGeometryRenderDecision(false,unsupportedReason);}
            if(rejected){return new VirtualGeometryRenderDecision(false,"Archiveを読み込めないためProxyを表示しています。");}
            if(!rootReady){return new VirtualGeometryRenderDecision(false,"Root pageを準備中です。");}
            return new VirtualGeometryRenderDecision(true,string.Empty);
        }
    }
}