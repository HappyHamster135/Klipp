import {useEffect,useState} from 'react';
import type {Api} from '../lib/service';
import {messageFor,sizeLabel} from '../lib/service';
import type {CommunityUsage} from '../lib/models';
import {Notice} from './Primitives';

export function UsageDashboard({api,onBack,onManage,onManageClips}:{api:Api;onBack:()=>void;onManage:()=>void;onManageClips:()=>void}){
  const [usage,setUsage]=useState<CommunityUsage|null>(null),[error,setError]=useState(''),[retry,setRetry]=useState(0),[loading,setLoading]=useState(true);
  useEffect(()=>{
    const abort=new AbortController();setLoading(true);setError('');setUsage(null);
    void api.getUsage(abort.signal).then(value=>{if(!abort.signal.aborted)setUsage(value);}).catch(error=>{if(!abort.signal.aborted)setError(messageFor(error));}).finally(()=>{if(!abort.signal.aborted)setLoading(false);});
    return()=>abort.abort();
  },[api,retry]);
  const warning=usage&&usage.beta.slotsUsed/usage.beta.slotsLimit*100>=usage.limits.warningPercent;
  return <main className="profile-page operations-page"><button className="back" onClick={onBack}>Back to profile</button><h1>Storage &amp; usage</h1><p>Your shared clips and the beta’s current capacity.</p>
    {loading?<p role="status">Checking usage…</p>:null}{error?<Notice>{error}<button onClick={()=>setRetry(value=>value+1)}>Retry usage</button></Notice>:null}
    {usage?<><section className="account-tool-section"><h2>Your clips</h2><p><strong>{usage.me.slotsUsed} of {usage.me.slotsLimit}</strong> upload slots in use</p><progress aria-label="Your upload slots" value={usage.me.slotsUsed} max={usage.me.slotsLimit}/><p className="caption">{usage.me.published} shared · {usage.me.uploading} uploading · {usage.me.deleting} awaiting removal</p><p>{sizeLabel(usage.me.storedBytes)} stored</p><button onClick={onManageClips}>Manage shared clips</button></section>
      <section className="account-tool-section"><h2>Beta capacity</h2><p>{usage.beta.slotsUsed} of {usage.beta.slotsLimit} clip slots reserved</p><progress aria-label="Beta clip capacity" value={usage.beta.slotsUsed} max={usage.beta.slotsLimit}/><p>{sizeLabel(usage.beta.storageBytes)} of {sizeLabel(usage.beta.storageLimitBytes)} stored</p>{warning?<Notice>The beta is nearing its clip limit. Existing clips can still be watched.</Notice>:null}<p className="caption">Incomplete uploads keep their reserved slot until cleanup finishes. Checked {new Date(usage.checkedAt).toLocaleString()}.</p></section>
      {usage.operator&&usage.operations?<section className="account-tool-section"><h2>Administration</h2><p>{usage.operations.registeredUsers} accounts · {usage.operations.openReports} open reports · {usage.operations.openSupport} open requests</p><p>Database: {sizeLabel(usage.operations.databaseBytes)} of {sizeLabel(usage.operations.databaseLimitBytes)}</p><button onClick={onManage}>Review reports &amp; requests</button><p className="caption">Provider billing usage is measured separately from clip storage. Missing measurements are not treated as zero.</p></section>:null}</>:null}
  </main>;
}
