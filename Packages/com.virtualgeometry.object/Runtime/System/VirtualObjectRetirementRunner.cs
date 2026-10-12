using UnityEngine;

namespace VirtualGeometry.VirtualObjects{
    /// <summary>Play 中に <see cref="VirtualObjectRetirement"/> の frame を数える、隠れた部品(システムが止まっても残る)。</summary>
    [AddComponentMenu("")]
    internal sealed class VirtualObjectRetirementRunner:MonoBehaviour{
        private void Update(){
            VirtualObjectRetirement.Tick();
        }

        private void OnDestroy(){
            VirtualObjectRetirement.OnRunnerDestroyed(this);
        }
    }
}
