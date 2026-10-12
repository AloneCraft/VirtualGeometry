using System.Collections.Generic;
using UnityEngine;
using VirtualizedGeometry;

namespace VirtualGeometry.VirtualObjects{
    /// <summary>
    /// 表現の設定や子の割り当ての規則が使うアセット(仮想オブジェクトの種類、プレハブ、材質)を申告する。
    /// 組み込みの設定と規則は実装済み。新しい表現の設定も、アセットを参照するなら実装する(描画の準備と Validator が使う)。
    /// </summary>
    public interface IVirtualObjectDependencySource{
        void CollectDependencies(VirtualObjectDependencies dependencies);
    }

    /// <summary>
    /// 仮想オブジェクトが使うアセットの一覧。種類(<see cref="VirtualObject"/>)から、帯の設定、子の型、割り当ての規則を再帰でたどる。
    /// 材質は、通常の Renderer のもの(<see cref="Materials"/>)と、VG の部品(<see cref="VirtualGeometryRenderer"/>)のもの(<see cref="VirtualGeometryMaterials"/>)に分ける
    /// (Omni の一括描画の準備は前者だけを要る。VG の部品は VG が描く)。
    /// </summary>
    public sealed class VirtualObjectDependencies{
        public HashSet<VirtualObject> Definitions{get;}=new HashSet<VirtualObject>();
        public HashSet<GameObject> Prefabs{get;}=new HashSet<GameObject>();
        /// <summary>通常の Renderer の材質と、表現が直接使う材質(ビルボードなど)。</summary>
        public HashSet<Material> Materials{get;}=new HashSet<Material>();
        /// <summary>VG の部品の Renderer の材質(VG が描く。通常の描画の準備は要らない)。</summary>
        public HashSet<Material> VirtualGeometryMaterials{get;}=new HashSet<Material>();

        /// <summary>配置の集まりの全部の実体の種類から集める。</summary>
        public static VirtualObjectDependencies FromSets(IEnumerable<VirtualObjectSet> sets){
            var result=new VirtualObjectDependencies();
            if(sets==null){return result;}
            foreach(VirtualObjectSet set in sets){
                if(set==null||set.Placements==null){continue;}
                foreach(VirtualObjectPlacementRecord record in set.Placements){result.AddDefinition(record.Prefab);}
            }
            return result;
        }

        /// <summary>種類と、その帯・子の型・割り当ての規則が使うものを足す(同じ種類は 1 回だけたどる)。</summary>
        public void AddDefinition(VirtualObject definition){
            if(definition==null||!this.Definitions.Add(definition)){return;}
            this.AddPrefab(definition.gameObject);
            if(definition.Bands!=null){
                foreach(RepresentationBand band in definition.Bands){
                    if(band==null){continue;}
                    if(band.Settings is IVirtualObjectDependencySource source){source.CollectDependencies(this);}
                }
            }
            if(definition.ChildTemplates!=null){
                foreach(VirtualObject template in definition.ChildTemplates){this.AddDefinition(template);}
            }
            if(definition.Assignment!=null){
                foreach(IChildAssignmentRule rule in definition.Assignment){
                    if(rule is IVirtualObjectDependencySource source){source.CollectDependencies(this);}
                }
            }
        }

        /// <summary>プレハブと、その中の Renderer の材質を足す。プレハブが仮想オブジェクトの種類なら、それもたどる。</summary>
        public void AddPrefab(GameObject prefab){
            if(prefab==null||!this.Prefabs.Add(prefab)){return;}
            foreach(Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true)){
                bool virtualGeometry=renderer.GetComponent<VirtualGeometryRenderer>()!=null;
                foreach(Material material in renderer.sharedMaterials){
                    if(material==null){continue;}
                    if(virtualGeometry){this.VirtualGeometryMaterials.Add(material);}else{this.Materials.Add(material);}
                }
            }
            VirtualObject definition=prefab.GetComponent<VirtualObject>();
            if(definition!=null){this.AddDefinition(definition);}
        }

        public void AddMaterial(Material material){
            if(material!=null){this.Materials.Add(material);}
        }
    }
}
