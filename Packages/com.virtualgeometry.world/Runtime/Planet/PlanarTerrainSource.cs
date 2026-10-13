using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualGeometry.World{
    /// <summary>
    /// 平らな地面の Layer(高さ 0)。一辺 <see cref="Extent"/> の正方形の平面(局所座標の XZ、原点が正方形の中心、法線 +Y)を、
    /// 球と同じ Cell の四分木(<see cref="PlanetCellSelector.CreatePlane"/>)で分けて、頂点を実行時に生成する(PlaneTerrainGenerate.compute)。
    /// Cell の原点は Logical で正確に求め(<see cref="PlanetCellFrame.TryCreatePlane"/>)、頂点は Cell の原点からの相対位置なので、
    /// 平面が大きくても(Extent が 2^25 m でも)、Cell の中の頂点の精度は Cell の大きさに比例する。
    /// 選択・生成・退避・配置は <see cref="PlanetLayerSource"/>。先読み(<see cref="PlanetLayerSource.Prefetch"/>)は未対応。
    /// </summary>
    [ExecuteAlways]
    public sealed class PlanarTerrainSource:PlanetLayerSource{
        /// <summary>既定の一辺(2^25 m。float で quad の長さまで正確に表せる 2 のべき乗)。</summary>
        public const float DefaultExtent=33554432f;
        private static readonly int PlanetCellUpId=Shader.PropertyToID("_PlanetCellUp");

        [SerializeField,Min(1)] private float extent=DefaultExtent;

        /// <summary>平面の一辺(m)。</summary>
        public float Extent=>this.extent;
        /// <summary>球の半径の代わりに、平面の一辺の半分を返す(選択は <see cref="CreateSelector"/> で平面にする)。</summary>
        public override float Radius=>this.extent*0.5f;
        protected override float SelectionAmplitude=>0f;
        protected override string GeneratorResource=>"VirtualGeometryWorld/PlaneTerrainGenerate";

        /// <summary>形の設定を変える。有効なまま変えた場合は無効化して作り直す。</summary>
        public void Configure(float extent,int maximumLevel,float targetPixels){
            this.extent=extent;
            this.ConfigureSelection(maximumLevel,targetPixels);
            this.Rebuild();
        }

        protected override PlanetCellSelector CreateSelector(int maximumLevel,float skirtQuads){
            return PlanetCellSelector.CreatePlane(this.extent,0f,CellQuads,maximumLevel,skirtQuads);
        }

        // 球の値(原点の方向、R+H)ではなく、平面の法線(0,1,0)を渡す。
        protected override void WriteCellProperties(MaterialPropertyBlock block,int slot,in PlanetCellFrame frame){
            base.WriteCellProperties(block,slot,in frame);
            block.SetVector(PlanetCellUpId,new Vector4(0,1,0,0));
        }

        protected override void BindGeneration(CommandBuffer commands,ComputeShader shader,int kernel){
        }

        protected override void BindCell(CommandBuffer commands,ComputeShader shader,int slot,in PlanetCellFrame frame){
        }
    }
}
