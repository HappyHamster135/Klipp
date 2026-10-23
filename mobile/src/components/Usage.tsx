import {useEffect,useState} from 'react';
import type {Api} from '../lib/service';
import {messageFor,sizeLabel} from '../lib/service';
import type {CommunityUsage,ProviderUsageMetric,ProviderUsageKey} from '../lib/models';
import {Notice} from './Primitives';

export function metricIsCurrent(metric:ProviderUsageMetric,now=Date.now()){
  return metric.used!==null&&metric.observedAt!==null&&metric.periodStart!==null&&metric.periodEnd!==null
    &&Date.parse(metric.periodStart)<=now&&Date.parse(metric.periodEnd)>now&&Date.parse(metric.observedAt)<=now+300000
    &&now-Date.parse(metric.observedAt)<=86400000;
}
const metrics:ReadonlyArray<[ProviderUsageKey,string,boolean]>=[['cachedEgress','Cached delivery',true],['uncachedEgress','Uncached delivery',true],['monthlyActiveUsers','Monthly active users',false],['emailDaily','Daily emails',false],['emailMonthly','Monthly emails',false]];

export function UsageDashboard({api,onBack,onManage,onManageClips}:{api:Api;onBack:()=>void;onManage:()=>void;onManageClips:()=>void}){
  const [usage,setUsage]=useState<CommunityUsage|null>(null),[error,setError]=useState(''),[retry,setRetry]=useState(0),[loading,setLoading]=useState(true);
  const [now,setNow]=useState(Date.now);
  useEffect(()=>{const timer=window.setInterval(()=>setNow(Date.now()),60000);return()=>window.clearInterval(timer);},[]);
  useEffect(()=>{
    const abort=new AbortController();setLoading(true);setError('');setUsage(null);
    void api.getUsage(abort.signal).then(value=>{if(!abort.signal.aborted)setUsage(value);}).catch(error=>{if(!abort.signal.aborted)setError(messageFor(error));}).finally(()=>{if(!abort.signal.aborted)setLoading(false);});
    return()=>abort.abort();
  },[api,retry]);
  const warning=usage&&(usage.beta.slotsUsed/usage.beta.slotsLimit*100>=usage.limits.warningPercent||usage.me.slotsUsed/usage.me.slotsLimit*100>=usage.limits.warningPercent||usage.beta.reservedBytes/usage.beta.storageLimitBytes*100>=usage.limits.warningPercent);
  const full=usage&&(usage.me.slotsUsed>=usage.me.slotsLimit||usage.beta.slotsUsed>=usage.beta.slotsLimit);
  return <main className="profile-page operations-page"><button className="back" onClick={onBack}>Back to profile</button><h1>Storage &amp; usage</h1><p>Your shared clips and the beta’s current capacity. Upload clips from Klipp on your PC.</p>
    {loading?<p role="status">Checking usage…</p>:null}{error?<Notice>{error}<button onClick={()=>setRetry(value=>value+1)}>Retry usage</button></Notice>:null}
    {usage?<><section className="account-tool-section"><h2>Your clips</h2><p><strong>{usage.me.slotsUsed} of {usage.me.slotsLimit}</strong> upload slots in use</p><progress aria-label="Your upload slots" value={usage.me.slotsUsed} max={usage.me.slotsLimit}/><p className="caption">{usage.me.published} shared · {usage.me.uploading} uploading · {usage.me.deleting} awaiting removal</p><p>{sizeLabel(usage.me.storedBytes)} stored</p><button onClick={onManageClips}>Manage shared clips</button></section>
      <section className="account-tool-section"><h2>Beta capacity</h2><p>{usage.beta.slotsUsed} of {usage.beta.slotsLimit} clip slots reserved</p><progress aria-label="Beta clip capacity" value={usage.beta.slotsUsed} max={usage.beta.slotsLimit}/><p>{sizeLabel(usage.beta.storageBytes)} tracked files · {sizeLabel(usage.beta.reservedBytes)} reserved</p><p className="caption">The {sizeLabel(usage.beta.storageLimitBytes)} media budget follows clip reservations. It is not a measurement of free space on the server’s SSD.</p>{usage.beta.homeBytes!==undefined&&usage.beta.legacyBytes!==undefined?<p className="caption">Home server: {sizeLabel(usage.beta.homeBytes)} · Legacy cloud clips: {sizeLabel(usage.beta.legacyBytes)}</p>:null}{warning?<Notice>{full?'The available upload slots are full. Delete one of your shared clips to free your own slot, or wait for beta capacity.':'The beta is nearing its clip limit.'} Existing clips can still be watched.</Notice>:null}<p className="caption">Incomplete uploads keep their reserved slot until cleanup finishes. Checked {new Date(usage.checkedAt).toLocaleString()}.</p></section>
      {usage.beta.unknownStorageObjects>0?<Notice>Some legacy file sizes are unavailable. Tracked media totals may be incomplete.</Notice>:null}
      {usage.operator&&usage.operations?<section className="account-tool-section"><h2>Administration</h2><p>{usage.operations.registeredUsers} accounts · {usage.operations.openReports} open reports · {usage.operations.openSupport} open requests</p><p>Database: {sizeLabel(usage.operations.databaseBytes)} of {sizeLabel(usage.operations.databaseLimitBytes)}</p><progress aria-label="Database capacity" value={usage.operations.databaseBytes} max={usage.operations.databaseLimitBytes}/>{usage.operations.databaseBytes/usage.operations.databaseLimitBytes*100>=usage.limits.warningPercent?<Notice>The database is nearing its free allowance. Review provider usage before inviting more members.</Notice>:null}<button onClick={onManage}>Review reports &amp; requests</button><h2 className="provider-heading">Provider allowances</h2><p className="caption">Provider billing usage is measured separately from clip storage. Missing or stale measurements are not treated as zero.</p>{metrics.map(([key,label,bytes])=>{
        const metric=usage.operations!.providerUsage[key],current=metricIsCurrent(metric,now),near=current&&metric.used!/metric.limit*100>=usage.limits.warningPercent;
        return <div className="provider-metric" key={key}><strong>{label}</strong><p>{current?`${bytes?sizeLabel(metric.used!):metric.used!.toLocaleString()} of ${bytes?sizeLabel(metric.limit):metric.limit.toLocaleString()}`:'Not measured recently'}</p>{current?<><progress aria-label={label} value={metric.used!} max={metric.limit}/><p className="caption">Measured {new Date(metric.observedAt!).toLocaleString()} · {metric.source}</p></>:<p className="caption">Check the provider dashboard before inviting more users.</p>}{near?<Notice>{label} is nearing its free allowance.</Notice>:null}</div>;
      })}</section>:null}<button onClick={()=>setRetry(value=>value+1)}>Refresh usage</button></>:null}
  </main>;
}
