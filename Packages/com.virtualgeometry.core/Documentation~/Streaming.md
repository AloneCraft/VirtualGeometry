# ページストリーミング（M4 / M5）

対象は Unity 6000.6.0f1 / URP 17.6 / Windows DX12。最粗表現を保持しながら詳細を要求する。検証の比較用に全常駐モードを残し、M3のFrustum/HZB切替を維持する。

## 変換形式と常駐データ

既定の階層変換はv3 archiveを生成する。VirtualGeometryMeshAssetはパス・ハッシュ・bounds・件数・容量と低密度Proxy Mesh / materialへの参照を保持し、高密度geometry配列はシリアライズしない。v1/v2は従来どおり全常駐で読み込める。GenerateStreamingPagesを無効にした階層変換はv2、階層なしもv2。

v3は96 bytesのheader、page descriptor、cluster bounds、階層node、material roots、独立payloadの順。descriptorはoffset / byte count / vertex count / index count / CRC / pinned flagを持つ。metadataと各payloadを別CRCで検証し、範囲・サイズ・有限値・page-local index・階層・cluster所有を検証する。

各nodeのrepresentation全体を1 pageへ置く。全materialのrootsはpage 0へまとめてpinする。archiveのpinned flagはpage 0だけで、streaming制御は読込時に階層から、rootから単一子の段を降りて低密度Proxyと同じ段(group束ねで1 nodeへまとまった段)に至るまでのpageも固定する。固定pageは起動時に最高優先度で要求し、未読込なら毎Tick要求し直し、timeout取消・退避の対象にしない。固定pageだけで予算を埋めると詳細を読めず粗いまま止まるため、1 nodeを細分化するのに要るslot(子のpageのslot数の和の最大、`RefinementReserveSlots`)を空け、残りにrootに近い段から入る分だけ固定する(`VirtualGeometryStreamingController.SelectPinnedPages`)。詳細payloadはpage-local vertices（32 bytes）とindices（4 bytes）。共有頂点はpage境界で複製されるので、unique geometry容量にはこの複製も含む。ReadMetadataはgeometryを読み込まない。ReadAllは全常駐比較・従来CPU API用に明示的に全payloadを復元する。

## GPU選択と固定予算

同じasset/content/互換streaming設定のRendererは、slot pool、metadata、page table、I/OとCPU cacheを参照共有する。選択状態・可視index・argsはinstance×cameraごとに分離し、最後のleaseで共有資源を解放する。

GPU poolは割り当て単位(slot)に区切る。1 slotの頂点数・添字数は、root以外のpageの頂点数・添字数の中央値(`SlotVertices`/`SlotIndices`、archiveには書かずdescriptorから決まる)。pageは頂点と添字が入る数だけ連続したslotを使う(`PageSlots`)。node representationの大きさは段ごとに増える(fanout 4・割合0.5では上の段ほど2倍)ため、最大のpageに合わせた固定枠では小さなpageまで大きな枠を使い、予算に入るpage数が大きく減る(Blenderで作った地形で、画面に入る1,225 pageの実データ約4.6 MBに対し、144 KBの枠で約177 MBが要った)。slot数はGPU geometry budgetをslot bytesで割り、全pageを置けるslot数を上限に切り捨てる。rootは先頭のslotに置く。rootのslotまたはroot decode予約を確保できない設定はロード拒否になる。予算の全slotを使っても置けないpageは読込失敗として報告する。

連続した空きが足りないときは、区間の中のpageがすべて退避してよい(固定でなく、常駐済みで、使用中でない)区間のうち、区間で最も最近使われたpageが最も古い区間を選び、その中のpageをすべて退避する。空きと退避中のslotだけで足りる区間があれば、退避の完了を待つ。

GPU page tableはvirtual page IDをphysical vertex/index offset、resident、generationへ解決する。選択computeは必要な全child pageがresidentであるときだけrefineし、欠けると親representationを描画して不足pageを要求する。compaction時にpage-local indexへslot vertex baseを加算する。転送途中のslotはresidentとして公開しない。

geometry予算は専用vertex/index poolへ適用する。GPU常駐metadataとpage tableは共有metadataとして、選択状態・可視index compaction・indirect args・要求feedback bufferはinstance×cameraのselection bytesとして別計上する。CPU metadata容量はserialized metadata bytesとして別計測する。Unity管理の低密度Proxy Mesh、driver allocation、material/textureなどはこのpool予算へ含まれず、全VRAM容量の上限ではない。benchmarkはプロセスworking setとUnity driver allocation estimateも別に記録する。

## 非同期要求・I/O・キャッシュ

