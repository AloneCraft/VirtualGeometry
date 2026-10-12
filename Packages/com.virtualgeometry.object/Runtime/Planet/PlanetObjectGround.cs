using Unity.Mathematics;
using UnityEngine;
using VirtualGeometry.World;
using LogicalScalar=Omni.Logical.Scalar;
using LogicalVector3=Omni.Logical.Vector3;

namespace VirtualGeometry.VirtualObjects{
    /// <summary>
    /// 惑星の地形を地面にする。ルートは惑星(<see cref="PlanetTerrainSource"/> と同じ GameObject)で、ルートの局所 = 惑星の局所。
    /// 地面の高さは <see cref="PlanetTerrainSource.TrySampleSurface(in LogicalVector3,out LogicalScalar,out LogicalScalar)"/>(Logical の精度)。
    /// 地形の準備は <see cref="PlanetLayerSource.Prefetch"/>(真下の Cell と周り 3×3 を、quad が radius 以下になる階層まで要求する)。
    /// </summary>
    public sealed class PlanetObjectGround:IVirtualObjectGround{
        private readonly PlanetTerrainSource terrain;

        public PlanetObjectGround(PlanetTerrainSource terrain){
            this.terrain=terrain;
        }

        public bool TryProject(in LogicalVector3 rootLocalPosition,out LogicalVector3 groundPoint,out Vector3 up){
            groundPoint=default;up=Vector3.up;
            if(this.terrain==null){return false;}
            if(!LogicalVector3.TryNormalize(rootLocalPosition,out LogicalVector3 direction)||!direction.TryToFloat3(out float3 upFloat)){return false;}
            up=(Vector3)upFloat;
            if(!this.terrain.TrySampleSurface(in rootLocalPosition,out LogicalScalar height,out LogicalScalar altitude)){return false;}
            // 点を、上向きに沿って高度の分だけ下げる(地面の点)。
            if(!LogicalVector3.TryScale(in direction,in altitude,out LogicalVector3 drop)||!LogicalVector3.TrySubtract(rootLocalPosition,drop,out groundPoint)){return false;}
            return true;
        }

        public bool IsReady(in LogicalVector3 rootLocalPosition,float radius){
            if(this.terrain==null){return true;}
            return this.terrain.Prefetch(in rootLocalPosition,Mathf.Max(radius,0.5f));
        }
    }
}