不足要求は要求queue容量の領域へGPUが書き、GPU overflowを数える。利用pageのtouchはpageごとの(最大優先度, generation)配列へ書き、件数上限を持たない。touchが件数上限で欠けると、現在のcutが使うpageが未使用に見えて退避と再読込が続くため。slot再利用前に記録されたtouchはgenerationの不一致で捨てる。

page優先度は「そのpageを必要とする親nodeのscreen error」で統一する。refineを望むnodeは、不足している子pageを要求し、resident済みの子pageも同じ値でtouchする。rootを含むpageはrootのscreen errorでtouchする。階層の誤差は親≥子なので、現在のcutが依存する祖先側pageは子孫の要求より低い優先度にならない。nodeが自身のscreen errorで自分のpageをtouchすると、使用中の兄弟pageが別枝の孫要求より低く評価され、兄弟退避→親へ後退→再要求→孫退避の振動(近接時の点滅)が起きるため採用しない。

feedbackはinstance×cameraごとに別のframeで非同期に届く。CPUはpageごとに8 frame幅の窓で優先度の最大値を取り、直前の窓も含めた最大値を使う。1件ごとの上書きでは、同じpageを使う別cameraの高い優先度が消える。退避ではresident pageのうち、直近8 frame以内に要求/touchされ、かつ要求元以上の優先度を持つpageを保護する。feedbackは描画frameで刻印され数frame遅れて届くため、直前1 frameだけでは利用中pageまで古く見える。予算で子pageの組が揃わないnodeは、揃わないまま親を描画し続け、同じ優先度の部分集合同士で退避を繰り返さない。CPUはpage IDを重複排除し、優先度queue、Loading、Cached、Uploading、Resident、Retiring、Failed、Cancelledを管理する。queue overflowした要求は次frame以降のGPU選択から再要求される。失敗pageはFailedを保持し、修復後はRetryStreamingFailuresで再試行を許可する。

FileStreamの非同期ReadAsyncがdescriptorのoffset/sizeだけを読む。IoDelayMillisecondsは検証用遅延注入。I/O並列数、CPU cache bytes、要求queue上限、GPU geometry bytes、frame転送bytesを独立設定する。上限を自動拡大しない。

CPU予算はraw payloadとdecode後配列が同時存在するため、読み込み中に2×payload bytesを予約する。decode終了後は配列payloadの実bytesへ減らす。予約が単独でも上限を超えるdetailはFailedとなり、親を維持する。cacheはLRUで破棄する。進行中uploadと現在要求中のCached payloadを保持し、不要になった要求はtimeoutで取消す。partial uploadの取消は未公開slotとCPU payloadを返却する。DisposeはI/Oを取消し、終了まで予約を保持し、遅延faultも観測する。

これらのbytesはgeometry payloadと予約の論理容量であり、GC object header、Task、FileStream内部buffer、driver allocation、プロセスRSSの計測ではない。metadataと補助bufferを別計測し、payload予算と混同しない。

## frame転送とGPU寿命

1 pageが転送上限より大きくても、vertexは32 bytes、indexは4 bytes単位で複数frameへ分割する。最低転送設定は32 bytes。Time.frameCountが同じ間は転送残量を共有し、同frameの再描画で上限をリセットしない。全vertex/index完了後にのみpage tableとresidencyを公開する。

GPU不足要求のreadbackは描画後の同じgraphics command streamへCommandBuffer.RequestAsyncReadbackを記録する。同frameの同期readbackは通常経路では使用しない。feedback bufferとpending callback上限(2件)はinstance×cameraごとに持つ。共有pageに1つだけ持つと、後に描画するcamera(Editorでは多くの場合Scene view)の要求が恒常的に捨てられ、そのcameraだけ詳細化されない。submission番号はpage共有の通し番号のままとする。

退避時はpage tableを先にnonresidentへ変更する。次の選択が親へ戻り、その描画後に記録したreadbackの完了submission番号に達するまでslotをRetiringとして保護する。readback完了は、その前のdrawが完了したことの同期点となる。完了後にだけslotを再利用し、再利用したpageには新しいgeneration(通し番号)を付ける。単純な固定N frame待ちに依存しない。描画とfeedbackのRenderGraph依存を維持し、async computeや別queueへ移す場合はこの保証を再設計する必要がある。

初回の固定page(rootとProxyと同じ段まで)のasync load/upload完了前は低密度の通常Proxyを表示する。RootReady(固定pageがすべて公開済み)になったcamera描画から専用geometryへ原子的に切り替える。予算に余裕があれば切替直後もProxyと同じ段まで詳細化でき、Proxyより粗いrootへ見た目が後退しない。予算が小さく固定がProxyの段まで届かない場合は、切替直後に固定範囲の段(Proxyより粗い)で描き、詳細の到着に従って細かくなる。詳細未到着・破損・予算不足・evictionが起きても粗い表現を維持する。root準備失敗やarchive拒否は診断付きProxyとなる。

## 診断と比較

StreamingStatisticsは現在/peak geometry bytes、CPU payload/予約bytes、I/O bytes、各state件数、要求/再要求/dedup/miss/overflow/eviction/cancel、frame転送、quality target miss、submission/generationを公開する。QualityTargetMissesは直近GPU feedbackの不足pageによるrefinement未達件数。予算不足でも目標品質を達成したとは扱わない。

SetStreamingConfigurationでStreamingとAllResidentを切り替える。AllResidentは比較用に全geometryを確保し、streaming予算は適用しない。カリング切替は独立。ReadbackSelectionForDiagnosticsとEditorのdepth probeは検証専用の同期readbackであり、通常描画・要求制御には使用しない。

## 再現

Unityを1プロセスずつ、当該projectだけで実行する。GPU検証は-nographicsを付けず、executeMethodが非同期完了後にEditorApplication.Exitするため-quitも付けない。

```powershell
$u = Start-Process 'C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe' -WindowStyle Hidden -PassThru -ArgumentList '-batchmode -force-d3d12 -projectPath G:\Unity\6.6\VirtualGeometry -executeMethod VirtualizedGeometry.Editor.M4Validation.Run -logFile Logs/M4ValidationFinal.log'
$u.WaitForExit()
```

結果はTestResults/M4/Results.txt、Environment.txt、Metrics.csv、FaultMetrics.csv、PNGへ保存する。Metricsは通常240 frameについてdepth欠落、silhouette差、depth RMS、色差、各予算、generation/完了submissionを記録する。FaultMetricsは欠損range、CRC破損、終了中cancel、detail CPU予算不足を別記録する。検証用fault archiveはEditorの変換キャッシュ配下のLibrary/VirtualGeometry/M4Validationへ生成される(起動時の点検はこのサブフォルダを削除しない)。

M2/M3はAllResidentを明示し、v3からの復元と既存選択/cullingを回帰検証する。EditModeはv3原子性・legacy reader・header/CRC/extent・実FileStream I/O・queue優先度/overflow・chunk転送・取消/再要求・退避generationを確認する。
## 実測と証跡

M4は517 pages / unique geometry 1,012,080 bytes、root 928 bytes、slot 1,344 bytes(最大page 19,968 bytes)×108、GPU pool 145,152 bytes、transfer cap 2,048 bytes/frame、I/O cap 2で検証します。最初のcoarseは1,056 indices、観測した詳細化peakは4,224 indices、視点の移動で退避と再要求が起きます。最大pageの固定枠だった時は同じpeakに299,520 bytesのpoolが要りました。reference GPU geometryはstreaming計測中0 bytesです。

数値の正本はTestResults/M4のResults.txt、Environment、Metrics.csv、FaultMetrics.csv、および最新M5集約Verifyが更新するResults.jsonです。TestResults/M5/Verify.pyは全240 frameと異常系のbudget、depth欠落、silhouette、color、完了submissionとslot reuseを検算します。元のM4段階集計はM4BaselineResults.jsonへ保存します。M4/Verify.ps1はM4段階のログを参照するため、現在の全回帰集約にはM5/Verify.pyを使います。

M4の通常streaming画像では欠落・silhouette差・Unlit色差0を維持します。full-detailとのdepth差はLODによる品質差としてRMS/maxを記録し、0と偽りません。欠損range・CRC破損・detail CPU予算不足はrootを維持し、coarse基準とのdepth差0を確認します。終了中cancelはLoading/CPU予約を解放し、遅延callbackの例外を発生させません。

予算を低くしたこの検証は、粗い被覆を維持する安全性と部分的LOD改善を実証するものです。品質目標未達frame数、overflow、eviction、再要求は隠さず記録します。非同期I/Oとframe完了時刻によって件数はrun間で変動します。最終frameでrootへ退避することも正常な状態であり、詳細化は全観測区間のresident peakと実描画trianglesで判定します。

全EditModeはM5最終化時点34/34。M2/M3の選択・culling回帰、およびM5の2instance共有・root Proxy・Playerで同じ不変条件を確認しています。Unity起動時Licensing診断はアプリケーションのshader/C#/runtime warning/errorと分けて記録します。